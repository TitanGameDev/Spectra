import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { Link, Navigate } from "react-router-dom";
import { getSettings, updateSettings, searchGroups, type GraphGroup, type SettingsResponse } from "../api";
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

  // Defense in depth — the backend already rejects non-admins with a 403;
  // this just avoids flashing the page for someone who navigates here directly.
  if (!meLoading && me && !me.isAdmin) {
    return <Navigate to="/" replace />;
  }

  return (
    <div className="dashboard">
      <header className="dashboard-header">
        <div className="dashboard-brand">
          <div className="brand-mark brand-mark-sm" />
          <span>Spectra</span>
        </div>
        <Link className="btn btn-ghost btn-sm" to="/">
          Back to dashboard
        </Link>
      </header>

      <main className="dashboard-main">
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
      </main>
    </div>
  );
}
