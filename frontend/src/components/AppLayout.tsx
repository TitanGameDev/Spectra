import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { NavLink, Outlet } from "react-router-dom";
import { fetchProfilePhoto } from "../api";
import { endLocalSession, getSessionRemainingMs } from "../session";
import { useCurrentUser } from "../UserContext";

const NAV_ITEMS = [
  { to: "/", label: "Overview", end: true },
  { to: "/users", label: "Users" },
  { to: "/security", label: "Security" },
  { to: "/intune", label: "Intune" },
  { to: "/azure", label: "Azure" },
];

export default function AppLayout() {
  const { instance, accounts } = useMsal();
  const account = accounts[0];
  const { me } = useCurrentUser();
  const [photoUrl, setPhotoUrl] = useState<string | null>(null);

  useEffect(() => {
    let objectUrl: string | null = null;
    let cancelled = false;

    fetchProfilePhoto(instance)
      .then((url) => {
        if (cancelled) return;
        objectUrl = url;
        setPhotoUrl(url);
      })
      .catch(() => {
        // No photo available (or Graph call failed) — fall back to initials.
      });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [instance]);

  // Absolute 1-hour session: force sign-out exactly 1 hour after login,
  // independent of activity.
  useEffect(() => {
    const remaining = getSessionRemainingMs();
    if (remaining <= 0) {
      endLocalSession();
      return;
    }
    const timer = setTimeout(endLocalSession, remaining);
    return () => clearTimeout(timer);
  }, []);

  const displayName = account?.name ?? account?.username ?? "";
  const initials = displayName
    .split(/\s+/)
    .map((part) => part[0])
    .filter(Boolean)
    .slice(0, 2)
    .join("")
    .toUpperCase();

  return (
    <div className="dashboard">
      <div className="dashboard-topbar">
        <header className="dashboard-header">
          <NavLink className="dashboard-brand" to="/">
            <div className="brand-mark brand-mark-sm" />
            <span>Spectra</span>
          </NavLink>
          <div className="dashboard-account">
            {me?.isAdmin && (
              <NavLink className="btn btn-ghost btn-sm" to="/settings">
                Settings
              </NavLink>
            )}
            {photoUrl ? (
              <img className="avatar avatar-sm avatar-photo" src={photoUrl} alt="" />
            ) : (
              <div className="avatar avatar-sm">{initials || "?"}</div>
            )}
            <span className="dashboard-account-name">{displayName}</span>
            <button className="btn btn-ghost btn-sm" onClick={endLocalSession}>
              Sign out
            </button>
          </div>
        </header>

        <nav className="tab-bar">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) => `tab-link${isActive ? " tab-link-active" : ""}`}
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
      </div>

      <main className="dashboard-main">
        <Outlet />
      </main>
    </div>
  );
}
