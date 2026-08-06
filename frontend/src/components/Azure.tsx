import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import {
  getCustomerAzureResources,
  type AzureResourceInfo,
  type AzureVirtualMachine,
  type AzureAppService,
  type AzureReservation,
  type EntraAppRegistration,
  type EntraServicePrincipal,
} from "../api";
import { useCustomer } from "../CustomerContext";
import { SortableHeader, sortRows, toggleSort, type SortState, type SortValue } from "../sorting";

const SUB_TABS = [
  { key: "overview", label: "Overview" },
  { key: "vms", label: "Virtual Machines" },
  { key: "app-services", label: "App Services" },
  { key: "reservations", label: "Reservations" },
  { key: "entra-apps", label: "Entra Apps" },
] as const;

type SubTabKey = (typeof SUB_TABS)[number]["key"];

const VM_ACCESSORS: Record<string, (v: AzureVirtualMachine) => SortValue> = {
  name: (v) => v.name,
  subscription: (v) => v.subscriptionName,
  resourceGroup: (v) => v.resourceGroup,
  size: (v) => v.vmSize ?? "",
  os: (v) => v.osType ?? "",
  power: (v) => v.powerState ?? "",
  location: (v) => v.location,
};

const APP_SERVICE_ACCESSORS: Record<string, (a: AzureAppService) => SortValue> = {
  name: (a) => a.name,
  subscription: (a) => a.subscriptionName,
  resourceGroup: (a) => a.resourceGroup,
  kind: (a) => a.kind ?? "",
  state: (a) => a.state ?? "",
  location: (a) => a.location,
};

const RESERVATION_ACCESSORS: Record<string, (r: AzureReservation) => SortValue> = {
  displayName: (r) => r.displayName ?? "",
  sku: (r) => r.skuName ?? "",
  quantity: (r) => r.quantity,
  term: (r) => r.term ?? "",
  state: (r) => r.provisioningState ?? "",
  expiry: (r) => (r.expiryDateTime ? new Date(r.expiryDateTime).getTime() : null),
  scope: (r) => r.appliedScopeType ?? "",
};

const APP_REGISTRATION_ACCESSORS: Record<string, (a: EntraAppRegistration) => SortValue> = {
  name: (a) => a.displayName ?? "",
  appId: (a) => a.appId,
  audience: (a) => a.signInAudience ?? "",
  created: (a) => (a.createdDateTime ? new Date(a.createdDateTime).getTime() : null),
  expiry: (a) => (a.soonestCredentialExpiry ? new Date(a.soonestCredentialExpiry).getTime() : null),
};

const SERVICE_PRINCIPAL_ACCESSORS: Record<string, (s: EntraServicePrincipal) => SortValue> = {
  name: (s) => s.displayName ?? "",
  appId: (s) => s.appId,
  type: (s) => s.servicePrincipalType ?? "",
  created: (s) => (s.createdDateTime ? new Date(s.createdDateTime).getTime() : null),
};

function StatTile({ label, value, caption }: { label: string; value: string; caption: string }) {
  return (
    <div className="stat-tile">
      <span className="stat-tile-label">{label}</span>
      <span className="stat-tile-value">{value}</span>
      <span className="stat-tile-caption">{caption}</span>
    </div>
  );
}

// A credential expiring within 30 days gets flagged the same way the EXO
// certificate banner elsewhere in the app flags its own expiry window.
function isExpiringSoon(iso: string | null): boolean {
  if (!iso) return false;
  const days = (new Date(iso).getTime() - Date.now()) / (1000 * 60 * 60 * 24);
  return days <= 30;
}

