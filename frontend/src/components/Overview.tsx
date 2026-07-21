import { useMsal } from "@azure/msal-react";
import { useCurrentUser } from "../UserContext";

export default function Overview() {
  const { accounts } = useMsal();
  const account = accounts[0];
  const { loading, error } = useCurrentUser();

  const displayName = account?.name ?? account?.username ?? "";
  const firstName = displayName.split(/\s+/)[0] || "there";

  const apiStatus = loading ? "checking" : error ? "error" : "connected";
  const apiStatusLabel = loading ? "Checking…" : error ? "Unreachable" : "Connected";

  return (
    <>
      <div className="dashboard-intro">
        <h1>Welcome, {firstName}</h1>
        <p>Here's what's happening across your environment.</p>
      </div>

      <div className="kpi-row">
        <StatTile label="Devices" value="—" caption="Coming soon" />
        <StatTile label="Open alerts" value="—" caption="Coming soon" />
        <StatTile label="Backend" value={apiStatusLabel} caption="API status" status={apiStatus} />
      </div>

      <div className="panel-empty">
        <svg className="panel-empty-icon" viewBox="0 0 24 24" fill="none" aria-hidden="true">
          <path
            d="M4 19V5a1 1 0 0 1 1-1h10l5 5v10a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1Z"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinejoin="round"
          />
          <path d="M15 4v4a1 1 0 0 0 1 1h4" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" />
          <path d="M8 13h8M8 16.5h5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        <h2>Recent activity</h2>
        <p>Nothing to show yet — this is where live events will appear.</p>
      </div>
    </>
  );
}

function StatTile({
  label,
  value,
  caption,
  status,
}: {
  label: string;
  value: string;
  caption: string;
  status?: "checking" | "connected" | "error";
}) {
  return (
    <div className="stat-tile">
      <span className="stat-tile-label">{label}</span>
      <span className="stat-tile-value">
        {status && <span className={`status-dot status-dot-${status}`} aria-hidden="true" />}
        {value}
      </span>
      <span className="stat-tile-caption">{caption}</span>
    </div>
  );
}
