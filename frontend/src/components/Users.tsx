import { useEffect, useMemo, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { listUsers, type DirectoryUser } from "../api";

export default function Users() {
  const { instance } = useMsal();
  const [users, setUsers] = useState<DirectoryUser[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState("");

  useEffect(() => {
    let cancelled = false;

    listUsers(instance)
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
  }, [instance]);

  const filtered = useMemo(() => {
    if (!users) return [];
    const q = query.trim().toLowerCase();
    if (!q) return users;
    return users.filter((u) =>
      [u.displayName, u.mail, u.userPrincipalName, u.jobTitle].some((field) => field?.toLowerCase().includes(q)),
    );
  }, [users, query]);

  return (
    <>
      <div className="dashboard-intro">
        <h1>Users</h1>
        <p>The people in your tenant.</p>
      </div>

      {error && <p className="login-error">{error}</p>}

      {loading ? (
        <p className="fine-print">Loading users…</p>
      ) : users && users.length > 0 ? (
        <>
          <input
            type="text"
            className="text-input"
            placeholder="Filter by name, email, or job title…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />

          <div className="data-table-wrap">
            <table className="data-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Email</th>
                  <th>Job title</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((user) => (
                  <tr key={user.id}>
                    <td>{user.displayName ?? "—"}</td>
                    <td>{user.mail ?? user.userPrincipalName}</td>
                    <td>{user.jobTitle ?? "—"}</td>
                    <td>
                      <span className={`status-dot status-dot-${user.accountEnabled ? "connected" : "error"}`} />
                      {user.accountEnabled ? "Enabled" : "Disabled"}
                    </td>
                  </tr>
                ))}
              </tbody>
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
            <p>No users found.</p>
          </div>
        )
      )}
    </>
  );
}
