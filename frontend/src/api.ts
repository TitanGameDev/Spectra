import type { IPublicClientApplication } from "@azure/msal-browser";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { apiScopes, directoryScopes, graphScopes } from "./authConfig";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;
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
  const token = await acquireToken(instance, apiScopes);
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

export function getConsentUrl(instance: IPublicClientApplication, customerId: number): Promise<{ consentUrl: string }> {
  return apiFetch(instance, `/api/customers/${customerId}/consent-url`);
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
