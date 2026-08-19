import type { IPublicClientApplication } from "@azure/msal-browser";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { getApiScopes, directoryScopes, graphScopes } from "./authConfig";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";
const GRAPH_BASE_URL = "https://graph.microsoft.com/v1.0";

async function acquireToken(instance: IPublicClientApplication, scopes: string[]): Promise<string> {
  const account = instance.getActiveAccount();
  if (!account) {
    throw new Error("No active account — sign in first");
  }

  try {
    const result = await instance.acquireTokenSilent({ scopes, account });
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      const result = await instance.acquireTokenPopup({ scopes });
      return result.accessToken;
    }
    throw err;
  }
}

async function apiFetch<T>(instance: IPublicClientApplication, path: string, init?: RequestInit): Promise<T> {
  const token = await acquireToken(instance, getApiScopes());
  const response = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    headers: {
      ...(init?.headers ?? {}),
      Authorization: `Bearer ${token}`,
    },
  });
  if (!response.ok) {
    let message = `API call failed: ${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (typeof body?.error === "string") message = body.error;
    } catch {
      // Response wasn't JSON — stick with the generic message.
    }
    throw new Error(message);
  }
  return response.json();
}

export function callApi<T>(instance: IPublicClientApplication, path: string): Promise<T> {
  return apiFetch<T>(instance, path);
}

export interface SystemStatus {
  databaseHealthy: boolean;
  databaseError: string | null;
}

export function getSystemStatus(instance: IPublicClientApplication): Promise<SystemStatus> {
  return apiFetch<SystemStatus>(instance, "/api/system/status");
}

export function resetToSqlite(instance: IPublicClientApplication): Promise<{ activeProvider: string }> {
  return apiFetch(instance, "/api/system/reset-to-sqlite", { method: "POST" });
}

// Not tied to any customer — the EXO certificate is one app-wide file.
export interface ExoCertificateStatus {
  configured: boolean;
  expiresAt: string | null;
  daysRemaining: number | null;
  expiringSoon: boolean;
  error: string | null;
}

export function getExoCertificateStatus(instance: IPublicClientApplication): Promise<ExoCertificateStatus> {
  return apiFetch<ExoCertificateStatus>(instance, "/api/system/exo-certificate-status");
}

export interface MeResponse {
  name: string | null;
  email: string | null;
  isAdmin: boolean;
  adminDiagnostics: {
    groupsClaimCount: number;
    groupsOverageDetected: boolean;
  };
}

export function getMe(instance: IPublicClientApplication): Promise<MeResponse> {
  return apiFetch<MeResponse>(instance, "/api/me");
}

export interface SettingsResponse {
  adminGroupId: string | null;
  adminGroupDisplayName: string | null;
  updatedAt: string | null;
  updatedByEmail: string | null;
}

export function getSettings(instance: IPublicClientApplication): Promise<SettingsResponse> {
  return apiFetch<SettingsResponse>(instance, "/api/settings");
}

export function updateSettings(
  instance: IPublicClientApplication,
  body: { adminGroupId: string | null; adminGroupDisplayName: string | null },
): Promise<SettingsResponse> {
  return apiFetch<SettingsResponse>(instance, "/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

export interface Customer {
  id: number;
  name: string;
  tenantId: string;
  consentGranted: boolean;
  lastSyncedAt: string | null;
  lastSyncError: string | null;
  createdAt: string;
  createdByEmail: string;
}

export function getCustomers(instance: IPublicClientApplication): Promise<Customer[]> {
  return apiFetch<Customer[]>(instance, "/api/customers");
}

export function createCustomer(instance: IPublicClientApplication, name: string, tenantId: string): Promise<Customer> {
  return apiFetch<Customer>(instance, "/api/customers", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name, tenantId }),
  });
}

export function collectCustomerData(instance: IPublicClientApplication, customerId: number): Promise<Customer> {
  return apiFetch<Customer>(instance, `/api/customers/${customerId}/collect`, { method: "POST" });
}

export interface CollectionProgressLine {
  seq: number;
  at: string;
  message: string;
}

export interface CollectionProgressResponse {
  isRunning: boolean;
  lines: CollectionProgressLine[];
}

// Polled while a collectCustomerData() call above is in flight, to render a
// live terminal-style feed — see CollectionProgressTracker.cs. `after` is
// the highest seq already rendered, so each poll only returns new lines.
export function getCollectionProgress(
  instance: IPublicClientApplication,
  customerId: number,
  after: number,
): Promise<CollectionProgressResponse> {
  return apiFetch<CollectionProgressResponse>(instance, `/api/customers/${customerId}/collect/progress?after=${after}`);
}

export interface BulkSyncStatus {
  isRunning: boolean;
  completed: number;
  total: number;
  currentCustomerId: number | null;
  currentCustomerName: string | null;
}

// Starts CustomerCollectionService.CollectAllAsync in the background — the
// response comes back immediately (or 409 if a sync is already running),
// well before the sweep itself finishes. Settings polls getSyncAllStatus()
// to track it instead of waiting on this call.
export function syncAllCustomers(instance: IPublicClientApplication): Promise<{ started: boolean }> {
  return apiFetch<{ started: boolean }>(instance, "/api/customers/sync-all", { method: "POST" });
}

export function getSyncAllStatus(instance: IPublicClientApplication): Promise<BulkSyncStatus> {
  return apiFetch<BulkSyncStatus>(instance, "/api/customers/sync-all/status");
}

export function getConsentUrl(instance: IPublicClientApplication, customerId: number): Promise<{ consentUrl: string }> {
  return apiFetch(instance, `/api/customers/${customerId}/consent-url`);
}

// Azure RBAC has no admin-consent equivalent — this returns a ready-to-run az
// CLI command (scoped at the tenant's root management group, so it covers
// every current and future subscription) for the customer's Azure admin to
// run themselves, rather than a URL Spectra can open on its own.
export function getAzureRoleCommand(instance: IPublicClientApplication, customerId: number): Promise<{ command: string }> {
  return apiFetch(instance, `/api/customers/${customerId}/azure-role-command`);
}

// Minimal per-customer shape for the customer switcher — every signed-in
// user can see this, unlike the full admin Customers management list above.
export interface CustomerSummary {
  id: number;
  name: string;
  // True when the last mailbox usage collection came back with every row
  // keyed by an anonymized identifier — a tenant-wide Microsoft 365 report
  // privacy setting, not a missing Graph permission. Lets the Mailboxes
  // sub-tab (Users.tsx) show the actual fix instead of the generic
  // "needs Reports.Read.All" message.
  mailboxDataConcealed: boolean;
}

export function getCustomerSummaries(instance: IPublicClientApplication): Promise<CustomerSummary[]> {
  return apiFetch<CustomerSummary[]>(instance, "/api/customers/summary");
}

export interface CustomerUserMailbox {
  sizeBytes: number | null;
  itemCount: number | null;
  hasArchive: boolean | null;
}

export interface CustomerUserLicense {
  skuId: string;
  skuPartNumber: string;
  displayName: string;
}

export interface CustomerUserMfa {
  isMfaRegistered: boolean;
  isMfaCapable: boolean;
  methods: string[];
}

// From Graph's proxyAddresses (User.Read.All, already granted) — isPrimary
// reflects the uppercase "SMTP:" vs lowercase "smtp:" prefix Graph uses.
export interface UserAlias {
  address: string;
  isPrimary: boolean;
}

export interface CustomerUserForwardingRule {
  name: string;
  enabled: boolean;
  forwardsTo: string[];
}

export interface CustomerUserInboxRule {
  name: string;
  enabled: boolean;
  sequence: number;
  conditionTypes: string[];
  actionTypes: string[];
  forwardsTo: string[];
}

export interface CustomerUser {
  id: number;
  graphUserId: string;
  displayName: string | null;
  mail: string | null;
  userPrincipalName: string;
  jobTitle: string | null;
  department: string | null;
  officeLocation: string | null;
  accountEnabled: boolean;
  createdDateTime: string | null;
  syncedAt: string;
  mailbox: CustomerUserMailbox | null;
  licenses: CustomerUserLicense[];
  mfa: CustomerUserMfa | null;
  aliases: UserAlias[];
  forwardingRules: CustomerUserForwardingRule[];
  inboxRules: CustomerUserInboxRule[];
}

export function getCustomerUsers(instance: IPublicClientApplication, customerId: number): Promise<CustomerUser[]> {
  return apiFetch<CustomerUser[]>(instance, `/api/customers/${customerId}/users`);
}

export interface ConditionalAccessPolicy {
  displayName: string;
  state: string;
  createdDateTime: string | null;
  modifiedDateTime: string | null;
  includeUsers: string[];
  excludeUsers: string[];
  includeGroups: string[];
  excludeGroups: string[];
  includeRoles: string[];
  excludeRoles: string[];
  includeApplications: string[];
  excludeApplications: string[];
  clientAppTypes: string[];
  grantControlsOperator: string | null;
  builtInControls: string[];
}

export interface SecureScoreControl {
  id: string;
  title: string | null;
  category: string | null;
  achievedScore: number;
  maxScore: number;
  rank: string | null;
  tier: string | null;
  implementationCost: string | null;
  userImpact: string | null;
  actionType: string | null;
  remediation: string | null;
  remediationImpact: string | null;
  threats: string[];
}

export interface CustomerSecurityInfo {
  secureScore: { currentScore: number; maxScore: number; createdDateTime: string | null } | null;
  secureScoreControls: SecureScoreControl[];
  conditionalAccessPolicies: ConditionalAccessPolicy[];
}

export function getCustomerSecurity(instance: IPublicClientApplication, customerId: number): Promise<CustomerSecurityInfo> {
  return apiFetch<CustomerSecurityInfo>(instance, `/api/customers/${customerId}/security`);
}

export interface OrcaCheckResult {
  id: string;
  title: string;
  category: string;
  status: "pass" | "fail" | "info";
  currentValue: string;
  remediation: string;
}

// Mailbox-level auto-forwarding (the ForwardingAddress/ForwardingSmtpAddress
// mailbox property) — set via the Exchange admin center's Mail flow > Edit
// forwarding panel, or a user's own OWA "Forwarding" setting. A different
// mechanism from CustomerUserForwardingRule above (an inbox rule).
export interface ExoMailboxForwarding {
  userPrincipalName: string | null;
  forwardingAddress: string | null;
  forwardingSmtpAddress: string | null;
  deliverToMailboxAndForward: boolean | null;
}

// A mail flow ("transport") rule, configured tenant-wide in the Exchange
// admin center — distinct from a per-user inbox rule.
export interface ExoTransportRule {
  name: string | null;
  state: string | null;
  priority: number | null;
  description: string | null;
  setSCL: string | null;
  deleteMessage: boolean | null;
}

// Full Access (and other) delegate grants on a mailbox — who besides the
// owner can open it. NT AUTHORITY\SELF and inherited entries are already
// filtered out server-side, so every row here is a genuine, explicit grant.
export interface ExoMailboxPermission {
  identity: string | null;
  user: string | null;
  accessRights: string[] | null;
  deny: boolean | null;
}

// Send As grants — a different permission from mailbox access above: lets a
// delegate send mail that appears to come from the mailbox.
export interface ExoRecipientPermission {
  identity: string | null;
  trustee: string | null;
  accessRights: string[] | null;
}

// Exchange Online PowerShell data is a separate collection track from
// everything in CustomerSecurityInfo above — it needs its own one-time setup
// (Spectra's app being granted the Global Reader role in the customer's
// tenant) that's independent of, and can lag behind, Graph admin consent.
export interface EmailSecurityInfo {
  exoRoleAssigned: boolean;
  exoLastCollectedAt: string | null;
  exoLastError: string | null;
  checks: OrcaCheckResult[];
  mailboxForwarding: ExoMailboxForwarding[];
  transportRules: ExoTransportRule[];
  mailboxPermissions: ExoMailboxPermission[];
  recipientPermissions: ExoRecipientPermission[];
}

export function getCustomerEmailSecurity(instance: IPublicClientApplication, customerId: number): Promise<EmailSecurityInfo> {
  return apiFetch<EmailSecurityInfo>(instance, `/api/customers/${customerId}/email-security`);
}

// Identity/RBAC checks sourced from Graph, not Exchange Online PowerShell —
// unlike EmailSecurityInfo, there's no separate access-grant track here, so
// no exoRoleAssigned/exoLastCollectedAt-style setup fields: everything this
// needs is already covered by the app's existing Graph permission set.
export interface IdentitySecurityInfo {
  checks: OrcaCheckResult[];
}

export function getCustomerIdentitySecurity(instance: IPublicClientApplication, customerId: number): Promise<IdentitySecurityInfo> {
  return apiFetch<IdentitySecurityInfo>(instance, `/api/customers/${customerId}/identity-security`);
}

// SPF/DMARC checks sourced from live public DNS lookups, not Graph or
// Exchange Online PowerShell — same no-setup shape as IdentitySecurityInfo.
export interface DomainSecurityInfo {
  checks: OrcaCheckResult[];
}

export function getCustomerDomainSecurity(instance: IPublicClientApplication, customerId: number): Promise<DomainSecurityInfo> {
  return apiFetch<DomainSecurityInfo>(instance, `/api/customers/${customerId}/domain-security`);
}

// DLP/retention/alert policy checks sourced from Security & Compliance
// PowerShell (Connect-IPPSSession) — a sibling session to the Exchange
// Online PowerShell one behind EmailSecurityInfo, using the exact same
// certificate and Global Reader role, so it shares exoRoleAssigned as its
// setup-gate rather than having its own.
export interface ComplianceSecurityInfo {
  exoRoleAssigned: boolean;
  sccLastCollectedAt: string | null;
  sccLastError: string | null;
  checks: OrcaCheckResult[];
}

export function getCustomerComplianceSecurity(instance: IPublicClientApplication, customerId: number): Promise<ComplianceSecurityInfo> {
  return apiFetch<ComplianceSecurityInfo>(instance, `/api/customers/${customerId}/compliance-security`);
}

// Azure Resource Manager data — a separate collection track from everything
// above, authorized by Azure RBAC role assignment on the customer's
// subscription(s) rather than Entra admin consent. See the README for the
// exact Azure Portal steps; there's no Graph consent screen involved here.
export interface AzureSubscription {
  id: string;
  displayName: string;
  state: string;
}

export interface AzureVirtualMachine {
  subscriptionId: string;
  subscriptionName: string;
  resourceGroup: string;
  name: string;
  location: string;
  vmSize: string | null;
  osType: string | null;
  powerState: string | null;
}

export interface AzureAppService {
  subscriptionId: string;
  subscriptionName: string;
  resourceGroup: string;
  name: string;
  kind: string | null;
  state: string | null;
  defaultHostName: string | null;
  location: string;
}

// Reservations need a second, tenant-scoped RBAC role (Reservations Reader)
// separate from the subscription-scoped Reader role above — see README.
export interface AzureReservation {
  reservationOrderId: string;
  displayName: string | null;
  skuName: string | null;
  quantity: number;
  provisioningState: string | null;
  expiryDateTime: string | null;
  term: string | null;
  appliedScopeType: string | null;
}

// Entra App Registrations — Graph-sourced (needs Application.Read.All, a
// separate new permission from everything ARM-sourced above).
export interface EntraAppRegistration {
  id: string;
  appId: string;
  displayName: string | null;
  signInAudience: string | null;
  createdDateTime: string | null;
  soonestCredentialExpiry: string | null;
}

// Enterprise Applications — every service principal in the tenant, including
// third-party/gallery apps, not just ones registered here.
export interface EntraServicePrincipal {
  id: string;
  appId: string;
  displayName: string | null;
  servicePrincipalType: string | null;
  createdDateTime: string | null;
}

export interface AzureResourceInfo {
  azureLastCollectedAt: string | null;
  azureLastError: string | null;
  subscriptions: AzureSubscription[];
  virtualMachines: AzureVirtualMachine[];
  appServices: AzureAppService[];
  reservations: AzureReservation[];
  entraAppRegistrations: EntraAppRegistration[];
  entraServicePrincipals: EntraServicePrincipal[];
}

export function getCustomerAzureResources(instance: IPublicClientApplication, customerId: number): Promise<AzureResourceInfo> {
  return apiFetch<AzureResourceInfo>(instance, `/api/customers/${customerId}/azure`);
}

export interface SharePointSite {
  siteId: string | null;
  siteUrl: string;
  ownerDisplayName: string | null;
  ownerPrincipalName: string | null;
  rootWebTemplate: string | null;
  storageUsedBytes: number | null;
  storageAllocatedBytes: number | null;
  fileCount: number | null;
  activeFileCount: number | null;
  lastActivityDate: string | null;
}

export interface OneDriveUsage {
  displayName: string | null;
  userPrincipalName: string;
  oneDriveSiteUrl: string | null;
  oneDriveStorageUsedBytes: number | null;
  oneDriveStorageAllocatedBytes: number | null;
  oneDriveFileCount: number | null;
  oneDriveActiveFileCount: number | null;
  oneDriveLastActivityDate: string | null;
}

export interface SharePointInfo {
  sites: SharePointSite[];
  oneDrives: OneDriveUsage[];
}

// Sites and OneDrive usage, both sourced from the Reports API — see
// GetSharePointSiteUsageAsync/GetOneDriveUsageByUpnAsync in
// GraphAppClient.cs. No SharePoint admin-center tenant settings yet (that
// needs SharePoint/PnP PowerShell, a separate follow-up — see README).
export function getCustomerSharePoint(instance: IPublicClientApplication, customerId: number): Promise<SharePointInfo> {
  return apiFetch<SharePointInfo>(instance, `/api/customers/${customerId}/sharepoint`);
}

export interface TeamChannel {
  channelId: string | null;
  displayName: string;
  description: string | null;
  membershipType: string | null;
}

export interface TeamMember {
  displayName: string | null;
  email: string | null;
  roles: string[];
}

export interface Team {
  teamId: string;
  displayName: string;
  description: string | null;
  visibility: string | null;
  isArchived: boolean | null;
  channels: TeamChannel[];
  members: TeamMember[];
}

export interface TeamsActivityUsage {
  displayName: string | null;
  userPrincipalName: string;
  teamsChatMessageCount: number | null;
  teamsPrivateChatMessageCount: number | null;
  teamsCallCount: number | null;
  teamsMeetingCount: number | null;
  teamsLastActivityDate: string | null;
}

export interface TeamsInfo {
  teams: Team[];
  activity: TeamsActivityUsage[];
}

// Teams list (with channels + membership roster) and per-user Teams
// activity — see GraphAppClient.GetTeamsAsync/GetTeamsActivityByUpnAsync.
// Team.ReadBasic.All, Channel.ReadBasic.All, and TeamMember.Read.All are new
// permissions on top of everything else Spectra collects — existing
// customers need to re-grant consent before this returns data.
export function getCustomerTeams(instance: IPublicClientApplication, customerId: number): Promise<TeamsInfo> {
  return apiFetch<TeamsInfo>(instance, `/api/customers/${customerId}/teams`);
}

// Downloads the branded PDF snapshot of a customer's security posture. Can't
// go through apiFetch (it always parses the response as JSON) — fetches the
// PDF as a blob instead, then triggers a normal browser file download via a
// throwaway object URL/anchor, same trick used for any programmatic download.
// Shared by every "download a generated PDF" call — fetches as a blob (can't
// go through apiFetch, which always parses the response as JSON) and
// triggers a normal browser file download via a throwaway object URL/anchor.
async function downloadFile(instance: IPublicClientApplication, path: string, fileName: string): Promise<void> {
  const token = await acquireToken(instance, getApiScopes());
  const response = await fetch(`${API_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new Error(`Failed to generate report: ${response.status} ${response.statusText}`);
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

export function downloadCustomerReport(instance: IPublicClientApplication, customerId: number, customerName: string): Promise<void> {
  const dateStamp = new Date().toISOString().slice(0, 10);
  return downloadFile(instance, `/api/customers/${customerId}/report`, `${customerName} Security Report ${dateStamp}.pdf`);
}

export function downloadCustomerUserReport(instance: IPublicClientApplication, customerId: number, customerName: string): Promise<void> {
  const dateStamp = new Date().toISOString().slice(0, 10);
  return downloadFile(instance, `/api/customers/${customerId}/users-report`, `${customerName} User Report ${dateStamp}.pdf`);
}

// One Excel workbook covering every customer, one worksheet per customer,
// listing enabled users and their MFA registration — see
// MfaExportExcelGenerator.cs.
export function downloadMfaExport(instance: IPublicClientApplication): Promise<void> {
  const dateStamp = new Date().toISOString().slice(0, 10);
  return downloadFile(instance, "/api/customers/mfa-export", `MFA Export ${dateStamp}.xlsx`);
}

export type DatabaseType = "sqlserver" | "mysql";

export interface DatabaseStatus {
  activeProvider: "sqlite" | "sqlserver" | "mysql";
  configured: boolean;
  databaseType: DatabaseType | null;
  host: string | null;
  port: number | null;
  databaseName: string | null;
  username: string | null;
  isProvisioned: boolean;
  updatedAt: string | null;
  updatedByEmail: string | null;
}

export function getDatabaseStatus(instance: IPublicClientApplication): Promise<DatabaseStatus> {
  return apiFetch<DatabaseStatus>(instance, "/api/settings/database");
}

export interface SaveDatabaseConnectionResult {
  databaseType: DatabaseType;
  host: string;
  port: number;
  databaseName: string;
  username: string;
  isProvisioned: boolean;
  needsCreation: boolean;
}

export function saveDatabaseConnection(
  instance: IPublicClientApplication,
  body: { databaseType: DatabaseType; host: string; port: number; databaseName: string; username: string; password: string },
): Promise<SaveDatabaseConnectionResult> {
  return apiFetch<SaveDatabaseConnectionResult>(instance, "/api/settings/database", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

export function provisionDatabase(
  instance: IPublicClientApplication,
): Promise<{ success: boolean; activeProvider: string }> {
  return apiFetch(instance, "/api/settings/database/provision", { method: "POST" });
}

// Settings -> Authentication panel. Saving always restarts the backend (see
// AppUpdateService.RequestRestart's doc comment for why) — unlike the
// Database panel, there's no live-switch, so saveAzureAdConfig's result
// always has restartRequired: true.
export interface AzureAdStatus {
  configured: boolean;
  tenantId: string | null;
  frontendClientId: string | null;
  backendClientId: string | null;
  apiScope: string | null;
  hasSecret: boolean;
  updatedAt: string | null;
  updatedByEmail: string | null;
}

export function getAzureAdStatus(instance: IPublicClientApplication): Promise<AzureAdStatus> {
  return apiFetch<AzureAdStatus>(instance, "/api/settings/azure-ad");
}

export interface SaveAzureAdConfigResult {
  success: boolean;
  restartRequired: boolean;
  restartQueued: boolean;
  restartError: string | null;
}

export function saveAzureAdConfig(
  instance: IPublicClientApplication,
  body: {
    tenantId: string;
    frontendClientId: string;
    backendClientId: string;
    backendClientSecret: string; // blank = keep the existing one
    apiScope: string;
  },
): Promise<SaveAzureAdConfigResult> {
  return apiFetch<SaveAzureAdConfigResult>(instance, "/api/settings/azure-ad", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

// Settings -> Updates panel. Only meaningful on a server set up by
// deploy/install.sh — see the README's "One-click update" section. This
// endpoint never performs an update itself, only reports what a root-owned
// process (install.sh / update.sh) has recorded.
export interface AppVersionInfo {
  commit: string;
  ref: string;
  deployedAt: string;
}

export type UpdateState = "unavailable" | "idle" | "running" | "succeeded" | "failed";

export interface UpdateRunStatus {
  state: UpdateState;
  message: string | null;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface UpdateStatusResponse {
  currentVersion: AppVersionInfo | null;
  updateAvailable: boolean | null;
  latestCommit: string | null;
  status: UpdateRunStatus;
}

export function getUpdateStatus(instance: IPublicClientApplication): Promise<UpdateStatusResponse> {
  return apiFetch<UpdateStatusResponse>(instance, "/api/settings/update-status");
}

// Never performs the update inline — the backend only writes a request flag
// a separate systemd unit watches for. Resolves once the request is queued;
// throws (via apiFetch's non-2xx handling) if one's already in progress.
export function requestUpdate(instance: IPublicClientApplication): Promise<{ queued: boolean }> {
  return apiFetch(instance, "/api/settings/update", { method: "POST" });
}

// Object URL for the user's Microsoft profile photo, or null if they don't have one set
// (Graph returns 404 in that case — not an error, just "no photo").
export async function fetchProfilePhoto(instance: IPublicClientApplication): Promise<string | null> {
  const token = await acquireToken(instance, graphScopes);
  const response = await fetch(`${GRAPH_BASE_URL}/me/photo/$value`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    return null;
  }
  const blob = await response.blob();
  return URL.createObjectURL(blob);
}

export interface GraphGroup {
  id: string;
  displayName: string;
}

// Searches the tenant's security groups by name. First call of a session
// triggers a one-time consent popup for Group.Read.All (see authConfig.ts) —
// only admins opening Settings ever hit this path.
export async function searchGroups(instance: IPublicClientApplication, query: string): Promise<GraphGroup[]> {
  const trimmed = query.trim();
  if (!trimmed) {
    return [];
  }

  const token = await acquireToken(instance, directoryScopes);
  const escaped = trimmed.replace(/'/g, "''");
  const filter = encodeURIComponent(`startswith(displayName,'${escaped}')`);
  const response = await fetch(`${GRAPH_BASE_URL}/groups?$filter=${filter}&$select=id,displayName&$top=10`, {
    headers: { Authorization: `Bearer ${token}`, ConsistencyLevel: "eventual" },
  });
  if (!response.ok) {
    throw new Error(`Group search failed: ${response.status} ${response.statusText}`);
  }
  const data = await response.json();
  return (data.value ?? []) as GraphGroup[];
}

export interface DirectoryUser {
  id: string;
  displayName: string | null;
  mail: string | null;
  userPrincipalName: string;
  jobTitle: string | null;
  accountEnabled: boolean;
}

// Lists every user in the tenant. First call of a session triggers a one-time
// consent popup for User.Read.All (see authConfig.ts). Graph paginates at up
// to 999 results per page, so this follows @odata.nextLink until exhausted.
export async function listUsers(instance: IPublicClientApplication): Promise<DirectoryUser[]> {
  const token = await acquireToken(instance, directoryScopes);

  const users: DirectoryUser[] = [];
  let url: string | null =
    `${GRAPH_BASE_URL}/users?$select=id,displayName,mail,userPrincipalName,jobTitle,accountEnabled&$top=999`;

  while (url) {
    const response: Response = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
    if (!response.ok) {
      throw new Error(`Failed to list users: ${response.status} ${response.statusText}`);
    }
    const data = await response.json();
    users.push(...((data.value ?? []) as DirectoryUser[]));
    url = data["@odata.nextLink"] ?? null;
  }

  return users;
}
