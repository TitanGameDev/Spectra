import { Fragment, useEffect, useState, type ReactNode } from "react";
import { useMsal } from "@azure/msal-react";
import {
  getCustomerUsers,
  getCustomerSecurity,
  getCustomerEmailSecurity,
  getCustomerIdentitySecurity,
  getCustomerDomainSecurity,
  getCustomerComplianceSecurity,
  downloadCustomerReport,
  getExoCertificateStatus,
  type CustomerUser,
  type CustomerUserInboxRule,
  type CustomerSecurityInfo,
  type ConditionalAccessPolicy,
  type SecureScoreControl,
  type EmailSecurityInfo,
  type IdentitySecurityInfo,
  type DomainSecurityInfo,
  type ComplianceSecurityInfo,
  type ExoTransportRule,
  type ExoMailboxPermission,
  type ExoRecipientPermission,
  type OrcaCheckResult,
  type ExoCertificateStatus,
} from "../api";
import { useCustomer } from "../CustomerContext";
import { SortableHeader, sortRows, toggleSort, type SortState, type SortValue } from "../sorting";

// Normalizes two different forwarding mechanisms into one row shape so they
// can share a single sortable table with a Type column distinguishing them —
// an inbox rule (Graph-sourced, CustomerUserForwardingRule) and mailbox-level
// auto-forwarding (EXO-sourced, ExoMailboxForwarding — a mailbox property set
// via the Exchange admin center or a user's own OWA setting, not a rule).
interface ForwardingTableRow {
  key: string;
  type: "Inbox rule" | "Mailbox forwarding";
  name: string;
  email: string;
  detail: string;
  enabled: boolean;
  target: string;
}

interface InboxRuleRow {
  user: CustomerUser;
  rule: CustomerUserInboxRule;
}

const SECURE_SCORE_ACCESSORS: Record<string, (c: SecureScoreControl) => SortValue> = {
  control: (c) => c.title ?? c.id,
  category: (c) => c.category ?? "",
  score: (c) => (c.maxScore > 0 ? c.achievedScore / c.maxScore : 1),
};

const MFA_ACCESSORS: Record<string, (u: CustomerUser) => SortValue> = {
  name: (u) => u.displayName ?? "",
  email: (u) => u.mail ?? u.userPrincipalName,
  registered: (u) => u.mfa?.isMfaRegistered ?? null,
  capable: (u) => u.mfa?.isMfaCapable ?? null,
  methods: (u) => u.mfa?.methods.length ?? 0,
};

const CA_ACCESSORS: Record<string, (p: ConditionalAccessPolicy) => SortValue> = {
  policy: (p) => p.displayName,
  state: (p) => p.state,
};

const FORWARDING_ACCESSORS: Record<string, (r: ForwardingTableRow) => SortValue> = {
  type: (r) => r.type,
  name: (r) => r.name,
  email: (r) => r.email,
  detail: (r) => r.detail,
  enabled: (r) => r.enabled,
  target: (r) => r.target,
};

const EXCHANGE_RULE_ACCESSORS: Record<string, (r: InboxRuleRow) => SortValue> = {
  name: (r) => r.user.displayName ?? "",
  email: (r) => r.user.mail ?? r.user.userPrincipalName,
  rule: (r) => r.rule.name,
  enabled: (r) => r.rule.enabled,
  conditions: (r) => r.rule.conditionTypes.length,
  actions: (r) => r.rule.actionTypes.length,
};

const MAIL_FLOW_RULE_ACCESSORS: Record<string, (r: ExoTransportRule) => SortValue> = {
  name: (r) => r.name ?? "",
  state: (r) => r.state ?? "",
  priority: (r) => r.priority ?? Number.MAX_SAFE_INTEGER,
  deleteMessage: (r) => r.deleteMessage ?? false,
};

const MAILBOX_PERMISSION_ACCESSORS: Record<string, (p: ExoMailboxPermission) => SortValue> = {
  mailbox: (p) => p.identity ?? "",
  user: (p) => p.user ?? "",
  accessRights: (p) => (p.accessRights ?? []).join(", "),
  deny: (p) => p.deny ?? false,
};

const RECIPIENT_PERMISSION_ACCESSORS: Record<string, (p: ExoRecipientPermission) => SortValue> = {
  mailbox: (p) => p.identity ?? "",
  trustee: (p) => p.trustee ?? "",
  accessRights: (p) => (p.accessRights ?? []).join(", "),
};

// Shared by every OrcaCheckResult-shaped table (Email Security, Identity).
const CHECK_ACCESSORS: Record<string, (c: OrcaCheckResult) => SortValue> = {
  check: (c) => c.title,
  category: (c) => c.category,
  status: (c) => c.status,
};

