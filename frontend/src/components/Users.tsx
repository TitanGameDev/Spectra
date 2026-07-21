import { useEffect, useMemo, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { getCustomerUsers, type CustomerUser } from "../api";
import { useCustomer } from "../CustomerContext";

const SUB_TABS = [
  { key: "directory", label: "Directory" },
  { key: "mailboxes", label: "Mailboxes" },
  { key: "licenses", label: "Licenses" },
] as const;

type SubTabKey = (typeof SUB_TABS)[number]["key"];

function formatBytes(bytes: number | null): string {
  if (bytes === null) return "—";
  if (bytes === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

export default function Users() {
  const { instance } = useMsal();
  const { customers, selectedCustomerId, loading: customersLoading } = useCustomer();
  const [users, setUsers] = useState<CustomerUser[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState("");
  const [subTab, setSubTab] = useState<SubTabKey>("directory");

  const selectedCustomer = customers.find((c) => c.id === selectedCustomerId);

  useEffect(() => {
    if (!selectedCustomerId) {
      setUsers(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    getCustomerUsers(instance, selectedCustomerId)
      .then((data) => {
        if (!cancelled) setUsers(data);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load users");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance, selectedCustomerId]);

  const filtered = useMemo(() => {
    if (!users) return [];
    const q = query.trim().toLowerCase();
    if (!q) return users;
    return users.filter((u) =>
      [u.displayName, u.mail, u.userPrincipalName, u.jobTitle, u.department].some((field) =>
        field?.toLowerCase().includes(q),
      ),
    );
  }, [users, query]);

  // Mailbox data comes from a separate Graph permission (Reports.Read.All) —
  // if nobody has it, that's very likely because it hasn't been granted yet
  // rather than every mailbox genuinely having no data.
  const hasAnyMailboxData = users?.some((u) => u.mailbox !== null) ?? false;

  return (
    <>
      <div className="dashboard-intro">
        <h1>Users</h1>
        <p>{selectedCustomer ? `The people at ${selectedCustomer.name}.` : "The people at your selected customer."}</p>
      </div>

      {!customersLoading && customers.length === 0 ? (
        <div className="panel-empty">
          <p>No customers yet. Add one under Settings to start collecting user data.</p>
        </div>
      ) : !customersLoading && !selectedCustomerId ? (
        <div className="panel-empty">
          <p>Select a customer from the switcher above to see their users.</p>
        </div>
      ) : (
        <>
          {error && <p className="login-error">{error}</p>}

          {loading ? (
            <p className="fine-print">Loading users…</p>
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

              <input
                type="text"
                className="text-input"
                placeholder="Filter by name, email, job title, or department…"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
              />

              {subTab === "mailboxes" && !hasAnyMailboxData && (
                <p className="fine-print">
                  No mailbox data yet — this needs the Reports.Read.All Graph permission on top of the one used for
                  the directory. See the README, then grant consent again and re-collect.
                </p>
              )}

              <div className="data-table-wrap">
                <table className="data-table">
                  {subTab === "directory" && (
                    <>
                      <thead>
                        <tr>
                          <th>Name</th>
                          <th>Email</th>
                          <th>Job title</th>
                          <th>Department</th>
                          <th>Office</th>
                          <th>Status</th>
                          <th>Created</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filtered.map((user) => (
                          <tr key={user.id}>
                            <td>{user.displayName ?? "—"}</td>
                            <td>{user.mail ?? user.userPrincipalName}</td>
                            <td>{user.jobTitle ?? "—"}</td>
                            <td>{user.department ?? "—"}</td>
                            <td>{user.officeLocation ?? "—"}</td>
                            <td>
                              <span
                                className={`status-dot status-dot-${user.accountEnabled ? "connected" : "error"}`}
                              />
                              {user.accountEnabled ? "Enabled" : "Disabled"}
                            </td>
                            <td>{user.createdDateTime ? new Date(user.createdDateTime).toLocaleDateString() : "—"}</td>
                          </tr>
                        ))}
                      </tbody>
                    </>
                  )}

                  {subTab === "mailboxes" && (
                    <>
                      <thead>
                        <tr>
                          <th>Name</th>
                          <th>Email</th>
                          <th>Size</th>
                          <th>Items</th>
                          <th>Archive</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filtered.map((user) => (
                          <tr key={user.id}>
                            <td>{user.displayName ?? "—"}</td>
                            <td>{user.mail ?? user.userPrincipalName}</td>
                            <td>{formatBytes(user.mailbox?.sizeBytes ?? null)}</td>
                            <td>{user.mailbox?.itemCount ?? "—"}</td>
                            <td>
                              {user.mailbox?.hasArchive === null || user.mailbox === null
                                ? "—"
                                : user.mailbox.hasArchive
                                  ? "Enabled"
                                  : "Disabled"}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </>
                  )}

                  {subTab === "licenses" && (
                    <>
                      <thead>
                        <tr>
                          <th>Name</th>
                          <th>Email</th>
                          <th>Licenses</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filtered.map((user) => (
                          <tr key={user.id}>
                            <td>{user.displayName ?? "—"}</td>
                            <td>{user.mail ?? user.userPrincipalName}</td>
                            <td>
                              {user.licenses.length > 0 ? (
                                <div className="license-chip-row">
                                  {user.licenses.map((license) => (
                                    <span key={license.skuId} className="license-chip">
                                      {license.displayName}
                                    </span>
                                  ))}
                                </div>
                              ) : (
                                <span className="fine-print">Unlicensed</span>
                              )}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </>
                  )}
                </table>
              </div>

              {filtered.length === 0 && <p className="fine-print">No users match "{query}".</p>}
              <p className="fine-print">
                {filtered.length} of {users.length} users
              </p>
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
