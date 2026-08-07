import { useEffect, useState, type FormEvent } from "react";
import { useMsal } from "@azure/msal-react";
import { Navigate } from "react-router-dom";
import {
  getSettings,
  updateSettings,
  searchGroups,
  getCustomers,
  createCustomer,
  collectCustomerData,
  getConsentUrl,
  getAzureRoleCommand,
  getDatabaseStatus,
  saveDatabaseConnection,
  provisionDatabase,
  getUpdateStatus,
  requestUpdate,
  getAzureAdStatus,
  saveAzureAdConfig,
  getCollectionProgress,
  syncAllCustomers,
  getSyncAllStatus,
  type GraphGroup,
  type SettingsResponse,
  type Customer,
  type DatabaseStatus,
  type DatabaseType,
  type UpdateStatusResponse,
  type AzureAdStatus,
  type CollectionProgressLine,
  type BulkSyncStatus,
} from "../api";

const DEFAULT_PORTS: Record<DatabaseType, string> = { sqlserver: "1433", mysql: "3306" };
const DATABASE_LABELS: Record<DatabaseType, string> = { sqlserver: "SQL Server", mysql: "MySQL" };
import { useCurrentUser } from "../UserContext";
import { CONSENT_MESSAGE_TYPE } from "./ConsentCallback";

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
  const [newCustomerTenantId, setNewCustomerTenantId] = useState("");
  const [addingCustomer, setAddingCustomer] = useState(false);
  const [addCustomerError, setAddCustomerError] = useState<string | null>(null);
  const [collectingId, setCollectingId] = useState<number | null>(null);
  const [collectError, setCollectError] = useState<string | null>(null);
  const [progressLines, setProgressLines] = useState<Record<number, CollectionProgressLine[]>>({});
  const [syncingAll, setSyncingAll] = useState(false);
  const [syncAllStatus, setSyncAllStatus] = useState<BulkSyncStatus | null>(null);
  const [syncAllError, setSyncAllError] = useState<string | null>(null);
  const [consentError, setConsentError] = useState<string | null>(null);
  const [azureCommandError, setAzureCommandError] = useState<string | null>(null);
  const [azureCommandCopiedId, setAzureCommandCopiedId] = useState<number | null>(null);

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

  const [updateStatus, setUpdateStatus] = useState<UpdateStatusResponse | null>(null);
  const [updateStatusError, setUpdateStatusError] = useState<string | null>(null);
  const [requestingUpdate, setRequestingUpdate] = useState(false);
  const [requestUpdateError, setRequestUpdateError] = useState<string | null>(null);

  const [azureAdStatus, setAzureAdStatus] = useState<AzureAdStatus | null>(null);
  const [azureAdStatusError, setAzureAdStatusError] = useState<string | null>(null);
  const [showAzureAdForm, setShowAzureAdForm] = useState(false);
  const [azureAdTenantId, setAzureAdTenantId] = useState("");
  const [azureAdFrontendClientId, setAzureAdFrontendClientId] = useState("");
  const [azureAdBackendClientId, setAzureAdBackendClientId] = useState("");
  const [azureAdBackendClientSecret, setAzureAdBackendClientSecret] = useState("");
  const [azureAdApiScope, setAzureAdApiScope] = useState("");
  const [azureAdSaving, setAzureAdSaving] = useState(false);
  const [azureAdSaveError, setAzureAdSaveError] = useState<string | null>(null);
  const [azureAdRestarting, setAzureAdRestarting] = useState(false);

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
    getUpdateStatus(instance)
      .then(setUpdateStatus)
      .catch((err) => setUpdateStatusError(err instanceof Error ? err.message : "Failed to load update status"));
  }, [instance, me?.isAdmin]);

  useEffect(() => {
    if (!me?.isAdmin) return;
    getAzureAdStatus(instance)
      .then((status) => {
        setAzureAdStatus(status);
        setAzureAdTenantId(status.tenantId ?? "");
        setAzureAdFrontendClientId(status.frontendClientId ?? "");
        setAzureAdBackendClientId(status.backendClientId ?? "");
        setAzureAdApiScope(status.apiScope ?? "");
      })
      .catch((err) => setAzureAdStatusError(err instanceof Error ? err.message : "Failed to load Azure AD status"));
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
    const tenantId = newCustomerTenantId.trim();
    if (!name || !tenantId) return;

    setAddingCustomer(true);
    setAddCustomerError(null);
    try {
      const created = await createCustomer(instance, name, tenantId);
      setCustomers((prev) => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)));
      setNewCustomerName("");
      setNewCustomerTenantId("");
      setShowAddCustomer(false);
    } catch (err) {
      setAddCustomerError(err instanceof Error ? err.message : "Failed to add customer");
    } finally {
      setAddingCustomer(false);
    }
  };

  // Polls the live progress feed (CollectionProgressTracker.cs) while the
  // collectCustomerData() request below is in flight — that request blocks
  // until the whole collection run finishes, so this is what lets Settings
  // show a terminal-style "checking user X of N" / "waiting on rate limit"
  // feed instead of just a spinner for however long the run takes.
  const pollCollectionProgress = async (customerId: number, isDone: () => boolean) => {
    let after = 0;
    while (!isDone()) {
      try {
        const { lines } = await getCollectionProgress(instance, customerId, after);
        if (lines.length > 0) {
          after = lines[lines.length - 1].seq;
          setProgressLines((prev) => ({ ...prev, [customerId]: [...(prev[customerId] ?? []), ...lines] }));
        }
      } catch {
        // Transient — the collect request's own result is authoritative, so
        // just keep polling rather than surfacing this as an error.
      }
      await new Promise((resolve) => setTimeout(resolve, 700));
    }
    // One last poll to catch any lines written just before the collect
    // request resolved (e.g. the final "Done" line).
    try {
      const { lines } = await getCollectionProgress(instance, customerId, after);
      if (lines.length > 0) {
        setProgressLines((prev) => ({ ...prev, [customerId]: [...(prev[customerId] ?? []), ...lines] }));
      }
    } catch {
      // Best-effort.
    }
  };

  const handleCollect = async (customerId: number) => {
    setCollectingId(customerId);
    setCollectError(null);
    setProgressLines((prev) => ({ ...prev, [customerId]: [] }));

    let done = false;
    const progressDone = pollCollectionProgress(customerId, () => done);
    try {
      const updated = await collectCustomerData(instance, customerId);
      setCustomers((prev) => prev.map((c) => (c.id === customerId ? updated : c)));
    } catch (err) {
      setCollectError(err instanceof Error ? err.message : "Failed to collect data");
    } finally {
      done = true;
      await progressDone;
      setCollectingId(null);
    }
  };

  // Runs CollectAllAsync for every customer in the background (see
  // BulkSyncStatusTracker.cs) — the POST returns immediately, so this polls
  // /sync-all/status for overall progress ("3 of 12: Acme Corp") and, for
  // whichever customer is currently active, chains into the same
  // per-customer terminal feed handleCollect uses above.
  const handleSyncAll = async () => {
    setSyncingAll(true);
    setSyncAllError(null);
    setSyncAllStatus(null);

    try {
      await syncAllCustomers(instance);
    } catch (err) {
      setSyncAllError(err instanceof Error ? err.message : "Failed to start sync");
      setSyncingAll(false);
      return;
    }

    let activeCustomerId: number | null = null;
    let after = 0;
    let running = true;
    while (running) {
      try {
        const status = await getSyncAllStatus(instance);
        setSyncAllStatus(status);
        running = status.isRunning;

        if (status.currentCustomerId !== null) {
          if (status.currentCustomerId !== activeCustomerId) {
            activeCustomerId = status.currentCustomerId;
            after = 0;
            setProgressLines((prev) => ({ ...prev, [activeCustomerId!]: [] }));
          }
          const { lines } = await getCollectionProgress(instance, activeCustomerId, after);
          if (lines.length > 0) {
            after = lines[lines.length - 1].seq;
            const id = activeCustomerId;
            setProgressLines((prev) => ({ ...prev, [id]: [...(prev[id] ?? []), ...lines] }));
          }
        }
      } catch {
        // Transient — keep polling until the run itself reports done.
      }
      if (running) {
        await new Promise((resolve) => setTimeout(resolve, 700));
      }
    }

    try {
      setCustomers(await getCustomers(instance));
    } catch (err) {
      setCustomersError(err instanceof Error ? err.message : "Failed to load customers");
    }
    setSyncingAll(false);
  };

  const handleGrantConsent = async (customerId: number) => {
    setConsentError(null);
    try {
      const { consentUrl } = await getConsentUrl(instance, customerId);
      // Deliberately no "noopener" here — ConsentCallback.tsx needs
      // window.opener to report the outcome back once Entra redirects this
      // popup. The target is always login.microsoftonline.com, a trusted
      // first-party origin, so keeping the opener link is safe.
      window.open(consentUrl, "_blank");
    } catch (err) {
      setConsentError(err instanceof Error ? err.message : "Failed to build the consent link");
    }
  };

  // Unlike consent above, there's no URL Spectra can just open — Azure RBAC
  // has no admin-consent flow, so the best available "one click" is copying
  // a ready-to-run az CLI command for the customer's Azure admin to paste
  // into Cloud Shell (or their own terminal, signed into that tenant with
  // Owner/User Access Administrator rights) themselves.
  const handleCopyAzureRoleCommand = async (customerId: number) => {
    setAzureCommandError(null);
    try {
      const { command } = await getAzureRoleCommand(instance, customerId);
      await navigator.clipboard.writeText(command);
      setAzureCommandCopiedId(customerId);
      setTimeout(() => setAzureCommandCopiedId((current) => (current === customerId ? null : current)), 2000);
    } catch (err) {
      setAzureCommandError(err instanceof Error ? err.message : "Failed to copy the Azure role command");
    }
  };

  // The consent tab (opened above) reports back here once Entra redirects it,
  // then closes itself — this is what lets Settings pick up a granted consent
  // and immediately retry collection without the admin doing anything else.
  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== CONSENT_MESSAGE_TYPE) {
        return;
      }
      const { customerId, success, error } = event.data as {
        customerId: number | null;
        success: boolean;
        error: string | null;
      };
      if (!customerId) return;
      if (success) {
        handleCollect(customerId);
      } else {
        setConsentError(error ?? "Consent was not granted.");
      }
    };
    window.addEventListener("message", handleMessage);
    return () => window.removeEventListener("message", handleMessage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [instance]);

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

  // The update itself restarts the backend (see README's "One-click update"),
  // so a request or two failing mid-poll is expected, not an error — only
  // stop once a successful response says the run has actually finished.
  const pollUpdateStatus = async () => {
    for (let attempt = 0; attempt < 60; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 4000));
      try {
        const status = await getUpdateStatus(instance);
        setUpdateStatus(status);
        if (status.status.state !== "running") return;
      } catch {
        // Backend is most likely mid-restart — keep polling.
      }
    }
  };

  const handleRequestUpdate = async () => {
    setRequestingUpdate(true);
    setRequestUpdateError(null);
    try {
      await requestUpdate(instance);
      const status = await getUpdateStatus(instance).catch(() => null);
      if (status) setUpdateStatus(status);
      await pollUpdateStatus();
    } catch (err) {
      setRequestUpdateError(err instanceof Error ? err.message : "Failed to request an update");
    } finally {
      setRequestingUpdate(false);
    }
  };

  // Saving restarts the backend (see AppUpdateService.RequestRestart's doc
  // comment for why) — which will sign out everyone currently signed in,
  // including whoever's making this change, so confirm before doing it.
  const handleSaveAzureAd = async (e: FormEvent) => {
    e.preventDefault();
    if (!window.confirm("Saving will restart the Spectra backend and sign out every current user, including you. Continue?")) {
      return;
    }
    setAzureAdSaving(true);
    setAzureAdSaveError(null);
    try {
      await saveAzureAdConfig(instance, {
        tenantId: azureAdTenantId.trim(),
        frontendClientId: azureAdFrontendClientId.trim(),
        backendClientId: azureAdBackendClientId.trim(),
        backendClientSecret: azureAdBackendClientSecret.trim(),
        apiScope: azureAdApiScope.trim(),
      });
      setAzureAdRestarting(true);
    } catch (err) {
      setAzureAdSaveError(err instanceof Error ? err.message : "Failed to save Azure AD configuration");
      setAzureAdSaving(false);
    }
  };

  // Defense in depth — the backend already rejects non-admins with a 403;
  // this just avoids flashing the page for someone who navigates here directly.
  if (!meLoading && me && !me.isAdmin) {
    return <Navigate to="/" replace />;
  }

  // The save that got us here already restarted the backend — the current
  // session's token was validated against the old Azure AD config and won't
  // survive the restart, so there's no point rendering the rest of Settings
  // (or anything else) until the user signs back in.
  if (azureAdRestarting) {
    return (
      <div className="dashboard-intro">
        <h1>Restarting…</h1>
        <p>Azure AD configuration saved — the backend is restarting to apply it. Sign in again once it's back.</p>
      </div>
    );
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
          <div className="settings-actions">
            <button className="btn btn-ghost btn-sm" onClick={handleSyncAll} disabled={syncingAll || customers.length === 0}>
              {syncingAll ? "Syncing…" : "Sync all now"}
            </button>
            {!showAddCustomer && (
              <button className="btn btn-primary btn-sm" onClick={() => setShowAddCustomer(true)}>
                + Add Customer
              </button>
            )}
          </div>
        </div>
        <p className="settings-hint">
          Adding a customer collects their Entra ID users immediately via app-only Graph access. Their tenant admin
          must grant consent to Spectra's app registration first — use "Grant consent" below if a collection fails.
        </p>

        {customersError && <p className="login-error">{customersError}</p>}
        {syncAllError && <p className="login-error">{syncAllError}</p>}
        {syncingAll && syncAllStatus && (
          <p className="fine-print">
            <span className="status-dot status-dot-checking" aria-hidden="true" />
            Syncing {Math.min(syncAllStatus.completed + 1, syncAllStatus.total)} of {syncAllStatus.total}
            {syncAllStatus.currentCustomerName && `: ${syncAllStatus.currentCustomerName}`}
          </p>
        )}

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
            <input
              type="text"
              className="text-input"
              placeholder="Entra tenant ID (GUID)…"
              value={newCustomerTenantId}
              onChange={(e) => setNewCustomerTenantId(e.target.value)}
            />
            <div className="settings-actions">
              <button
                type="submit"
                className="btn btn-primary"
                disabled={addingCustomer || !newCustomerName.trim() || !newCustomerTenantId.trim()}
              >
                {addingCustomer ? "Adding…" : "Add"}
              </button>
              <button
                type="button"
                className="btn btn-ghost"
                onClick={() => {
                  setShowAddCustomer(false);
                  setNewCustomerName("");
                  setNewCustomerTenantId("");
                  setAddCustomerError(null);
                }}
              >
                Cancel
              </button>
            </div>
            {addCustomerError && <p className="login-error">{addCustomerError}</p>}
          </form>
        )}

        {collectError && <p className="login-error">{collectError}</p>}
        {consentError && <p className="login-error">{consentError}</p>}
        {azureCommandError && <p className="login-error">{azureCommandError}</p>}

        {customers.length > 0 ? (
          <ul className="customer-list">
            {customers.map((customer) => (
              <li key={customer.id} className="customer-list-item customer-list-item-detailed">
                <div className="customer-list-item-row">
                  <div>
                    <span>{customer.name}</span>
                    <p className="fine-print">
                      Tenant {customer.tenantId} — added {new Date(customer.createdAt).toLocaleDateString()}
                    </p>
                    <p className="fine-print">
                      <span
                        className={`status-dot status-dot-${customer.consentGranted ? "connected" : "error"}`}
                        aria-hidden="true"
                      />
                      {customer.consentGranted ? "Consent granted" : "Consent not granted"}
                      {customer.lastSyncedAt && ` — last synced ${new Date(customer.lastSyncedAt).toLocaleString()}`}
                    </p>
                    {customer.lastSyncError && <p className="login-error">{customer.lastSyncError}</p>}
                  </div>
                  <div className="settings-actions">
                    <button className="btn btn-ghost btn-sm" onClick={() => handleGrantConsent(customer.id)}>
                      Grant consent
                    </button>
                    <button
                      className="btn btn-ghost btn-sm"
                      onClick={() => handleCopyAzureRoleCommand(customer.id)}
                      title="Copies an az CLI command that grants Spectra Reader access to every current and future Azure subscription in this tenant — run it once, signed into the customer's tenant with Owner/User Access Administrator rights."
                    >
                      {azureCommandCopiedId === customer.id ? "Copied!" : "Copy Azure access command"}
                    </button>
                    <button
                      className="btn btn-ghost btn-sm"
                      onClick={() => handleCollect(customer.id)}
                      disabled={collectingId === customer.id || syncingAll}
                    >
                      {collectingId === customer.id ? "Collecting…" : "Collect data"}
                    </button>
                  </div>
                </div>
                {(collectingId === customer.id || (syncingAll && syncAllStatus?.currentCustomerId === customer.id)) && (
                  <pre
                    className="collect-terminal"
                    ref={(el) => {
                      if (el) el.scrollTop = el.scrollHeight;
                    }}
                  >
                    {(progressLines[customer.id] ?? []).map((line) => `[${new Date(line.at).toLocaleTimeString()}] ${line.message}`).join("\n") ||
                      "Starting…"}
                  </pre>
                )}
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

      <div className="settings-panel">
        <h2>Authentication</h2>
        <p className="settings-hint">
          The Azure AD (Entra ID) app registrations Spectra signs in with and uses for per-customer data collection.
          Set once via the setup wizard on first run — change it here if you need to rotate the secret or fix a
          value. Saving restarts the backend and signs everyone out, including you.
        </p>

        {azureAdStatusError && <p className="login-error">{azureAdStatusError}</p>}

        {showAzureAdForm || !azureAdStatus?.configured ? (
          <form className="customer-add-form" onSubmit={handleSaveAzureAd}>
            <input
              type="text"
              className="text-input"
              placeholder="Tenant ID"
              value={azureAdTenantId}
              onChange={(e) => setAzureAdTenantId(e.target.value)}
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="Frontend app registration client ID"
              value={azureAdFrontendClientId}
              onChange={(e) => setAzureAdFrontendClientId(e.target.value)}
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="Backend app registration client ID"
              value={azureAdBackendClientId}
              onChange={(e) => setAzureAdBackendClientId(e.target.value)}
              required
            />
            <input
              type="password"
              className="text-input"
              placeholder={azureAdStatus?.hasSecret ? "Backend client secret (leave blank to keep the current one)" : "Backend client secret"}
              value={azureAdBackendClientSecret}
              onChange={(e) => setAzureAdBackendClientSecret(e.target.value)}
              autoComplete="new-password"
              required={!azureAdStatus?.hasSecret}
            />
            <input
              type="text"
              className="text-input"
              placeholder="API scope, e.g. api://<backend-client-id>/access_as_user"
              value={azureAdApiScope}
              onChange={(e) => setAzureAdApiScope(e.target.value)}
              required
            />
            <div className="settings-actions">
              <button type="submit" className="btn btn-primary" disabled={azureAdSaving}>
                {azureAdSaving ? "Saving…" : "Save and restart"}
              </button>
              {azureAdStatus?.configured && (
                <button type="button" className="btn btn-ghost" onClick={() => setShowAzureAdForm(false)}>
                  Cancel
                </button>
              )}
            </div>
            {azureAdSaveError && <p className="login-error">{azureAdSaveError}</p>}
          </form>
        ) : (
          <>
            <div className="db-status db-status-active">
              <span className="status-dot status-dot-connected" aria-hidden="true" />
              <div>
                <strong>Configured</strong>
                <p className="fine-print">
                  Tenant {azureAdStatus.tenantId} — last updated{" "}
                  {azureAdStatus.updatedAt ? new Date(azureAdStatus.updatedAt).toLocaleString() : "unknown"}
                  {azureAdStatus.updatedByEmail ? ` by ${azureAdStatus.updatedByEmail}` : ""}
                </p>
              </div>
            </div>
            <div className="settings-actions">
              <button className="btn btn-ghost" onClick={() => setShowAzureAdForm(true)}>
                Edit
              </button>
            </div>
          </>
        )}
      </div>

      <div className="settings-panel">
        <h2>Updates</h2>
        <p className="settings-hint">
          Pulls the latest code, rebuilds, and restarts the backend in place — only available on a server set up by{" "}
          <code>deploy/install.sh</code>. The backend process itself never gains any elevated privilege: this only
          requests an update, a separate root-owned service on the server does the actual work.
        </p>

        {updateStatusError && <p className="login-error">{updateStatusError}</p>}

        {updateStatus && updateStatus.status.state === "unavailable" ? (
          <p className="fine-print">Not available on this deployment — not installed via deploy/install.sh.</p>
        ) : updateStatus ? (
          <>
            {updateStatus.currentVersion ? (
              <p className="fine-print">
                Running <code>{updateStatus.currentVersion.commit.slice(0, 8)}</code> ({updateStatus.currentVersion.ref}) —
                deployed {new Date(updateStatus.currentVersion.deployedAt).toLocaleString()}
              </p>
            ) : (
              <p className="fine-print">Version unknown — no successful install/update recorded yet.</p>
            )}

            {updateStatus.updateAvailable === true && (
              <p className="fine-print">
                Update available: <code>{updateStatus.latestCommit?.slice(0, 8)}</code>
              </p>
            )}

            {updateStatus.status.state === "running" && (
              <p className="fine-print">
                <span className="status-dot status-dot-checking" aria-hidden="true" />
                Update in progress — this restarts the backend, the page may briefly disconnect.
              </p>
            )}
            {updateStatus.status.state === "succeeded" && updateStatus.status.finishedAt && (
              <p className="fine-print">
                <span className="status-dot status-dot-connected" aria-hidden="true" />
                Last update succeeded at {new Date(updateStatus.status.finishedAt).toLocaleString()}.
              </p>
            )}
            {updateStatus.status.state === "failed" && (
              <p className="login-error">Last update failed: {updateStatus.status.message}</p>
            )}

            <div className="settings-actions">
              <button
                className="btn btn-primary"
                onClick={handleRequestUpdate}
                disabled={requestingUpdate || updateStatus.status.state === "running"}
              >
                {requestingUpdate || updateStatus.status.state === "running" ? "Updating…" : "Update now"}
              </button>
            </div>
            {requestUpdateError && <p className="login-error">{requestUpdateError}</p>}
          </>
        ) : (
          !updateStatusError && <p className="fine-print">Loading…</p>
        )}
      </div>
    </>
  );
}