export default function Azure() {
  const { instance } = useMsal();
  const { customers, selectedCustomerId, loading: customersLoading } = useCustomer();
  const [azure, setAzure] = useState<AzureResourceInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [subTab, setSubTab] = useState<SubTabKey>("overview");
  const [sort, setSort] = useState<SortState<string> | null>(null);

  const selectedCustomer = customers.find((c) => c.id === selectedCustomerId);

  useEffect(() => {
    if (!selectedCustomerId) {
      setAzure(null);
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    getCustomerAzureResources(instance, selectedCustomerId)
      .then((data) => {
        if (!cancelled) setAzure(data);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load Azure data");
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [instance, selectedCustomerId]);

  const handleSort = (key: string) => setSort((prev) => toggleSort(prev, key));

  const hasAnyResourceData = (azure?.subscriptions.length ?? 0) > 0;

  return (
    <>
      <div className="dashboard-intro">
        <h1>Azure</h1>
        <p>{selectedCustomer ? `Azure resources for ${selectedCustomer.name}.` : "Azure resources for your selected customer."}</p>
      </div>

      {!customersLoading && customers.length === 0 ? (
        <div className="panel-empty">
          <p>No customers yet. Add one under Settings to start collecting Azure data.</p>
        </div>
      ) : !customersLoading && !selectedCustomerId ? (
        <div className="panel-empty">
          <p>Select a customer from the switcher above to see their Azure resources.</p>
        </div>
      ) : (
        <>
          {error && <p className="login-error">{error}</p>}

          {loading ? (
            <p className="fine-print">Loading Azure data…</p>
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

              {!hasAnyResourceData && subTab !== "reservations" && subTab !== "entra-apps" && (
                <p className="fine-print">
                  No subscriptions found — this needs an Azure admin to assign the <strong>Reader</strong> role to
                  Spectra's app on the customer's subscription(s) via Access Control (IAM). This is a separate step
                  from Entra admin consent used elsewhere — see the README. {azure?.azureLastError && `Most recent attempt: ${azure.azureLastError}`}
                </p>
              )}

              {subTab === "overview" && (
                <div className="kpi-row">
                  <StatTile label="Subscriptions" value={String(azure?.subscriptions.length ?? 0)} caption="with Reader access" />
                  <StatTile label="Virtual Machines" value={String(azure?.virtualMachines.length ?? 0)} caption="across all subscriptions" />
                  <StatTile label="App Services" value={String(azure?.appServices.length ?? 0)} caption="Web/Function apps" />
                  <StatTile label="Reservations" value={String(azure?.reservations.length ?? 0)} caption="active or expired" />
                </div>
              )}

              {subTab === "vms" && (
                <>
                  {(azure?.virtualMachines.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>No virtual machines found for this customer.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Subscription" columnKey="subscription" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Resource Group" columnKey="resourceGroup" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Size" columnKey="size" sort={sort} onSort={handleSort} />
                            <SortableHeader label="OS" columnKey="os" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Power state" columnKey="power" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Location" columnKey="location" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(azure?.virtualMachines ?? [], sort, VM_ACCESSORS).map((vm, i) => (
                            <tr key={`${vm.subscriptionId}-${vm.resourceGroup}-${vm.name}-${i}`}>
                              <td>{vm.name}</td>
                              <td>{vm.subscriptionName}</td>
                              <td>{vm.resourceGroup}</td>
                              <td>{vm.vmSize ?? "—"}</td>
                              <td>{vm.osType ?? "—"}</td>
                              <td>
                                <span
                                  className={`status-dot status-dot-${vm.powerState?.toLowerCase() === "running" ? "connected" : vm.powerState ? "checking" : "error"}`}
                                />
                                {vm.powerState ?? "Unknown"}
                              </td>
                              <td>{vm.location}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "app-services" && (
                <>
                  {(azure?.appServices.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>No App Services found for this customer.</p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Subscription" columnKey="subscription" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Resource Group" columnKey="resourceGroup" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Kind" columnKey="kind" sort={sort} onSort={handleSort} />
                            <SortableHeader label="State" columnKey="state" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Location" columnKey="location" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(azure?.appServices ?? [], sort, APP_SERVICE_ACCESSORS).map((a, i) => (
                            <tr key={`${a.subscriptionId}-${a.resourceGroup}-${a.name}-${i}`}>
                              <td>{a.name}</td>
                              <td>{a.subscriptionName}</td>
                              <td>{a.resourceGroup}</td>
                              <td>{a.kind ?? "—"}</td>
                              <td>
                                <span className={`status-dot status-dot-${a.state === "Running" ? "connected" : "checking"}`} />
                                {a.state ?? "Unknown"}
                              </td>
                              <td>{a.location}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "reservations" && (
                <>
                  {(azure?.reservations.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        {(azure?.virtualMachines.length ?? 0) === 0 ? (
                          "No Azure virtual machines on this tenant, so reservations aren't collected — nothing to reserve capacity for."
                        ) : (
                          <>
                            No reservation data yet — this needs a separate, tenant-scoped <strong>Reservations Reader</strong>{" "}
                            role, assigned via Azure Portal → Home → Reservations → Role assignment. This is a different
                            screen from the subscription Reader role used for VMs/App Services above — see the README.
                          </>
                        )}
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="displayName" sort={sort} onSort={handleSort} />
                            <SortableHeader label="SKU" columnKey="sku" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Quantity" columnKey="quantity" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Term" columnKey="term" sort={sort} onSort={handleSort} />
                            <SortableHeader label="State" columnKey="state" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Expiry" columnKey="expiry" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Applied scope" columnKey="scope" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(azure?.reservations ?? [], sort, RESERVATION_ACCESSORS).map((r, i) => (
                            <tr key={`${r.reservationOrderId}-${i}`}>
                              <td>{r.displayName ?? "—"}</td>
                              <td>{r.skuName ?? "—"}</td>
                              <td>{r.quantity}</td>
                              <td>{r.term ?? "—"}</td>
                              <td>{r.provisioningState ?? "—"}</td>
                              <td>{r.expiryDateTime ? new Date(r.expiryDateTime).toLocaleDateString() : "—"}</td>
                              <td>{r.appliedScopeType ?? "—"}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {subTab === "entra-apps" && (
                <>
                  <h3 className="subsection-heading">App Registrations</h3>
                  {(azure?.entraAppRegistrations.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No app registration data found, or Application.Read.All isn't granted yet — this is a new
                        permission, so existing customers need to hit <strong>Grant consent</strong> again in
                        Settings. See the README.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="App ID" columnKey="appId" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Sign-in audience" columnKey="audience" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Created" columnKey="created" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Soonest credential expiry" columnKey="expiry" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(azure?.entraAppRegistrations ?? [], sort, APP_REGISTRATION_ACCESSORS).map((a) => (
                            <tr key={a.id}>
                              <td>{a.displayName ?? "—"}</td>
                              <td>{a.appId}</td>
                              <td>{a.signInAudience ?? "—"}</td>
                              <td>{a.createdDateTime ? new Date(a.createdDateTime).toLocaleDateString() : "—"}</td>
                              <td>
                                {a.soonestCredentialExpiry ? (
                                  <>
                                    {isExpiringSoon(a.soonestCredentialExpiry) && <span className="status-dot status-dot-error" />}
                                    {new Date(a.soonestCredentialExpiry).toLocaleDateString()}
                                  </>
                                ) : (
                                  "—"
                                )}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  <h3 className="subsection-heading">Enterprise Applications</h3>
                  {(azure?.entraServicePrincipals.length ?? 0) === 0 ? (
                    <div className="panel-empty">
                      <p>
                        No enterprise application data found, or Application.Read.All isn't granted yet — see the
                        note above.
                      </p>
                    </div>
                  ) : (
                    <div className="data-table-wrap">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <SortableHeader label="Name" columnKey="name" sort={sort} onSort={handleSort} />
                            <SortableHeader label="App ID" columnKey="appId" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Type" columnKey="type" sort={sort} onSort={handleSort} />
                            <SortableHeader label="Created" columnKey="created" sort={sort} onSort={handleSort} />
                          </tr>
                        </thead>
                        <tbody>
                          {sortRows(azure?.entraServicePrincipals ?? [], sort, SERVICE_PRINCIPAL_ACCESSORS).map((s) => (
                            <tr key={s.id}>
                              <td>{s.displayName ?? "—"}</td>
                              <td>{s.appId}</td>
                              <td>{s.servicePrincipalType ?? "—"}</td>
                              <td>{s.createdDateTime ? new Date(s.createdDateTime).toLocaleDateString() : "—"}</td>
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
