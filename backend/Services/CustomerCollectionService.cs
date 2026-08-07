using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spectra.Api.Data;

namespace Spectra.Api.Services;

public record UserAliasDto(string Address, bool IsPrimary);

// Pulls the current user list (plus licenses and, if permitted, mailbox
// usage) from a customer's tenant via Graph and replaces whatever was
// previously stored for them, plus the EXO/SCC PowerShell and DNS collection
// tracks. A hard failure here (e.g. consent not granted yet) is recorded on
// the customer rather than thrown, so it can be retried later. This is what
// both the manual "Collect data" endpoints (Program.cs) and
// CustomerSyncBackgroundService's scheduled runs call into — one collection
// implementation, two triggers.
//
// Registered Scoped (it depends on the scoped SpectraDbContext) — the
// per-customer collection lock below has to be a genuinely shared singleton
// across every scope instead, hence CollectionLockRegistry rather than
// holding the lock table here.
public class CustomerCollectionService(
    SpectraDbContext db,
    GraphAppClient graphClient,
    ExoPowerShellClient exoClient,
    DnsCheckClient dnsClient,
    SccPowerShellClient sccClient,
    AzureResourceClient azureClient,
    CollectionLockRegistry lockRegistry,
    ILogger<CustomerCollectionService> logger)
{
    public async Task CollectAsync(Customer customer)
    {
        var gate = lockRegistry.GetLock(customer.Id);
        await gate.WaitAsync();
        try
        {
            var graphUsers = await graphClient.ListUsersAsync(customer.TenantId);
            var token = await graphClient.GetAppTokenAsync(customer.TenantId);

            var licensesByUserId = new Dictionary<string, List<GraphLicenseDto>>();
            await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (u, ct) =>
            {
                try
                {
                    var licenses = await graphClient.GetLicenseDetailsAsync(customer.TenantId, u.Id, token, ct);
                    lock (licensesByUserId) licensesByUserId[u.Id] = licenses;
                }
                catch (Exception ex)
                {
                    // Best-effort per user — one odd account (e.g. a resource
                    // mailbox Graph won't return licenseDetails for) shouldn't
                    // sink the rest of the tenant's collection.
                    logger.LogWarning(ex, "Failed to get license details for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
                }
            });

            var warnings = new List<string>();

            Dictionary<string, GraphMailboxUsageDto> mailboxByUpn = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                mailboxByUpn = await graphClient.GetMailboxUsageByUpnAsync(customer.TenantId, token);

                // Real rows came back, but none of them match any actual user in
                // this tenant — the signature of Microsoft's default report
                // concealment (see Customer.MailboxDataConcealed), not a
                // permission problem. Only evaluate this when there's at least
                // one real user to check against, so a brand-new/empty tenant
                // doesn't get misdiagnosed.
                customer.MailboxDataConcealed = mailboxByUpn.Count > 0
                    && graphUsers.Count > 0
                    && !graphUsers.Any(u => mailboxByUpn.ContainsKey(u.UserPrincipalName));
                if (customer.MailboxDataConcealed)
                {
                    // Same fix as the manual admin-center toggle, just done via Graph
                    // directly — no reason to make every customer's admin click
                    // through Settings by hand for something Spectra's own app
                    // permissions can already flip. Still takes a few minutes to
                    // propagate on Microsoft's side either way, so this run's
                    // mailbox data stays concealed regardless; the next collection
                    // picks up the change.
                    try
                    {
                        await graphClient.EnsureReportIdentitiesRevealedAsync(token);
                        warnings.Add("mailbox data unavailable — report identities were concealed; Spectra just disabled that setting automatically, it takes a few minutes to take effect, then re-collect");
                    }
                    catch (GraphPermissionDeniedException ex)
                    {
                        logger.LogWarning(ex, "ReportSettings.ReadWrite.All not granted for tenant {TenantId}", customer.TenantId);
                        warnings.Add("mailbox data unavailable — report identities are concealed (ReportSettings.ReadWrite.All isn't granted yet, so Spectra can't fix this automatically; Microsoft 365 admin center → Settings → Org Settings → Services → Reports → check \"Display Concealed user, group, and site names in all reports\" → Save works too, then re-collect)");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to disable report concealment for tenant {TenantId}", customer.TenantId);
                        warnings.Add($"mailbox data unavailable — report identities are concealed, and Spectra's automatic fix failed ({ex.Message}); Microsoft 365 admin center → Settings → Org Settings → Services → Reports → check \"Display Concealed user, group, and site names in all reports\" → Save works too, then re-collect");
                    }
                }
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "Reports.Read.All not yet effective for tenant {TenantId}", customer.TenantId);
                warnings.Add("mailbox data unavailable — Reports.Read.All isn't granted yet");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get mailbox usage for tenant {TenantId}", customer.TenantId);
                // Not a permission problem — don't tell the admin to re-consent when
                // that isn't the actual fix; show Graph's own explanation instead.
                warnings.Add($"mailbox data unavailable — {ex.Message}");
            }

            var mfaByUserId = new Dictionary<string, GraphMfaDto>();
            var mfaPermissionDenied = false;
            var mfaFailureCount = 0;
            // Lower concurrency than the license/inbox-rules loops below —
            // /authentication/methods has a noticeably tighter per-app Graph
            // throttle, and GraphRetryHandler already absorbs occasional 429s;
            // this just means fewer requests need retrying in the first place.
            await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (u, ct) =>
            {
                try
                {
                    var mfa = await graphClient.GetAuthenticationMethodsAsync(customer.TenantId, u.Id, token, ct);
                    lock (mfaByUserId) mfaByUserId[u.Id] = mfa;
                }
                catch (GraphPermissionDeniedException)
                {
                    mfaPermissionDenied = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to get authentication methods for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
                    Interlocked.Increment(ref mfaFailureCount);
                }
            });
            if (mfaPermissionDenied)
            {
                warnings.Add("MFA data unavailable — UserAuthenticationMethod.Read.All isn't granted yet");
            }
            else if (mfaFailureCount > 0)
            {
                warnings.Add($"MFA data unavailable for {mfaFailureCount} user(s) — see logs");
            }

            try
            {
                var policies = await graphClient.GetConditionalAccessPoliciesAsync(customer.TenantId, token);
                customer.ConditionalAccessPoliciesJson = JsonSerializer.Serialize(policies);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "Policy.Read.All not granted for tenant {TenantId}", customer.TenantId);
                warnings.Add("Conditional Access data unavailable — Policy.Read.All isn't granted yet");
                customer.ConditionalAccessPoliciesJson = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Conditional Access policies for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Conditional Access data unavailable — {ex.Message}");
                customer.ConditionalAccessPoliciesJson = null;
            }

            try
            {
                var globalAdmins = await graphClient.GetGlobalAdministratorsAsync(customer.TenantId, token);
                customer.GlobalAdminsJson = JsonSerializer.Serialize(globalAdmins);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "RoleManagement.ReadWrite.Directory not granted for tenant {TenantId}", customer.TenantId);
                warnings.Add("Global Administrator data unavailable — RoleManagement.ReadWrite.Directory isn't granted yet");
                customer.GlobalAdminsJson = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Global Administrators for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Global Administrator data unavailable — {ex.Message}");
                customer.GlobalAdminsJson = null;
            }

            try
            {
                customer.SecurityDefaultsEnabled = await graphClient.GetSecurityDefaultsEnabledAsync(customer.TenantId, token);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "Policy.Read.All not granted (Security Defaults) for tenant {TenantId}", customer.TenantId);
                customer.SecurityDefaultsEnabled = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Security Defaults status for tenant {TenantId}", customer.TenantId);
                customer.SecurityDefaultsEnabled = null;
            }

            // SPF/DMARC DNS checks — live public DNS lookups, not Graph or EXO,
            // so the only real failure mode here is "couldn't resolve the
            // verified domain list", not a missing permission.
            try
            {
                var verifiedDomains = await graphClient.GetVerifiedDomainsAsync(customer.TenantId, token);
                var domainChecks = await Task.WhenAll(verifiedDomains.Select(d => dnsClient.CheckDomainAsync(d)));
                customer.DnsRecordChecksJson = JsonSerializer.Serialize(domainChecks);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to run SPF/DMARC DNS checks for tenant {TenantId}", customer.TenantId);
                warnings.Add($"DNS record checks unavailable — {ex.Message}");
                customer.DnsRecordChecksJson = null;
            }

            try
            {
                var secureScore = await graphClient.GetSecureScoreAsync(customer.TenantId, token);
                customer.SecureScoreJson = secureScore is null ? null : JsonSerializer.Serialize(secureScore);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "SecurityEvents.Read.All not granted for tenant {TenantId}", customer.TenantId);
                warnings.Add("Secure Score unavailable — SecurityEvents.Read.All isn't granted yet");
                customer.SecureScoreJson = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Secure Score for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Secure Score unavailable — {ex.Message}");
                customer.SecureScoreJson = null;
            }

            try
            {
                var profiles = await graphClient.GetSecureScoreControlProfilesAsync(customer.TenantId, token);
                customer.SecureScoreControlProfilesJson = JsonSerializer.Serialize(profiles);
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "SecurityEvents.Read.All not granted (control profiles) for tenant {TenantId}", customer.TenantId);
                customer.SecureScoreControlProfilesJson = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Secure Score control profiles for tenant {TenantId}", customer.TenantId);
                customer.SecureScoreControlProfilesJson = null;
            }

            var inboxRulesByUserId = new Dictionary<string, List<GraphInboxRuleDto>>();
            var inboxRulesPermissionDenied = false;
            await Parallel.ForEachAsync(graphUsers, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (u, ct) =>
            {
                try
                {
                    var rules = await graphClient.GetInboxRulesAsync(customer.TenantId, u.Id, token, ct);
                    lock (inboxRulesByUserId) inboxRulesByUserId[u.Id] = rules;
                }
                catch (GraphPermissionDeniedException)
                {
                    inboxRulesPermissionDenied = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to get inbox rules for {UserId} in tenant {TenantId}", u.Id, customer.TenantId);
                }
            });
            if (inboxRulesPermissionDenied)
            {
                warnings.Add("inbox rule data unavailable — MailboxSettings.Read isn't granted yet");
            }

            // Exchange Online PowerShell data — a separate collection track from
            // everything above, needing its own access grant (Global Reader,
            // auto-assigned here) rather than Graph admin consent. Attempted on
            // every run until the role assignment succeeds, since it can lag
            // behind (or fail independently of) ConsentGranted.
            if (!customer.ExoRoleAssigned)
            {
                try
                {
                    customer.ExoRoleAssigned = await graphClient.EnsureGlobalReaderRoleAssignedAsync(customer.TenantId, token);
                }
                catch (GraphPermissionDeniedException ex)
                {
                    logger.LogWarning(ex, "RoleManagement.ReadWrite.Directory not granted for tenant {TenantId}", customer.TenantId);
                    warnings.Add("Exchange Online access setup unavailable — RoleManagement.ReadWrite.Directory isn't granted yet");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to assign Global Reader role for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Exchange Online role assignment failed — {ex.Message}");
                }
            }

            if (customer.ExoRoleAssigned)
            {
                string? organizationDomain = null;
                try
                {
                    organizationDomain = await graphClient.GetInitialDomainAsync(customer.TenantId, token)
                        ?? throw new InvalidOperationException("Couldn't resolve the tenant's initial domain.");

                    var exo = await exoClient.CollectAsync(organizationDomain);
                    customer.ExoOrganizationConfigJson = exo.OrganizationConfig is null ? null : JsonSerializer.Serialize(exo.OrganizationConfig);
                    customer.ExoAcceptedDomainsJson = exo.AcceptedDomains is null ? null : JsonSerializer.Serialize(exo.AcceptedDomains);
                    customer.ExoAntiPhishPoliciesJson = exo.AntiPhishPolicies is null ? null : JsonSerializer.Serialize(exo.AntiPhishPolicies);
                    customer.ExoSafeLinksPoliciesJson = exo.SafeLinksPolicies is null ? null : JsonSerializer.Serialize(exo.SafeLinksPolicies);
                    customer.ExoSafeAttachmentPoliciesJson = exo.SafeAttachmentPolicies is null ? null : JsonSerializer.Serialize(exo.SafeAttachmentPolicies);
                    customer.ExoHostedContentFilterPoliciesJson = exo.HostedContentFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedContentFilterPolicies);
                    customer.ExoHostedOutboundSpamFilterPoliciesJson = exo.HostedOutboundSpamFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedOutboundSpamFilterPolicies);
                    customer.ExoMalwareFilterPoliciesJson = exo.MalwareFilterPolicies is null ? null : JsonSerializer.Serialize(exo.MalwareFilterPolicies);
                    customer.ExoDkimSigningConfigsJson = exo.DkimSigningConfigs is null ? null : JsonSerializer.Serialize(exo.DkimSigningConfigs);
                    customer.ExoTransportRulesJson = exo.TransportRules is null ? null : JsonSerializer.Serialize(exo.TransportRules);
                    customer.ExoSharingPoliciesJson = exo.SharingPolicies is null ? null : JsonSerializer.Serialize(exo.SharingPolicies);
                    customer.ExoHostedConnectionFilterPoliciesJson = exo.HostedConnectionFilterPolicies is null ? null : JsonSerializer.Serialize(exo.HostedConnectionFilterPolicies);
                    customer.ExoAdminAuditLogConfigJson = exo.AdminAuditLogConfig is null ? null : JsonSerializer.Serialize(exo.AdminAuditLogConfig);
                    customer.ExoAtpPolicyForO365Json = exo.AtpPolicyForO365 is null ? null : JsonSerializer.Serialize(exo.AtpPolicyForO365);
                    customer.ExoRemoteDomainsJson = exo.RemoteDomains is null ? null : JsonSerializer.Serialize(exo.RemoteDomains);
                    customer.ExoMailboxAuditBypassJson = exo.MailboxAuditBypass is null ? null : JsonSerializer.Serialize(exo.MailboxAuditBypass);
                    customer.ExoMailboxForwardingJson = exo.MailboxForwarding is null ? null : JsonSerializer.Serialize(exo.MailboxForwarding);
                    customer.ExoMailboxPermissionsJson = exo.MailboxPermissions is null ? null : JsonSerializer.Serialize(exo.MailboxPermissions);
                    customer.ExoRecipientPermissionsJson = exo.RecipientPermissions is null ? null : JsonSerializer.Serialize(exo.RecipientPermissions);
                    customer.ExoLastCollectedAt = DateTimeOffset.UtcNow;
                    customer.ExoLastError = null;
                }
                catch (ExoAccessException ex)
                {
                    // Deliberately NOT nulling the 11 Exo*Json columns here (see
                    // the doc comment on Customer.ExoLastError) — EXO PowerShell
                    // is meaningfully flakier than a Graph HTTP call (external
                    // process, cert auth, role-propagation delay that can take
                    // ~15 minutes even right after a successful assignment
                    // above), and wiping previously-good check results on a
                    // transient failure would make a working integration look
                    // broken instead of just stale.
                    logger.LogWarning(ex, "Exchange Online access not yet effective for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Exchange Online checks unavailable — {ex.Message}");
                    customer.ExoLastError = ex.Message;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Exchange Online collection failed for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Exchange Online checks failed — {ex.Message}");
                    customer.ExoLastError = ex.Message;
                }

                // A separate PowerShell session (Connect-IPPSSession) from the EXO
                // one above — attempted independently as long as the tenant domain
                // resolved, regardless of whether the EXO collection itself
                // succeeded, since a failure in one session shouldn't block the
                // other from running.
                if (organizationDomain is not null)
                {
                    try
                    {
                        var scc = await sccClient.CollectAsync(organizationDomain);
                        customer.SccDlpPoliciesJson = scc.DlpPolicies is null ? null : JsonSerializer.Serialize(scc.DlpPolicies);
                        customer.SccRetentionPoliciesJson = scc.RetentionPolicies is null ? null : JsonSerializer.Serialize(scc.RetentionPolicies);
                        customer.SccAlertPoliciesJson = scc.AlertPolicies is null ? null : JsonSerializer.Serialize(scc.AlertPolicies);
                        customer.SccLastCollectedAt = DateTimeOffset.UtcNow;
                        customer.SccLastError = null;
                    }
                    catch (SccAccessException ex)
                    {
                        logger.LogWarning(ex, "Security & Compliance access not yet effective for tenant {TenantId}", customer.TenantId);
                        warnings.Add($"Security & Compliance checks unavailable — {ex.Message}");
                        customer.SccLastError = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Security & Compliance collection failed for tenant {TenantId}", customer.TenantId);
                        warnings.Add($"Security & Compliance checks failed — {ex.Message}");
                        customer.SccLastError = ex.Message;
                    }
                }
            }

            // Azure Resource Manager data — a separate collection track from
            // everything above, authorized by Azure RBAC role assignment
            // rather than Entra admin consent (see Customer.AzureSubscriptionsJson).
            // Attempted on every run since there's no "role assigned" flag
            // Spectra can set itself here (unlike ExoRoleAssigned, this grant
            // happens entirely outside the app).
            var vms = new List<AzureVirtualMachineDto>();
            try
            {
                var armToken = await azureClient.GetAppTokenAsync(customer.TenantId);
                var subscriptions = await azureClient.ListSubscriptionsAsync(armToken);
                customer.AzureSubscriptionsJson = JsonSerializer.Serialize(subscriptions);

                var appServices = new List<AzureAppServiceDto>();
                foreach (var sub in subscriptions)
                {
                    vms.AddRange(await azureClient.ListVirtualMachinesAsync(sub.Id, sub.DisplayName, armToken));
                    appServices.AddRange(await azureClient.ListAppServicesAsync(sub.Id, sub.DisplayName, armToken));
                }
                customer.AzureVirtualMachinesJson = JsonSerializer.Serialize(vms);
                customer.AzureAppServicesJson = JsonSerializer.Serialize(appServices);
                customer.AzureLastCollectedAt = DateTimeOffset.UtcNow;
                customer.AzureLastError = null;
            }
            catch (AzureAccessDeniedException ex)
            {
                logger.LogWarning(ex, "Azure RBAC Reader role not yet assigned for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Azure resource data unavailable — {ex.Message}");
                customer.AzureLastError = ex.Message;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to collect Azure resource data for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Azure resource data unavailable — {ex.Message}");
                customer.AzureLastError = ex.Message;
            }

            // Reservations need a separate, tenant-scoped RBAC role (Reservations
            // Reader) from the subscription-scoped Reader role above, so this is
            // attempted independently — a customer can easily have one grant but
            // not the other. Skipped entirely for a customer with no VMs, though —
            // reservations are an Azure-IaaS-specific concept (Reserved VM
            // Instances/savings plans), and most customers here are Microsoft
            // 365-cloud-only with no Azure infrastructure to reserve capacity for,
            // so there's no reason to prompt for (or fail on) a role grant that
            // wouldn't apply to them. If vms is empty because the collection above
            // failed rather than because the tenant genuinely has none, this stays
            // silent too — no evidence of a VM means no reservations warning,
            // consistent either way. Doesn't touch any previously-collected
            // AzureReservationsJson — a tenant that temporarily shows zero VMs
            // this run shouldn't have its last-known reservation data wiped.
            if (vms.Count > 0)
            {
                try
                {
                    var armToken = await azureClient.GetAppTokenAsync(customer.TenantId);
                    var reservations = await azureClient.ListReservationsAsync(armToken);
                    customer.AzureReservationsJson = JsonSerializer.Serialize(reservations);
                }
                catch (AzureAccessDeniedException ex)
                {
                    logger.LogWarning(ex, "Reservations Reader role not yet assigned for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Reservation data unavailable — {ex.Message}");
                    customer.AzureReservationsJson = null;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to collect reservation data for tenant {TenantId}", customer.TenantId);
                    warnings.Add($"Reservation data unavailable — {ex.Message}");
                    customer.AzureReservationsJson = null;
                }
            }

            // Entra Apps — Graph-sourced, needs the separate Application.Read.All
            // permission (see Customer.EntraAppRegistrationsJson).
            try
            {
                customer.EntraAppRegistrationsJson = JsonSerializer.Serialize(await graphClient.ListApplicationsAsync(customer.TenantId, token));
                customer.EntraServicePrincipalsJson = JsonSerializer.Serialize(await graphClient.ListServicePrincipalsAsync(customer.TenantId, token));
            }
            catch (GraphPermissionDeniedException ex)
            {
                logger.LogWarning(ex, "Application.Read.All not granted for tenant {TenantId}", customer.TenantId);
                warnings.Add("Entra app data unavailable — Application.Read.All isn't granted yet");
                customer.EntraAppRegistrationsJson = null;
                customer.EntraServicePrincipalsJson = null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Entra apps for tenant {TenantId}", customer.TenantId);
                warnings.Add($"Entra app data unavailable — {ex.Message}");
                customer.EntraAppRegistrationsJson = null;
                customer.EntraServicePrincipalsJson = null;
            }

            var existing = await db.CustomerUsers.Where(u => u.CustomerId == customer.Id).ToListAsync();
            db.CustomerUsers.RemoveRange(existing);

            var now = DateTimeOffset.UtcNow;
            db.CustomerUsers.AddRange(graphUsers.Select(u =>
            {
                var mailbox = mailboxByUpn.GetValueOrDefault(u.UserPrincipalName);
                var mfa = mfaByUserId.GetValueOrDefault(u.Id);
                return new CustomerUser
                {
                    CustomerId = customer.Id,
                    GraphUserId = u.Id,
                    DisplayName = u.DisplayName,
                    Mail = u.Mail,
                    UserPrincipalName = u.UserPrincipalName,
                    JobTitle = u.JobTitle,
                    Department = u.Department,
                    OfficeLocation = u.OfficeLocation,
                    AccountEnabled = u.AccountEnabled,
                    CreatedDateTime = u.CreatedDateTime,
                    MailboxSizeBytes = mailbox?.SizeBytes,
                    MailboxItemCount = mailbox?.ItemCount,
                    HasArchiveMailbox = mailbox?.HasArchive,
                    LicensesJson = licensesByUserId.TryGetValue(u.Id, out var licenses) ? SerializeLicenses(licenses) : null,
                    MfaJson = mfa is null ? null : JsonSerializer.Serialize(mfa),
                    AliasesJson = ParseAliases(u.ProxyAddresses),
                    InboxRulesJson = inboxRulesByUserId.TryGetValue(u.Id, out var inboxRules) ? JsonSerializer.Serialize(inboxRules) : null,
                    ForwardingRulesJson = inboxRulesByUserId.TryGetValue(u.Id, out var allRules)
                        ? JsonSerializer.Serialize(allRules
                            .Where(r => r.ForwardsTo.Count > 0)
                            .Select(r => new GraphForwardingRuleDto(r.Name, r.Enabled, r.ForwardsTo)))
                        : null,
                    SyncedAt = now,
                };
            }));

            customer.ConsentGranted = true;
            customer.LastSyncedAt = now;
            customer.LastSyncError = warnings.Count == 0
                ? null
                : $"Users and licenses synced, but some data is unavailable: {string.Join("; ", warnings)}.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to collect data for customer {CustomerId} ({TenantId})", customer.Id, customer.TenantId);
            customer.LastSyncError = ex.Message;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The per-customer lock above should make this unreachable in
            // practice — kept as a backstop so any other save failure still
            // surfaces as a normal API error instead of an unhandled 500.
            logger.LogError(ex, "Failed to save collected data for customer {CustomerId}", customer.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string SerializeLicenses(List<GraphLicenseDto> licenses) =>
        JsonSerializer.Serialize(licenses.Select(l => new { l.SkuId, l.SkuPartNumber }));

    // Graph's proxyAddresses entries look like "SMTP:primary@contoso.com"
    // (uppercase prefix) or "smtp:alias@contoso.com" (lowercase) — only the
    // SMTP-prefixed ones are actual mail addresses (other prefixes like
    // "X500:"/"SIP:" show up too and aren't aliases in any meaningful sense
    // for this list).
    private static string ParseAliases(List<string> proxyAddresses)
    {
        var aliases = proxyAddresses
            .Where(a => a.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
            .Select(a => new UserAliasDto(
                Address: a["SMTP:".Length..],
                IsPrimary: a.StartsWith("SMTP:", StringComparison.Ordinal)))
            .ToList();
        return JsonSerializer.Serialize(aliases);
    }
}
