import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { getCustomerSharePoint, type SharePointInfo, type SharePointSite, type OneDriveUsage } from "../api";
import { useCustomer } from "../CustomerContext";
import { SortableHeader, sortRows, toggleSort, type SortState, type SortValue } from "../sorting";

const SUB_TABS = [
  { key: "sites", label: "SharePoint Sites" },
  { key: "onedrive", label: "OneDrive" },
  { key: "settings", label: "Settings" },
] as const;

type SubTabKey = (typeof SUB_TABS)[number]["key"];

const SITE_ACCESSORS: Record<string, (s: SharePointSite) => SortValue> = {
  url: (s) => s.siteUrl,
  owner: (s) => s.ownerDisplayName ?? "",
  template: (s) => s.rootWebTemplate ?? "",
  storageUsed: (s) => s.storageUsedBytes ?? 0,
  fileCount: (s) => s.fileCount ?? 0,
  lastActivity: (s) => (s.lastActivityDate ? new Date(s.lastActivityDate).getTime() : null),
};

const ONEDRIVE_ACCESSORS: Record<string, (o: OneDriveUsage) => SortValue> = {
  name: (o) => o.displayName ?? o.userPrincipalName,
  storageUsed: (o) => o.oneDriveStorageUsedBytes ?? 0,
  storageAllocated: (o) => o.oneDriveStorageAllocatedBytes ?? 0,
  fileCount: (o) => o.oneDriveFileCount ?? 0,
  lastActivity: (o) => (o.oneDriveLastActivityDate ? new Date(o.oneDriveLastActivityDate).getTime() : null),
};

// Same formatting Users.tsx uses for mailbox size — duplicated rather than
// shared since each tab component here is otherwise self-contained.
function formatBytes(bytes: number | null): string {
  if (bytes === null) return "—";
  if (bytes === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

export default function SharePoint() {
  const { instance } = useMsal();
  const { customers, selectedCustomerId, loading: customersLoading } = useCustomer();
  const [data, setData] = useState<SharePointInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [subTab, setSubTab] = useState<SubTabKey>("sites");
  const [sort, setSort] = useState<SortState<string> | null>(null);

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

    getCustomerSharePoint(instance, selectedCustomerId)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load SharePoint data");
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
        <h1>SharePoint</h1>
        <p>{selectedCustomer ? `SharePoint and OneDrive for ${selectedCustomer.name}.` : "SharePoint and OneDrive for your selected customer."}</p>
      </div>

      {!customersLoading && customers.length === 0 ? (
        <div className="panel-empty">
          <p>No customers yet. Add one under Settings to start collecting SharePoint data.</p>
        </div>
      ) : !customersLoading && !selectedCustomerId ? (
        <div className="panel-empty">
          <p>Select a customer from the switcher above to see their SharePoint data.</p>
        </div>
      ) : (
        <>
          {error && <p className="login-error">{error}</p>}

          {loading ? (
            <p className="fine-print">Loading SharePoint data…</p>
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

              {subTab === "sites" && (
                <>
                  {(data?.sites.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>No SharePoint sites found for this customer, or Reports.Read.All isn't granted yet.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Site" columnKey="url" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Owner" columnKey="owner" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Template" columnKey="template" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Storage used" columnKey="storageUsed" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Files" columnKey="fileCount" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Last activity" columnKey="lastActivity" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(data?.sites ?? [], sort, SITE_ACCESSORS).map((s, i) => (
                            <tr key={s.siteId ?? `${s.siteUrl}-${i}`}>
                              <td>
                                <a href={s.siteUrl} target="_blank" rel="noreferrer">
                                  {s.siteUrl}
                                </a>
                              </td>
                              <td>{s.ownerDisplayName ?? "—"}</td>
                              <td>{s.rootWebTemplate ?? "—"}</td>
                              <td>{formatBytes(s.storageUsedBytes)}</td>
                              <td>{s.fileCount ?? "—"}</td>
                              <td>{s.lastActivityDate ? new Date(s.lastActivityDate).toLocaleDateString() : "—"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "onedrive" && (
                <>
                  {(data?.oneDrives.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>No OneDrive data found for this customer, or Reports.Read.All isn't granted yet.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Storage used" columnKey="storageUsed" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Storage allocated" columnKey="storageAllocated" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Files" columnKey="fileCount" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Last activity" columnKey="lastActivity" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(data?.oneDrives ?? [], sort, ONEDRIVE_ACCESSORS).map((o) => (
                            <tr key={o.userPrincipalName}>
                              <td>{o.displayName ?? o.userPrincipalName}</td>
                              <td>{formatBytes(o.oneDriveStorageUsedBytes)}</td>
                              <td>{formatBytes(o.oneDriveStorageAllocatedBytes)}</td>
                              <td>{o.oneDriveFileCount ?? "—"}</td>
                              <td>{o.oneDriveLastActivityDate ? new Date(o.oneDriveLastActivityDate).toLocaleDateString() : "—"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "settings" && (
                <div className="panel-empty">
                  <p>
                    Tenant-wide SharePoint settings (external sharing policy, etc.) aren't available yet — Microsoft
                    Graph doesn't expose these, so they need SharePoint PowerShell and a new Azure AD permission on
                    top of what's already set up. Coming in a follow-up.
                  </p>
                </div>
              )}
            </>
          )}
        </>
      )}
    </>
  );
}