const SUB_TABS = [
  { key: "overview", label: "Overview" },
  { key: "secure-score", label: "Secure Score" },
  { key: "mfa", label: "MFA" },
  { key: "conditional-access", label: "Conditional Access" },
  { key: "forwarding", label: "Forwarding Rules" },
  { key: "exchange-rules", label: "Exchange Rules" },
  { key: "mail-flow-rules", label: "Mail Flow Rules" },
  { key: "mailbox-access", label: "Mailbox Access" },
  { key: "email-security", label: "Email Security" },
  { key: "identity", label: "Identity" },
  { key: "domains", label: "Domains" },
  { key: "compliance", label: "Compliance" },
] as const;

type SubTabKey = (typeof SUB_TABS)[number]["key"];

function StatTile({ label, value, caption }: { label: string; value: string; caption: string }) {
  return (
    <div className="stat-tile">
      <span className="stat-tile-label">{label}</span>
      <span className="stat-tile-value">{value}</span>
      <span className="stat-tile-caption">{caption}</span>
    </div>
  );
}

const CHECK_STATUS_DOT: Record<OrcaCheckResult["status"], string> = {
  pass: "connected",
  fail: "error",
  info: "checking",
};

const CHECK_STATUS_LABEL: Record<OrcaCheckResult["status"], string> = {
  pass: "Pass",
  fail: "Fail",
  info: "Info",
};

function policyStateLabel(state: string): string {
  if (state === "enabled") return "Enabled";
  if (state === "disabled") return "Disabled";
  if (state === "enabledForReportingButNotEnforced") return "Report-only";
  return state;
}

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const TARGET_LABELS: Record<string, string> = {
  All: "All users",
  None: "None",
  GuestsOrExternalUsers: "Guests or external users",
};

const APP_LABELS: Record<string, string> = {
  All: "All cloud apps",
  None: "None",
  Office365: "Office 365",
  MicrosoftAdminPortals: "Microsoft Admin Portals",
};

const CONTROL_LABELS: Record<string, string> = {
  mfa: "Multi-factor authentication",
  compliantDevice: "Compliant device",
  domainJoinedDevice: "Hybrid Azure AD joined device",
  approvedApplication: "Approved client app",
  compliantApplication: "App protection policy",
  passwordChange: "Password change",
  block: "Block access",
  passwordlessMfa: "Passwordless MFA",
};

const CLIENT_APP_LABELS: Record<string, string> = {
  all: "All client apps",
  browser: "Browser",
  mobileAppsAndDesktopClients: "Mobile apps and desktop clients",
  exchangeActiveSync: "Exchange ActiveSync clients",
  easSupported: "EAS supported clients",
  other: "Other clients",
};

// Inbox rule condition/action types are Graph's raw camelCase property names
// (e.g. "subjectContains") — the common ones get a proper label, anything
// else falls back to a readable split rather than an exhaustive hardcoded
// list of every condition/action Graph supports.
const RULE_TYPE_LABELS: Record<string, string> = {
  subjectContains: "Subject contains",
  bodyContains: "Body contains",
  bodyOrSubjectContains: "Body or subject contains",
  senderContains: "Sender contains",
  recipientContains: "Recipient contains",
  fromAddresses: "From specific senders",
  sentToAddresses: "Sent to specific recipients",
  hasAttachments: "Has attachments",
  isAutomaticForward: "Is automatic forward",
  isAutomaticReply: "Is automatic reply",
  sentOnlyToMe: "Sent only to me",
  sentToMe: "Sent to me",
  sentCcMe: "Sent CC to me",
  sentToOrCcMe: "Sent to or CC me",
  notSentToMe: "Not sent to me",
  importance: "Importance",
  categories: "Categories",
  forwardTo: "Forward to",
  forwardAsAttachmentTo: "Forward as attachment to",
  redirectTo: "Redirect to",
  delete: "Delete",
  permanentDelete: "Permanently delete",
  markAsRead: "Mark as read",
  markImportance: "Mark importance",
  moveToFolder: "Move to folder",
  copyToFolder: "Copy to folder",
  assignCategories: "Assign categories",
  stopProcessingRules: "Stop processing more rules",
};

