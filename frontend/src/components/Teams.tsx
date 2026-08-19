import { Fragment, useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { getCustomerTeams, type TeamsInfo, type Team, type TeamsActivityUsage } from "../api";
import { useCustomer } from "../CustomerContext";
import { SortableHeader, sortRows, toggleSort, type SortState, type SortValue } from "../sorting";

const SUB_TABS = [
  { key: "teams", label: "Teams" },
  { key: "activity", label: "Activity" },
] as const;

type SubTabKey = (typeof SUB_TABS)[number]["key"];

function ownerNames(team: Team): string {
  return team.members
    .filter((m) => m.roles.includes("owner"))
    .map((m) => m.displayName ?? m.email ?? "—")
    .join(", ");
}

function channelNames(team: Team): string {
  return team.channels.map((c) => c.displayName).join(", ");
}

const TEAM_ACCESSORS: Record<string, (t: Team) => SortValue> = {
  name: (t) => t.displayName,
  visibility: (t) => t.visibility ?? "",
  archived: (t) => (t.isArchived ? 1 : 0),
  sharepoint: (t) => (t.sharePointSiteUrl ? 1 : 0),
  channels: (t) => t.channels.length,
  owners: (t) => ownerNames(t),
  members: (t) => t.members.length,
};

const ACTIVITY_ACCESSORS: Record<string, (a: TeamsActivityUsage) => SortValue> = {
  name: (a) => a.displayName ?? a.userPrincipalName,
  chat: (a) => a.teamsChatMessageCount ?? 0,
  privateChat: (a) => a.teamsPrivateChatMessageCount ?? 0,
  calls: (a) => a.teamsCallCount ?? 0,
  meetings: (a) => a.teamsMeetingCount ?? 0,
  lastActivity: (a) => (a.teamsLastActivityDate ? new Date(a.teamsLastActivityDate).getTime() : null),
};

export default function Teams() {
  const { instance } = useMsal();
  const { customers, selectedCustomerId, loading: customersLoading } = useCustomer();
  const [data, setData] = useState<TeamsInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [subTab, setSubTab] = useState<SubTabKey>("teams");
  const [sort, setSort] = useState<SortState<string> | null>(null);
  const [expandedTeamId, setExpandedTeamId] = useState<string | null>(null);

  const selectedCustomer = customers.find((c) => c.id === selectedCustomerId);

  useEffect(() => {
    if (!selectedCustomerId) {
      setData(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    getCustomerTeams(instance, selectedCustomerId)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load Teams data");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance, selectedCustomerId]);

  const handleSort = (key: string) => setSort((prev) => toggleSort(prev, key));

  return (
    <>
      <div className="dashboard-intro">
        <h1>Teams</h1>
        <p>{selectedCustomer ? `Microsoft Teams for ${selectedCustomer.name}.` : "Microsoft Teams for your selected customer."}</p>
      </div>

      {!customersLoading && customers.length === 0 ? (
        <div className="panel-empty">
          <p>No customers yet. Add one under Settings to start collecting Teams data.</p>
        </div>
      ) : !customersLoading && !selectedCustomerId ? (
        <div className="panel-empty">
          <p>Select a customer from the switcher above to see their Teams data.</p>
        </div>
      ) : (
        <>
          {error && <p className="login-error">{error}</p>}

          {loading ? (
            <p className="fine-print">Loading Teams data…</p>
          ) : (
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

              {subTab === "teams" && (
                <>
                  {(data?.teams.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No Teams collected for this customer yet. If Team.ReadBasic.All, Channel.ReadBasic.All, and
                        TeamMember.Read.All are already granted and other data (mailbox usage, MFA, etc.) is syncing
                        fine, run a sync to pick up Teams — these are new permissions added after the initial setup,
                        so an existing customer needs to re-grant consent first (Settings → Customers → Grant
                        consent).
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Team" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Visibility" columnKey="visibility" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Archived" columnKey="archived" sort={sort} onSort={handleSort} />
                            <SortableHeader label="SharePoint" columnKey="sharepoint" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Channels" columnKey="channels" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Owners" columnKey="owners" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Members" columnKey="members" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(data?.teams ?? [], sort, TEAM_ACCESSORS).map((t) => {
                            const expanded = expandedTeamId === t.teamId;
                            return (
                              <Fragment key={t.teamId}>
                                <tr
                                  className="data-table-row-clickable"
                                  style={{ cursor: "pointer" }}
                                  onClick={() => setExpandedTeamId(expanded ? null : t.teamId)}
                                  aria-expanded={expanded}
                                >
                                  <td title={t.description ?? undefined}>
                                    <span className={`sort-arrow${expanded ? " sort-arrow-active" : ""}`}>{expanded ? "▾" : "▸"}</span>{" "}
                                    {t.displayName}
                                  </td>
                                  <td>{t.visibility ?? "—"}</td>
                                  <td>{t.isArchived === null ? "—" : t.isArchived ? "Yes" : "No"}</td>
                                  <td>
                                    {t.sharePointSiteUrl ? (
                                      <a
                                        href={t.sharePointSiteUrl}
                                        target="_blank"
                                        rel="noreferrer"
                                        onClick={(e) => e.stopPropagation()}
                                      >
                                        Open ↗
                                      </a>
                                    ) : (
                                      "Not linked"
                                    )}
                                  </td>
                                  <td title={channelNames(t) || undefined}>{t.channels.length}</td>
                                  <td>{ownerNames(t) || "—"}</td>
                                  <td>{t.members.length}</td>
                                </tr>
                                {expanded && (
                                  <tr>
                                    <td colSpan={7}>
                                      {t.members.length === 0 ? (
                                        <p className="fine-print">No members returned for this team.</p>
                                      ) : (
                                        <table className="data-table">
                                          <thead>
                                            <tr>
                                              <th>Name</th>
                                              <th>Email</th>
                                              <th>Role</th>
                                            </tr>
                                          </thead>
                                          <tbody>
                                            {t.members.map((m, i) => (
                                              <tr key={`${t.teamId}-member-${i}`}>
                                                <td>{m.displayName ?? "—"}</td>
                                                <td>{m.email ?? "—"}</td>
                                                <td>{m.roles.includes("owner") ? "Owner" : "Member"}</td>
                                              </tr>
                                            ))}
                                          </tbody>
                                        </table>
                                      )}
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

              {subTab === "activity" && (
                <>
                  {(data?.activity.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No Teams activity collected for this customer yet. If Reports.Read.All is already granted and
                        other data is syncing fine, this is most likely Microsoft still generating the report for the
                        first time — that can take up to 48 hours after the first request. Check Settings →
                        Customers for a specific warning if it's still empty after that.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Chat messages" columnKey="chat" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Private chat messages" columnKey="privateChat" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Calls" columnKey="calls" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Meetings" columnKey="meetings" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Last activity" columnKey="lastActivity" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(data?.activity ?? [], sort, ACTIVITY_ACCESSORS).map((a) => (
                            <tr key={a.userPrincipalName}>
                              <td>{a.displayName ?? a.userPrincipalName}</td>
                              <td>{a.teamsChatMessageCount ?? "—"}</td>
                              <td>{a.teamsPrivateChatMessageCount ?? "—"}</td>
                              <td>{a.teamsCallCount ?? "—"}</td>
                              <td>{a.teamsMeetingCount ?? "—"}</td>
                              <td>{a.teamsLastActivityDate ? new Date(a.teamsLastActivityDate).toLocaleDateString() : "—"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </>
      )}
    </>
  );
}
