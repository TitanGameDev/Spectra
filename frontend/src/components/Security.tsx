import { Fragment, useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { getCustomerUsers, getCustomerSecurity, type CustomerUser, type CustomerSecurityInfo } from "../api";
import { useCustomer } from "../CustomerContext";

const SUB_TABS = [
  { key: "overview", label: "Overview" },
  { key: "mfa", label: "MFA" },
  { key: "conditional-access", label: "Conditional Access" },
  { key: "forwarding", label: "Forwarding Rules" },
  { key: "exchange-rules", label: "Exchange Rules" },
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
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [subTab, setSubTab] = useState<SubTabKey>("overview");
  const [expandedPolicy, setExpandedPolicy] = useState<string | null>(null);

  const selectedCustomer = customers.find((c) => c.id === selectedCustomerId);

  useEffect(() => {
    if (!selectedCustomerId) {
      setUsers(null);
      setSecurity(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([getCustomerUsers(instance, selectedCustomerId), getCustomerSecurity(instance, selectedCustomerId)])
      .then(([usersData, securityData]) => {
        if (!cancelled) {
          setUsers(usersData);
          setSecurity(securityData);
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

  const hasAnyMfaData = users?.some((u) => u.mfa !== null) ?? false;
  const mfaRegisteredCount = users?.filter((u) => u.mfa?.isMfaRegistered).length ?? 0;
  const usersWithForwarding = users?.filter((u) => u.forwardingRules.length > 0) ?? [];
  const caPolicies = security?.conditionalAccessPolicies ?? [];
  const enabledCaCount = caPolicies.filter((p) => p.state === "enabled").length;

  return (
    <>
      <div className="dashboard-intro">
        <h1>Security</h1>
        <p>{selectedCustomer ? `Security posture for ${selectedCustomer.name}.` : "Security posture for your selected customer."}</p>
      </div>

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
                    onClick={() => setSubTab(tab.key)}
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
                    value={hasAnyMfaData ? `${mfaRegisteredCount}/${users.length}` : "—"}
                    caption={hasAnyMfaData ? "users registered" : "No data — needs Reports.Read.All"}
                  />
                  <StatTile
                    label="Forwarding rules"
                    value={String(usersWithForwarding.length)}
                    caption={usersWithForwarding.length === 1 ? "user flagged" : "users flagged"}
                  />
                </div>
              )}

              {subTab === "mfa" && (
                <>
                  {!hasAnyMfaData && (
                    <p className="fine-print">
                      No MFA data yet — this needs the Reports.Read.All Graph permission. Grant consent again and
                      re-collect.
                    </p>
                  )}
                  <div className="data-table-wrap">
                    <table className="data-table">
                      <thead>
                        <tr>
                          <th>Name</th>
                          <th>Email</th>
                          <th>MFA registered</th>
                          <th>MFA capable</th>
                          <th>Methods</th>
                        </tr>
                      </thead>
                      <tbody>
                        {users.map((user) => (
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
                            <th>Policy</th>
                            <th>State</th>
                          </tr>
                        </thead>
                        <tbody>
                          {caPolicies.map((policy) => {
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
                  {usersWithForwarding.length === 0 ? (
                    <div className="panel-empty">
                      <p>No forwarding or redirect rules found — nothing flagged for this customer.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <th>Name</th>
                            <th>Email</th>
                            <th>Rule</th>
                            <th>Enabled</th>
                            <th>Forwards to</th>
                          </tr>
                        </thead>
                        <tbody>
                          {usersWithForwarding.flatMap((user) =>
                            user.forwardingRules.map((rule) => (
                              <tr key={`${user.id}-${rule.name}`}>
                                <td>{user.displayName ?? "—"}</td>
                                <td>{user.mail ?? user.userPrincipalName}</td>
                                <td>{rule.name}</td>
                                <td>
                                  <span className={`status-dot status-dot-${rule.enabled ? "error" : "checking"}`} />
                                  {rule.enabled ? "Yes" : "No"}
                                </td>
                                <td>{rule.forwardsTo.join(", ")}</td>
                              </tr>
                            )),
                          )}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "exchange-rules" && (
                <>
                  {users.every((u) => u.inboxRules.length === 0) ? (
                    <div className="panel-empty">
                      <p>No inbox rules found for this customer's mailboxes.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <th>Name</th>
                            <th>Email</th>
                            <th>Rule</th>
                            <th>Enabled</th>
                            <th>Conditions</th>
                            <th>Actions</th>
                          </tr>
                        </thead>
                        <tbody>
                          {users.flatMap((user) =>
                            user.inboxRules.map((rule) => (
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
                            )),
                          )}
                        </tbody>
                      </table>
                    </div>
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