function humanizeRuleType(type: string): string {
  if (RULE_TYPE_LABELS[type]) return RULE_TYPE_LABELS[type];
  const spaced = type.replace(/([a-z])([A-Z])/g, "$1 $2");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

// Secure Score's remediation text comes from Graph as a snippet of raw HTML
// (paragraphs, bold text, links, and numbered/bulleted steps like
// "<ol><li>...</li></ol>"). Deliberately not using dangerouslySetInnerHTML
// for content sourced from an external API — instead this parses the markup
// with DOMParser (which does not execute scripts or load resources) purely
// to read its tree structure, then walks that tree rebuilding only a small
// allow-listed set of elements as real React elements. Anything not in the
// allow list (and any attribute other than a validated https:// href) is
// dropped; its text content still passes through, but stripped of its
// wrapper. All text/attribute values still go through React's normal
// auto-escaping since they're passed as props/children, never as raw HTML.
function renderHtmlNode(node: ChildNode, key: number): ReactNode {
  if (node.nodeType === Node.TEXT_NODE) {
    return node.textContent;
  }
  if (node.nodeType !== Node.ELEMENT_NODE) {
    return null;
  }
  const el = node as HTMLElement;
  const children = [...el.childNodes].map((child, i) => renderHtmlNode(child, i));
  switch (el.tagName.toLowerCase()) {
    case "a": {
      const href = el.getAttribute("href");
      if (href && /^https:\/\//i.test(href)) {
        return (
          <a key={key} href={href} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        );
      }
      return children;
    }
    case "b":
    case "strong":
      return <strong key={key}>{children}</strong>;
    case "i":
    case "em":
      return <em key={key}>{children}</em>;
    case "ol":
      return (
        <ol key={key} className="remediation-list">
          {children}
        </ol>
      );
    case "ul":
      return (
        <ul key={key} className="remediation-list">
          {children}
        </ul>
      );
    case "li":
      return <li key={key}>{children}</li>;
    case "p":
      return <p key={key}>{children}</p>;
    case "br":
      return <br key={key} />;
    default:
      return <Fragment key={key}>{children}</Fragment>;
  }
}

function renderRemediation(html: string): ReactNode {
  const doc = new DOMParser().parseFromString(html, "text/html");
  return [...doc.body.childNodes].map((node, i) => renderHtmlNode(node, i));
}

// Shared table for any OrcaCheckResult list (Email Security, Identity) —
// sortable Check/Category/Status columns, click a row to expand its current
// value and remediation guidance. Extracted once a second check-table tab
// (Identity) needed the exact same rendering as Email Security's.
function ChecksTable({
  checks,
  sort,
  onSort,
  expandedId,
  onToggleExpand,
}: {
  checks: OrcaCheckResult[];
  sort: SortState<string> | null;
  onSort: (key: string) => void;
  expandedId: string | null;
  onToggleExpand: (id: string) => void;
}) {
  return (
    <div className="data-table-wrap">
      <table className="data-table">
        <thead>
          <tr>
            <th></th>
            <SortableHeader label="Check" columnKey="check" sort={sort} onSort={onSort} />
            <SortableHeader label="Category" columnKey="category" sort={sort} onSort={onSort} />
            <SortableHeader label="Status" columnKey="status" sort={sort} onSort={onSort} />
          </tr>
        </thead>
        <tbody>
          {sortRows(checks, sort, CHECK_ACCESSORS).map((check) => {
            const isExpanded = expandedId === check.id;
            return (
              <Fragment key={check.id}>
                <tr className="data-table-row-clickable" onClick={() => onToggleExpand(check.id)}>
                  <td className="ca-policy-chevron">{isExpanded ? "▾" : "▸"}</td>
                  <td>{check.title}</td>
                  <td>{check.category}</td>
                  <td>
                    <span className={`status-dot status-dot-${CHECK_STATUS_DOT[check.status]}`} />
                    {CHECK_STATUS_LABEL[check.status]}
                  </td>
                </tr>
                {isExpanded && (
                  <tr>
                    <td colSpan={4}>
                      <div className="ca-policy-detail">
                        <div className="ca-policy-detail-section">
                          <h3>Current value</h3>
                          <p>{check.currentValue}</p>
                        </div>
                        <div className="ca-policy-detail-section">
                          <h3>Remediation</h3>
                          <div className="remediation-content">{renderRemediation(check.remediation)}</div>
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

// Group/user/role targets come back from Graph as raw object IDs, which
// Spectra doesn't have permission to resolve to names (would need
// Group.Read.All on top of everything else already granted) — special values
// like "All" are shown directly, GUIDs are just counted rather than listed raw.
function formatTargetList(values: string[] | null | undefined, labels: Record<string, string>): string {
  if (!values || values.length === 0) return "None";
  const named = values.filter((v) => !GUID_RE.test(v)).map((v) => labels[v] ?? v);
  const idCount = values.length - named.length;
  const parts = [...named];
  if (idCount > 0) parts.push(`${idCount} specific ${idCount === 1 ? "entry" : "entries"} (ID only)`);
  return parts.join(", ");
}

export default function Security() {
  const { instance } = useMsal();
  const { customers, selectedCustomerId, loading: customersLoading } = useCustomer();
  const [users, setUsers] = useState<CustomerUser[] | null>(null);
  const [security, setSecurity] = useState<CustomerSecurityInfo | null>(null);
  const [emailSecurity, setEmailSecurity] = useState<EmailSecurityInfo | null>(null);
  const [identitySecurity, setIdentitySecurity] = useState<IdentitySecurityInfo | null>(null);
  const [domainSecurity, setDomainSecurity] = useState<DomainSecurityInfo | null>(null);
  const [complianceSecurity, setComplianceSecurity] = useState<ComplianceSecurityInfo | null>(null);
  const [certStatus, setCertStatus] = useState<ExoCertificateStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [downloadingReport, setDownloadingReport] = useState(false);
  const [reportError, setReportError] = useState<string | null>(null);
  const [subTab, setSubTab] = useState<SubTabKey>("overview");
  const [expandedPolicy, setExpandedPolicy] = useState<string | null>(null);
  const [expandedControl, setExpandedControl] = useState<string | null>(null);
  const [expandedCheck, setExpandedCheck] = useState<string | null>(null);
  const [expandedIdentityCheck, setExpandedIdentityCheck] = useState<string | null>(null);
  const [expandedDomainCheck, setExpandedDomainCheck] = useState<string | null>(null);
  const [expandedComplianceCheck, setExpandedComplianceCheck] = useState<string | null>(null);
  const [sort, setSort] = useState<SortState<string> | null>(null);

  const selectedCustomer = customers.find((c) => c.id === selectedCustomerId);

  // Not customer-scoped — the EXO certificate is one app-wide file, so this
  // is fetched once regardless of which customer is selected.
  useEffect(() => {
    let cancelled = false;
    getExoCertificateStatus(instance)
      .then((data) => {
        if (!cancelled) setCertStatus(data);
      })
      .catch(() => {
        // Non-critical — the Email Security empty/error states already
        // communicate collection problems; silently skip the banner rather
        // than surface a second, separate error for this.
      });
    return () => {
      cancelled = true;
    };
  }, [instance]);

  useEffect(() => {
    if (!selectedCustomerId) {
      setUsers(null);
      setSecurity(null);
      setEmailSecurity(null);
      setIdentitySecurity(null);
      setDomainSecurity(null);
      setComplianceSecurity(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([
      getCustomerUsers(instance, selectedCustomerId),
      getCustomerSecurity(instance, selectedCustomerId),
      getCustomerEmailSecurity(instance, selectedCustomerId),
      getCustomerIdentitySecurity(instance, selectedCustomerId),
      getCustomerDomainSecurity(instance, selectedCustomerId),
      getCustomerComplianceSecurity(instance, selectedCustomerId),
    ])
      .then(([usersData, securityData, emailSecurityData, identitySecurityData, domainSecurityData, complianceSecurityData]) => {
        if (!cancelled) {
          setUsers(usersData);
          setSecurity(securityData);
          setEmailSecurity(emailSecurityData);
          setIdentitySecurity(identitySecurityData);
          setDomainSecurity(domainSecurityData);
          setComplianceSecurity(complianceSecurityData);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load security data");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance, selectedCustomerId]);

  // A disabled or unlicensed account skews MFA coverage — same reasoning as
  // the PDF security report already excluding disabled accounts (see
  // SecurityReportPdfGenerator) — so the MFA sub-tab (and the Overview stat
  // tile fed by these same two values) only counts accounts that are both
  // enabled and actually assigned at least one license.
  const mfaEligibleUsers = users?.filter((u) => u.accountEnabled && u.licenses.length > 0) ?? [];
  const hasAnyMfaData = mfaEligibleUsers.some((u) => u.mfa !== null);
  const mfaRegisteredCount = mfaEligibleUsers.filter((u) => u.mfa?.isMfaRegistered).length;
  const usersWithForwarding = users?.filter((u) => u.forwardingRules.length > 0) ?? [];
  const caPolicies = security?.conditionalAccessPolicies ?? [];
  const enabledCaCount = caPolicies.filter((p) => p.state === "enabled").length;
  const secureScoreControls = security?.secureScoreControls ?? [];
  const inboxRuleForwardingRows: ForwardingTableRow[] = usersWithForwarding.flatMap((user) =>
    user.forwardingRules.map((rule) => ({
      key: `rule-${user.id}-${rule.name}`,
      type: "Inbox rule" as const,
      name: user.displayName ?? "—",
      email: user.mail ?? user.userPrincipalName,
      detail: rule.name,
      enabled: rule.enabled,
      target: rule.forwardsTo.join(", "),
    })),
  );
  const mailboxForwardingRows: ForwardingTableRow[] = (emailSecurity?.mailboxForwarding ?? []).map((f) => ({
    key: `mbx-${f.userPrincipalName}`,
    type: "Mailbox forwarding" as const,
    name: f.userPrincipalName ?? "—",
    email: f.userPrincipalName ?? "—",
    detail: f.deliverToMailboxAndForward ? "Keeps a copy in the mailbox" : "Forwards only, no copy kept",
    enabled: true,
    target: f.forwardingSmtpAddress ?? f.forwardingAddress ?? "—",
  }));
  const forwardingRows: ForwardingTableRow[] = [...inboxRuleForwardingRows, ...mailboxForwardingRows];
  const exchangeRuleRows: InboxRuleRow[] = users?.flatMap((user) => user.inboxRules.map((rule) => ({ user, rule }))) ?? [];
  const handleSort = (key: string) => setSort((prev) => toggleSort(prev, key));

  const handleDownloadReport = async () => {
    if (!selectedCustomerId || !selectedCustomer) return;
    setDownloadingReport(true);
    setReportError(null);
    try {
      await downloadCustomerReport(instance, selectedCustomerId, selectedCustomer.name);
    } catch (err) {
      setReportError(err instanceof Error ? err.message : "Failed to generate report");
    } finally {
      setDownloadingReport(false);
    }
  };

  return (
    <>
      <div className="dashboard-intro settings-panel-header">
        <div>
          <h1>Security</h1>
          <p>{selectedCustomer ? `Security posture for ${selectedCustomer.name}.` : "Security posture for your selected customer."}</p>
        </div>
        {selectedCustomerId && (
          <button className="btn btn-primary btn-sm" onClick={handleDownloadReport} disabled={downloadingReport}>
            {downloadingReport ? "Generating…" : "Download Report"}
          </button>
        )}
      </div>
      {reportError && <p className="login-error">{reportError}</p>}

      {!customersLoading && customers.length === 0 ? (
        <div className="panel-empty">
          <p>No customers yet. Add one under Settings to start collecting security data.</p>
        </div>
      ) : !customersLoading && !selectedCustomerId ? (
        <div className="panel-empty">
          <p>Select a customer from the switcher above to see their security posture.</p>
        </div>
      ) : (
        <>
          {error && <p className="login-error">{error}</p>}

          {loading ? (
            <p className="fine-print">Loading security data…</p>
          ) : users && users.length > 0 ? (
            <>
              <nav className="subtab-bar">
                {SUB_TABS.map((tab) => (
                  <button
                    key={tab.key}
                    className={`subtab-link${subTab === tab.key ? " subtab-link-active" : ""}`}
                    onClick={() => {
                      setSubTab(tab.key);
                      setSort(null);
                    }}
                  >
                    {tab.label}
                  </button>
                ))}
              </nav>

              {subTab === "overview" && (
                <div className="kpi-row">
                  <StatTile
                    label="Secure Score"
                    value={
                      security?.secureScore
                        ? `${Math.round((security.secureScore.currentScore / security.secureScore.maxScore) * 100)}%`
                        : "—"
                    }
                    caption={
                      security?.secureScore
                        ? `${Math.round(security.secureScore.currentScore)} of ${Math.round(security.secureScore.maxScore)} points`
                        : "No data — needs SecurityEvents.Read.All"
                    }
                  />
                  <StatTile
                    label="Conditional Access"
                    value={caPolicies.length > 0 ? `${enabledCaCount}/${caPolicies.length}` : "—"}
                    caption={caPolicies.length > 0 ? "policies enabled" : "No data — needs Policy.Read.All"}
                  />
                  <StatTile
                    label="MFA coverage"
                    value={hasAnyMfaData ? `${mfaRegisteredCount}/${mfaEligibleUsers.length}` : "—"}
                    caption={hasAnyMfaData ? "users registered" : "No data — needs Reports.Read.All"}
                  />
                  <StatTile
                    label="Forwarding rules"
                    value={String(usersWithForwarding.length)}
                    caption={usersWithForwarding.length === 1 ? "user flagged" : "users flagged"}
                  />
                </div>
              )}

              {subTab === "secure-score" && (
                <>
                  {secureScoreControls.length === 0 ? (
                    <div className="panel-empty">
                      <p>No Secure Score data yet — this needs the SecurityEvents.Read.All Graph permission.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <th></th>
                            <SortableHeader label="Control" columnKey="control" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Category" columnKey="category" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Score" columnKey="score" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(secureScoreControls, sort, SECURE_SCORE_ACCESSORS).map((control) => {
                            const isExpanded = expandedControl === control.id;
                            const ratio = control.maxScore > 0 ? control.achievedScore / control.maxScore : 1;
                            const status = ratio >= 1 ? "connected" : ratio > 0 ? "checking" : "error";
                            return (
                              <Fragment key={control.id}>
                                <tr
                                  className="data-table-row-clickable"
                                  onClick={() => setExpandedControl(isExpanded ? null : control.id)}
                                >
                                  <td className="ca-policy-chevron">{isExpanded ? "▾" : "▸"}</td>
                                  <td>{control.title ?? control.id}</td>
                                  <td>{control.category ?? "—"}</td>
                                  <td>
                                    <span className={`status-dot status-dot-${status}`} />
                                    {control.achievedScore.toFixed(1)} / {control.maxScore.toFixed(1)}
                                  </td>
                                </tr>
                                {isExpanded && (
                                  <tr>
                                    <td colSpan={4}>
                                      <div className="ca-policy-detail">
                                        <div className="ca-policy-detail-section">
                                          <h3>Remediation</h3>
                                          <div className="remediation-content">
                                            {control.remediation
                                              ? renderRemediation(control.remediation)
                                              : "No remediation guidance provided."}
                                          </div>
                                          {control.remediationImpact && control.remediationImpact.toLowerCase() !== "unknown" && (
                                            <p className="fine-print">{control.remediationImpact}</p>
                                          )}
                                        </div>
                                        <div className="ca-policy-detail-section">
                                          <h3>Details</h3>
                                          <dl>
                                            <dt>Tier</dt>
                                            <dd>{control.tier ?? "—"}</dd>
                                            <dt>Implementation cost</dt>
                                            <dd>{control.implementationCost ?? "—"}</dd>
                                            <dt>User impact</dt>
                                            <dd>{control.userImpact ?? "—"}</dd>
                                            <dt>Action type</dt>
                                            <dd>{control.actionType ?? "—"}</dd>
                                          </dl>
                                        </div>
                                        <div className="ca-policy-detail-section">
                                          <h3>Threats mitigated</h3>
                                          {control.threats.length > 0 ? (
                                            <div className="license-chip-row">
                                              {control.threats.map((threat) => (
                                                <span key={threat} className="license-chip">
                                                  {threat}
                                                </span>
                                              ))}
                                            </div>
                                          ) : (
                                            <p className="fine-print">—</p>
                                          )}
                                        </div>
                                      </div>
                                    </td>
                                  </tr>
                                )}
                              </Fragment>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "mfa" && (
                <>
                  {!hasAnyMfaData ? (
                    <p className="fine-print">
                      No MFA data yet — this needs the Reports.Read.All Graph permission. Grant consent again and
                      re-collect.
                    </p>
                  ) : (
                    <p className="fine-print">Showing enabled, licensed accounts only.</p>
                  )}
                  <div className="data-table-wrap">
                    <table className="data-table">
                      <thead>
                        <tr>
                          <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                          <SortableHeader label="Email" columnKey="email" sort={sort} onSort={handleSort} />
                          <SortableHeader label="MFA registered" columnKey="registered" sort={sort} onSort={handleSort} />
                          <SortableHeader label="MFA capable" columnKey="capable" sort={sort} onSort={handleSort} />
                          <SortableHeader label="Methods" columnKey="methods" sort={sort} onSort={handleSort} />
                        </tr>
                      </thead>
                      <tbody>
                        {sortRows(mfaEligibleUsers, sort, MFA_ACCESSORS).map((user) => (
                          <tr key={user.id}>
                            <td>{user.displayName ?? "—"}</td>
                            <td>{user.mail ?? user.userPrincipalName}</td>
                            <td>
                              {user.mfa === null ? (
                                "—"
                              ) : (
                                <>
                                  <span
                                    className={`status-dot status-dot-${user.mfa.isMfaRegistered ? "connected" : "error"}`}
                                  />
                                  {user.mfa.isMfaRegistered ? "Yes" : "No"}
                                </>
                              )}
                            </td>
                            <td>{user.mfa === null ? "—" : user.mfa.isMfaCapable ? "Yes" : "No"}</td>
                            <td>
                              {user.mfa && user.mfa.methods.length > 0 ? (
                                <div className="license-chip-row">
                                  {user.mfa.methods.map((method) => (
                                    <span key={method} className="license-chip">
                                      {method}
                                    </span>
                                  ))}
                                </div>
                              ) : (
                                "—"
                              )}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </>
              )}

              {subTab === "conditional-access" && (
                <>
                  {caPolicies.length === 0 ? (
                    <div className="panel-empty">
                      <p>No Conditional Access data yet — this needs the Policy.Read.All Graph permission, or the customer has no policies configured.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <th></th>
                            <SortableHeader label="Policy" columnKey="policy" sort={sort} onSort={handleSort} />
                            <SortableHeader label="State" columnKey="state" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(caPolicies, sort, CA_ACCESSORS).map((policy) => {
                            const isExpanded = expandedPolicy === policy.displayName;
                            return (
                              <Fragment key={policy.displayName}>
                                <tr
                                  className="data-table-row-clickable"
                                  onClick={() => setExpandedPolicy(isExpanded ? null : policy.displayName)}
                                >
                                  <td className="ca-policy-chevron">{isExpanded ? "▾" : "▸"}</td>
                                  <td>{policy.displayName}</td>
                                  <td>
                                    <span
                                      className={`status-dot status-dot-${policy.state === "enabled" ? "connected" : policy.state === "disabled" ? "error" : "checking"}`}
                                    />
                                    {policyStateLabel(policy.state)}
                                  </td>
                                </tr>
                                {isExpanded && (
                                  <tr key={`${policy.displayName}-detail`}>
                                    <td colSpan={3}>
                                      <div className="ca-policy-detail">
                                        <div className="ca-policy-detail-section">
                                          <h3>Who's targeted</h3>
                                          <dl>
                                            <dt>Included users</dt>
                                            <dd>{formatTargetList(policy.includeUsers, TARGET_LABELS)}</dd>
                                            <dt>Excluded users</dt>
                                            <dd>{formatTargetList(policy.excludeUsers, TARGET_LABELS)}</dd>
                                            <dt>Included groups</dt>
                                            <dd>{formatTargetList(policy.includeGroups, TARGET_LABELS)}</dd>
                                            <dt>Excluded groups</dt>
                                            <dd>{formatTargetList(policy.excludeGroups, TARGET_LABELS)}</dd>
                                            <dt>Included roles</dt>
                                            <dd>{formatTargetList(policy.includeRoles, TARGET_LABELS)}</dd>
                                            <dt>Excluded roles</dt>
                                            <dd>{formatTargetList(policy.excludeRoles, TARGET_LABELS)}</dd>
                                          </dl>
                                        </div>
                                        <div className="ca-policy-detail-section">
                                          <h3>What's covered</h3>
                                          <dl>
                                            <dt>Included apps</dt>
                                            <dd>{formatTargetList(policy.includeApplications, APP_LABELS)}</dd>
                                            <dt>Excluded apps</dt>
                                            <dd>{formatTargetList(policy.excludeApplications, APP_LABELS)}</dd>
                                            <dt>Client app types</dt>
                                            <dd>
                                              {policy.clientAppTypes && policy.clientAppTypes.length > 0
                                                ? policy.clientAppTypes.map((t) => CLIENT_APP_LABELS[t] ?? t).join(", ")
                                                : "All client apps"}
                                            </dd>
                                          </dl>
                                        </div>
                                        <div className="ca-policy-detail-section">
                                          <h3>Controls</h3>
                                          <dl>
                                            <dt>Requires ({policy.grantControlsOperator ?? "—"})</dt>
                                            <dd>
                                              {policy.builtInControls && policy.builtInControls.length > 0
                                                ? policy.builtInControls.map((c) => CONTROL_LABELS[c] ?? c).join(", ")
                                                : "—"}
                                            </dd>
                                          </dl>
                                          <p className="fine-print">
                                            {policy.createdDateTime && `Created ${new Date(policy.createdDateTime).toLocaleDateString()}`}
                                            {policy.modifiedDateTime &&
                                              ` — last modified ${new Date(policy.modifiedDateTime).toLocaleDateString()}`}
                                          </p>
                                        </div>
                                      </div>
                                    </td>
                                  </tr>
                                )}
                              </Fragment>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "forwarding" && (
                <>
                  {forwardingRows.length === 0 ? (
                    <div className="panel-empty">
                      <p>No forwarding rules or mailbox forwarding found — nothing flagged for this customer.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Type" columnKey="type" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Email" columnKey="email" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Detail" columnKey="detail" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Enabled" columnKey="enabled" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Target" columnKey="target" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(forwardingRows, sort, FORWARDING_ACCESSORS).map((row) => (
                            <tr key={row.key}>
                              <td>{row.type}</td>
                              <td>{row.name}</td>
                              <td>{row.email}</td>
                              <td>{row.detail}</td>
                              <td>
                                <span className={`status-dot status-dot-${row.enabled ? "error" : "checking"}`} />
                                {row.enabled ? "Yes" : "No"}
                              </td>
                              <td>{row.target}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "exchange-rules" && (
                <>
                  {exchangeRuleRows.length === 0 ? (
                    <div className="panel-empty">
                      <p>No inbox rules found for this customer's mailboxes.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Email" columnKey="email" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Rule" columnKey="rule" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Enabled" columnKey="enabled" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Conditions" columnKey="conditions" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Actions" columnKey="actions" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(exchangeRuleRows, sort, EXCHANGE_RULE_ACCESSORS).map(({ user, rule }) => (
                            <tr key={`${user.id}-${rule.name}-${rule.sequence}`}>
                              <td>{user.displayName ?? "—"}</td>
                              <td>{user.mail ?? user.userPrincipalName}</td>
                              <td>{rule.name}</td>
                              <td>
                                <span className={`status-dot status-dot-${rule.enabled ? "connected" : "checking"}`} />
                                {rule.enabled ? "Yes" : "No"}
                              </td>
                              <td>
                                {rule.conditionTypes.length > 0
                                  ? rule.conditionTypes.map(humanizeRuleType).join(", ")
                                  : "Any message"}
                              </td>
                              <td>
                                {rule.actionTypes.length > 0 ? rule.actionTypes.map(humanizeRuleType).join(", ") : "—"}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "mail-flow-rules" && (
                <>
                  {(emailSecurity?.transportRules.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No mail flow rules found for this customer, or Exchange Online checks haven't collected yet
                        — see the Email Security tab.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="State" columnKey="state" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Priority" columnKey="priority" sort={sort} onSort={handleSort} />
                            <th>Description</th>
                            <SortableHeader label="Deletes messages" columnKey="deleteMessage" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(emailSecurity?.transportRules ?? [], sort, MAIL_FLOW_RULE_ACCESSORS).map((rule, i) => (
                            <tr key={`${rule.name}-${i}`}>
                              <td>{rule.name ?? "(unnamed rule)"}</td>
                              <td>
                                <span className={`status-dot status-dot-${rule.state === "Enabled" ? "connected" : "checking"}`} />
                                {rule.state ?? "—"}
                              </td>
                              <td>{rule.priority ?? "—"}</td>
                              <td>{rule.description ?? "—"}</td>
                              <td>{rule.deleteMessage ? "Yes" : "No"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "mailbox-access" && (
                <>
                  <h3 className="subsection-heading">Full Access</h3>
                  {(emailSecurity?.mailboxPermissions.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No delegate Full Access grants found for this customer, or Exchange Online checks haven't
                        collected yet — see the Email Security tab.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Mailbox" columnKey="mailbox" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Granted to" columnKey="user" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Access rights" columnKey="accessRights" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Denied" columnKey="deny" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(emailSecurity?.mailboxPermissions ?? [], sort, MAILBOX_PERMISSION_ACCESSORS).map((p, i) => (
                            <tr key={`${p.identity}-${p.user}-${i}`}>
                              <td>{p.identity ?? "—"}</td>
                              <td>{p.user ?? "—"}</td>
                              <td>{(p.accessRights ?? []).join(", ") || "—"}</td>
                              <td>{p.deny ? "Yes" : "No"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  <h3 className="subsection-heading">Send As</h3>
                  {(emailSecurity?.recipientPermissions.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No delegate Send As grants found for this customer, or Exchange Online checks haven't
                        collected yet — see the Email Security tab.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Mailbox" columnKey="mailbox" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Granted to" columnKey="trustee" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Access rights" columnKey="accessRights" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(emailSecurity?.recipientPermissions ?? [], sort, RECIPIENT_PERMISSION_ACCESSORS).map((p, i) => (
                            <tr key={`${p.identity}-${p.trustee}-${i}`}>
                              <td>{p.identity ?? "—"}</td>
                              <td>{p.trustee ?? "—"}</td>
                              <td>{(p.accessRights ?? []).join(", ") || "—"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "email-security" && emailSecurity && (
                <>
                  {certStatus && (certStatus.expiringSoon || certStatus.error) && (
                    <p className="login-error">
                      {certStatus.error
                        ? `Exchange Online certificate problem: ${certStatus.error}`
                        : `Exchange Online certificate expires in ${certStatus.daysRemaining} days — see the README to renew it.`}
                    </p>
                  )}
                  {emailSecurity.exoLastCollectedAt === null ? (
                    <div className="panel-empty">
                      <p>Exchange Online checks need a one-time setup step.</p>
                      <p className="fine-print">
                        {!emailSecurity.exoRoleAssigned
                          ? "Waiting for Exchange Online access to be granted automatically — this happens on the next collection after Graph consent is granted."
                          : "Access granted — waiting for the first successful data collection."}
                      </p>
                      {emailSecurity.exoLastError && <p className="login-error">{emailSecurity.exoLastError}</p>}
                    </div>
                  ) : (
                    <>
                      {emailSecurity.exoLastError && (
                        <p className="login-error">
                          Most recent refresh failed: {emailSecurity.exoLastError} — showing data from{" "}
                          {new Date(emailSecurity.exoLastCollectedAt).toLocaleString()}.
                        </p>
                      )}
                      <ChecksTable
                        checks={emailSecurity.checks}
                        sort={sort}
                        onSort={handleSort}
                        expandedId={expandedCheck}
                        onToggleExpand={(id) => setExpandedCheck((prev) => (prev === id ? null : id))}
                      />
                    </>
                  )}
                </>
              )}

              {subTab === "identity" && identitySecurity && (
                <ChecksTable
                  checks={identitySecurity.checks}
                  sort={sort}
                  onSort={handleSort}
                  expandedId={expandedIdentityCheck}
                  onToggleExpand={(id) => setExpandedIdentityCheck((prev) => (prev === id ? null : id))}
                />
              )}

              {subTab === "domains" && domainSecurity && (
                <ChecksTable
                  checks={domainSecurity.checks}
                  sort={sort}
                  onSort={handleSort}
                  expandedId={expandedDomainCheck}
                  onToggleExpand={(id) => setExpandedDomainCheck((prev) => (prev === id ? null : id))}
                />
              )}

              {subTab === "compliance" && complianceSecurity && (
                <>
                  {complianceSecurity.sccLastCollectedAt === null ? (
                    <div className="panel-empty">
                      <p>Security & Compliance checks need a one-time setup step.</p>
                      <p className="fine-print">
                        {!complianceSecurity.exoRoleAssigned
                          ? "Waiting for Exchange Online access to be granted automatically — this happens on the next collection after Graph consent is granted."
                          : "Access granted — waiting for the first successful data collection."}
                      </p>
                      {complianceSecurity.sccLastError && <p className="login-error">{complianceSecurity.sccLastError}</p>}
                    </div>
                  ) : (
                    <>
                      {complianceSecurity.sccLastError && (
                        <p className="login-error">
                          Most recent refresh failed: {complianceSecurity.sccLastError} — showing data from{" "}
                          {new Date(complianceSecurity.sccLastCollectedAt).toLocaleString()}.
                        </p>
                      )}
                      <ChecksTable
                        checks={complianceSecurity.checks}
                        sort={sort}
                        onSort={handleSort}
                        expandedId={expandedComplianceCheck}
                        onToggleExpand={(id) => setExpandedComplianceCheck((prev) => (prev === id ? null : id))}
                      />
                    </>
                  )}
                </>
              )}
            </>
          ) : (
            !error && (
              <div className="panel-empty">
                <p>No users collected yet for this customer. Check the consent status under Settings.</p>
              </div>
            )
          )}
        </>
      )}
    </>
  );
}
