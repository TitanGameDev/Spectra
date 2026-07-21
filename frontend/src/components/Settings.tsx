import { useEffect, useState, type FormEvent } from "react";
import { useMsal } from "@azure/msal-react";
import { Navigate } from "react-router-dom";
import {
  getSettings,
  updateSettings,
  searchGroups,
  getCustomers,
  createCustomer,
  getDatabaseStatus,
  saveDatabaseConnection,
  provisionDatabase,
  type GraphGroup,
  type SettingsResponse,
  type Customer,
  type DatabaseStatus,
  type DatabaseType,
} from "../api";

const DEFAULT_PORTS: Record<DatabaseType, string> = { sqlserver: "1433", mysql: "3306" };
const DATABASE_LABELS: Record<DatabaseType, string> = { sqlserver: "SQL Server", mysql: "MySQL" };
import { useCurrentUser } from "../UserContext";

export default function Settings() {
  const { instance } = useMsal();
  const { me, loading: meLoading } = useCurrentUser();

  const [settings, setSettings] = useState<SettingsResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const [query, setQuery] = useState("");
  const [results, setResults] = useState<GraphGroup[]>([]);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [selectedGroup, setSelectedGroup] = useState<GraphGroup | null>(null);

  const [customers, setCustomers] = useState<Customer[]>([]);
  const [customersError, setCustomersError] = useState<string | null>(null);
  const [showAddCustomer, setShowAddCustomer] = useState(false);
  const [newCustomerName, setNewCustomerName] = useState("");
  const [addingCustomer, setAddingCustomer] = useState(false);
  const [addCustomerError, setAddCustomerError] = useState<string | null>(null);

  const [dbStatus, setDbStatus] = useState<DatabaseStatus | null>(null);
  const [dbStatusError, setDbStatusError] = useState<string | null>(null);
  const [showDbForm, setShowDbForm] = useState(false);
  const [dbType, setDbType] = useState<DatabaseType>("sqlserver");
  const [dbHost, setDbHost] = useState("");
  const [dbPort, setDbPort] = useState(DEFAULT_PORTS.sqlserver);
  const [dbName, setDbName] = useState("SpectraDb");
  const [dbUsername, setDbUsername] = useState("");
  const [dbPassword, setDbPassword] = useState("");
  const [dbSaving, setDbSaving] = useState(false);
  const [dbSaveError, setDbSaveError] = useState<string | null>(null);
  const [provisioning, setProvisioning] = useState(false);
  const [provisionError, setProvisionError] = useState<string | null>(null);
  const [switchedProvider, setSwitchedProvider] = useState<DatabaseType | null>(null);

  const handleDbTypeChange = (type: DatabaseType) => {
    setDbType(type);
    setDbPort(DEFAULT_PORTS[type]);
  };

  useEffect(() => {
    if (!me?.isAdmin) return;
    getDatabaseStatus(instance)
      .then(setDbStatus)
      .catch((err) => setDbStatusError(err instanceof Error ? err.message : "Failed to load database status"));
  }, [instance, me?.isAdmin]);

  useEffect(() => {
    if (!me?.isAdmin) return;
    getCustomers(instance)
      .then(setCustomers)
      .catch((err) => setCustomersError(err instanceof Error ? err.message : "Failed to load customers"));
  }, [instance, me?.isAdmin]);

  useEffect(() => {
    if (!me?.isAdmin) return;
    getSettings(instance)
      .then((data) => {
        setSettings(data);
        if (data.adminGroupId && data.adminGroupDisplayName) {
          setSelectedGroup({ id: data.adminGroupId, displayName: data.adminGroupDisplayName });
        }
      })
      .catch((err) => setLoadError(err instanceof Error ? err.message : "Failed to load settings"));
  }, [instance, me?.isAdmin]);

  // Debounced group search — the first call in a session triggers a one-time
  // Group.Read.All consent popup (see authConfig.ts / api.ts).
  useEffect(() => {
    if (!query.trim()) {
      setResults([]);
      return;
    }
    setSearching(true);
    setSearchError(null);
    const handle = setTimeout(() => {
      searchGroups(instance, query)
        .then(setResults)
        .catch((err) => setSearchError(err instanceof Error ? err.message : "Search failed"))
        .finally(() => setSearching(false));
    }, 350);
    return () => clearTimeout(handle);
  }, [instance, query]);

  const handleSelect = (group: GraphGroup) => {
    setSelectedGroup(group);
    setResults([]);
    setQuery("");
    setSaved(false);
  };

  const handleClear = () => {
    setSelectedGroup(null);
    setSaved(false);
  };

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    setSaved(false);
    try {
      const updated = await updateSettings(instance, {
        adminGroupId: selectedGroup?.id ?? null,
        adminGroupDisplayName: selectedGroup?.displayName ?? null,
      });
      setSettings(updated);
      setSaved(true);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Failed to save settings");
    } finally {
      setSaving(false);
    }
  };

  const handleAddCustomer = async (e: FormEvent) => {
    e.preventDefault();
    const name = newCustomerName.trim();
    if (!name) return;

    setAddingCustomer(true);
    setAddCustomerError(null);
    try {
      const created = await createCustomer(instance, name);
      setCustomers((prev) => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)));
      setNewCustomerName("");
      setShowAddCustomer(false);
    } catch (err) {
      setAddCustomerError(err instanceof Error ? err.message : "Failed to add customer");
    } finally {
      setAddingCustomer(false);
    }
  };

  const handleSaveDatabase = async (e: FormEvent) => {
    e.preventDefault();
    setDbSaving(true);
    setDbSaveError(null);
    try {
      const result = await saveDatabaseConnection(instance, {
        databaseType: dbType,
        host: dbHost.trim(),
        port: Number(dbPort) || Number(DEFAULT_PORTS[dbType]),
        databaseName: dbName.trim(),
        username: dbUsername.trim(),
        password: dbPassword,
      });
      setDbStatus((prev) => ({
        activeProvider: prev?.activeProvider ?? "sqlite",
        configured: true,
        databaseType: result.databaseType,
        host: result.host,
        port: result.port,
        databaseName: result.databaseName,
        username: result.username,
        isProvisioned: result.isProvisioned,
        updatedAt: new Date().toISOString(),
        updatedByEmail: prev?.updatedByEmail ?? null,
      }));
      setDbPassword("");
      setShowDbForm(false);
    } catch (err) {
      setDbSaveError(err instanceof Error ? err.message : "Failed to save connection");
    } finally {
      setDbSaving(false);
    }
  };

  const handleProvision = async () => {
    setProvisioning(true);
    setProvisionError(null);
    try {
      const result = await provisionDatabase(instance);
      const activeProvider = result.activeProvider as DatabaseStatus["activeProvider"];
      setDbStatus((prev) => prev && { ...prev, activeProvider, isProvisioned: true });
      setSwitchedProvider(activeProvider as DatabaseType);
    } catch (err) {
      setProvisionError(err instanceof Error ? err.message : "Failed to create the database");
    } finally {
      setProvisioning(false);
    }
  };

  // Defense in depth — the backend already rejects non-admins with a 403;
  // this just avoids flashing the page for someone who navigates here directly.
  if (!meLoading && me && !me.isAdmin) {
    return <Navigate to="/" replace />;
  }

  return (
    <>
      <div className="dashboard-intro">
        <h1>Settings</h1>
        <p>Configure who has admin access to Spectra.</p>
      </div>

      <div className="settings-panel">
        <h2>Admin group</h2>
        <p className="settings-hint">
          Members of this Entra ID group get admin access to Spectra, including this page. The first person to
          ever sign in keeps admin access permanently as a safety net, regardless of group membership.
        </p>

        {loadError && <p className="login-error">{loadError}</p>}

        {selectedGroup ? (
          <div className="group-chip">
            <span>{selectedGroup.displayName}</span>
            <button className="group-chip-remove" onClick={handleClear} aria-label="Remove admin group">
              ×
            </button>
          </div>
        ) : (
          <div className="group-search">
            <input
              type="text"
              className="text-input"
              placeholder="Search groups by name…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
            {searching && <p className="fine-print">Searching…</p>}
            {searchError && <p className="login-error">{searchError}</p>}
            {results.length > 0 && (
              <ul className="group-results">
                {results.map((group) => (
                  <li key={group.id}>
                    <button className="group-result" onClick={() => handleSelect(group)}>
                      {group.displayName}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        <div className="settings-actions">
          <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </button>
          {saved && <span className="fine-print">Saved.</span>}
        </div>
        {saveError && <p className="login-error">{saveError}</p>}

        {settings?.updatedAt && (
          <p className="fine-print">
            Last updated {new Date(settings.updatedAt).toLocaleString()}
            {settings.updatedByEmail ? ` by ${settings.updatedByEmail}` : ""}
          </p>
        )}
      </div>

      <div className="settings-panel">
        <div className="settings-panel-header">
          <h2>Customers</h2>
          {!showAddCustomer && (
            <button className="btn btn-primary btn-sm" onClick={() => setShowAddCustomer(true)}>
              + Add Customer
            </button>
          )}
        </div>
        <p className="settings-hint">The customers this tool manages.</p>

        {customersError && <p className="login-error">{customersError}</p>}

        {showAddCustomer && (
          <form className="customer-add-form" onSubmit={handleAddCustomer}>
            <input
              type="text"
              className="text-input"
              placeholder="Customer name…"
              value={newCustomerName}
              onChange={(e) => setNewCustomerName(e.target.value)}
              autoFocus
            />
            <div className="settings-actions">
              <button type="submit" className="btn btn-primary" disabled={addingCustomer || !newCustomerName.trim()}>
                {addingCustomer ? "Adding…" : "Add"}
              </button>
              <button
                type="button"
                className="btn btn-ghost"
                onClick={() => {
                  setShowAddCustomer(false);
                  setNewCustomerName("");
                  setAddCustomerError(null);
                }}
              >
                Cancel
              </button>
            </div>
            {addCustomerError && <p className="login-error">{addCustomerError}</p>}
          </form>
        )}

        {customers.length > 0 ? (
          <ul className="customer-list">
            {customers.map((customer) => (
              <li key={customer.id} className="customer-list-item">
                <span>{customer.name}</span>
                <span className="fine-print">Added {new Date(customer.createdAt).toLocaleDateString()}</span>
              </li>
            ))}
          </ul>
        ) : (
          !showAddCustomer && <p className="fine-print">No customers yet.</p>
        )}
      </div>

      <div className="settings-panel">
        <h2>Database</h2>
        <p className="settings-hint">
          Spectra currently stores its data in{" "}
          {dbStatus && dbStatus.activeProvider !== "sqlite"
            ? DATABASE_LABELS[dbStatus.activeProvider as DatabaseType]
            : "a local SQLite file"}
          . Connect a SQL Server or MySQL instance to move off SQLite.
        </p>

        {dbStatusError && <p className="login-error">{dbStatusError}</p>}

        {dbStatus && dbStatus.activeProvider !== "sqlite" ? (
          <div className="db-status db-status-active">
            <span className="status-dot status-dot-connected" aria-hidden="true" />
            <div>
              <strong>Using {DATABASE_LABELS[dbStatus.activeProvider as DatabaseType]}</strong>
              <p className="fine-print">
                {dbStatus.host}:{dbStatus.port} / {dbStatus.databaseName}
              </p>
            </div>
          </div>
        ) : showDbForm || !dbStatus?.configured ? (
          <form className="customer-add-form" onSubmit={handleSaveDatabase}>
            <div className="db-type-toggle">
              <button
                type="button"
                className={`db-type-option${dbType === "sqlserver" ? " db-type-option-active" : ""}`}
                onClick={() => handleDbTypeChange("sqlserver")}
              >
                SQL Server
              </button>
              <button
                type="button"
                className={`db-type-option${dbType === "mysql" ? " db-type-option-active" : ""}`}
                onClick={() => handleDbTypeChange("mysql")}
              >
                MySQL
              </button>
            </div>
            <input
              type="text"
              className="text-input"
              placeholder="Host / IP address"
              value={dbHost}
              onChange={(e) => setDbHost(e.target.value)}
              required
            />
            <input
              type="number"
              className="text-input"
              placeholder="Port"
              value={dbPort}
              onChange={(e) => setDbPort(e.target.value)}
              min={1}
              max={65535}
            />
            <input
              type="text"
              className="text-input"
              placeholder="Database name"
              value={dbName}
              onChange={(e) => setDbName(e.target.value)}
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="Username"
              value={dbUsername}
              onChange={(e) => setDbUsername(e.target.value)}
              required
              autoComplete="off"
            />
            <input
              type="password"
              className="text-input"
              placeholder="Password"
              value={dbPassword}
              onChange={(e) => setDbPassword(e.target.value)}
              required
              autoComplete="new-password"
            />
            <div className="settings-actions">
              <button type="submit" className="btn btn-primary" disabled={dbSaving}>
                {dbSaving ? "Connecting…" : "Connect"}
              </button>
              {dbStatus?.configured && (
                <button type="button" className="btn btn-ghost" onClick={() => setShowDbForm(false)}>
                  Cancel
                </button>
              )}
            </div>
            {dbSaveError && <p className="login-error">{dbSaveError}</p>}
          </form>
        ) : (
          <>
            <div className="db-status">
              <div>
                <strong>
                  {dbStatus.databaseType ? DATABASE_LABELS[dbStatus.databaseType] : ""} — {dbStatus.host}:
                  {dbStatus.port} / {dbStatus.databaseName}
                </strong>
                <p className="fine-print">User: {dbStatus.username} — database not created yet</p>
              </div>
            </div>
            <div className="settings-actions">
              <button className="btn btn-primary" onClick={handleProvision} disabled={provisioning}>
                {provisioning ? "Creating…" : "Create Database"}
              </button>
              <button className="btn btn-ghost" onClick={() => setShowDbForm(true)}>
                Edit connection
              </button>
            </div>
            {provisionError && <p className="login-error">{provisionError}</p>}
          </>
        )}

        {switchedProvider && (
          <p className="fine-print">Switched to {DATABASE_LABELS[switchedProvider]} — Spectra is now using this database.</p>
        )}
      </div>
    </>
  );
}
