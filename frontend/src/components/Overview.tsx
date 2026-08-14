import { useMsal } from "@azure/msal-react";
import { useCurrentUser } from "../UserContext";
import type { MeResponse } from "../api";

export default function Overview() {
  const { accounts } = useMsal();
  const account = accounts[0];
  const { me, loading, error } = useCurrentUser();

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

      {!loading && me && !me.isAdmin && <AdminAccessHint diagnostics={me.adminDiagnostics} />}

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

// Low-key by design (a <details> disclosure, not an alert box) — every
// non-admin user would see this on every visit otherwise, most of whom
// never expected admin access in the first place. Only useful to someone
// actively wondering "why don't I have admin access" — see
// SpectraClaimsTransformation.cs for where these diagnostics come from.
function AdminAccessHint({ diagnostics }: { diagnostics: MeResponse["adminDiagnostics"] }) {
  return (
    <details className="admin-access-hint">
      <summary>Expecting admin access?</summary>
      {diagnostics.groupsOverageDetected ? (
        <p className="fine-print">
          Your account belongs to too many Microsoft 365 groups (200+) for Entra ID to include them all when you
          sign in, so Spectra can't check your admin group membership this way — this needs a fix on Spectra's side,
          let your Spectra admin know.
        </p>
      ) : diagnostics.groupsClaimCount === 0 ? (
        <p className="fine-print">
          Your sign-in doesn't include any group memberships yet. If you were recently added to Spectra's admin
          group, try signing out completely and back in first — group membership only takes effect on your next
          sign-in, not immediately.
        </p>
      ) : (
        <p className="fine-print">
          Your sign-in includes {diagnostics.groupsClaimCount} group{diagnostics.groupsClaimCount === 1 ? "" : "s"},
          but not the one configured for Spectra admin access. If you were recently added, try signing out completely
          and back in first — otherwise check with your Spectra admin that you've been added to the right group.
        </p>
      )}
    </details>
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
