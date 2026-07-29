import { useState, type FormEvent } from "react";
import { fetchAuthConfig } from "../authConfig";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";

interface SetupWizardProps {
  onComplete: () => void;
}

// Renders instead of the normal login screen when /api/public/auth-config
// reports configured: false — no MsalProvider exists yet at this point (it
// can't, there's nothing to construct one with), so this talks to the backend
// via a plain fetch, not the api.ts/apiFetch helpers that need an MSAL
// instance. Usually finds things already done by the time anyone sees it —
// deploy/setup-azure-ad.sh can POST directly to /api/setup/azure-ad using the
// same token — but this manual path always has to exist too, for anyone
// without network access from that script to the server, or who wants to
// redo it by hand.
export default function SetupWizard({ onComplete }: SetupWizardProps) {
  const [setupToken, setSetupToken] = useState("");
  const [tenantId, setTenantId] = useState("");
  const [frontendClientId, setFrontendClientId] = useState("");
  const [backendClientId, setBackendClientId] = useState("");
  const [backendClientSecret, setBackendClientSecret] = useState("");
  const [apiScope, setApiScope] = useState("");
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [restarting, setRestarting] = useState(false);

  // The save itself restarts the backend (see AppUpdateService.RequestRestart),
  // so the very next request or two failing is expected, not an error — only
  // stop once /api/public/auth-config actually reports configured: true.
  const pollUntilConfigured = async () => {
    for (let attempt = 0; attempt < 30; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 2000));
      try {
        const config = await fetchAuthConfig();
        if (config.configured) {
          onComplete();
          return;
        }
      } catch {
        // Backend is most likely mid-restart — keep polling.
      }
    }
    setSaveError("The server didn't come back up in time — reload this page in a moment to check.");
    setRestarting(false);
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setSaveError(null);
    try {
      const response = await fetch(`${API_BASE_URL}/api/setup/azure-ad`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          setupToken: setupToken.trim(),
          tenantId: tenantId.trim(),
          frontendClientId: frontendClientId.trim(),
          backendClientId: backendClientId.trim(),
          backendClientSecret: backendClientSecret.trim(),
          apiScope: apiScope.trim(),
        }),
      });
      if (!response.ok) {
        let message = `Setup failed: ${response.status} ${response.statusText}`;
        try {
          const body = await response.json();
          if (typeof body?.error === "string") message = body.error;
        } catch {
          // Response wasn't JSON — stick with the generic message.
        }
        throw new Error(message);
      }
      setSaving(false);
      setRestarting(true);
      await pollUntilConfigured();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Setup failed");
      setSaving(false);
    }
  };

  return (
    <div className="centered-viewport">
      <main className="app-shell">
        <div className="brand">
          <div className="brand-mark" />
          <h1>Spectra</h1>
          <p>First-time setup — connect your Azure AD app registrations</p>
        </div>

        {restarting ? (
          <p className="fine-print">
            <span className="status-dot status-dot-checking" aria-hidden="true" />
            Saved — restarting the server to apply it. This page will reload automatically.
          </p>
        ) : (
          <form className="customer-add-form" onSubmit={handleSubmit}>
            <input
              type="password"
              className="text-input"
              placeholder="Setup token (from /etc/spectra/setup-token.txt)"
              value={setupToken}
              onChange={(e) => setSetupToken(e.target.value)}
              autoComplete="off"
            />
            <input
              type="text"
              className="text-input"
              placeholder="Tenant ID"
              value={tenantId}
              onChange={(e) => setTenantId(e.target.value)}
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="Frontend app registration client ID"
              value={frontendClientId}
              onChange={(e) => setFrontendClientId(e.target.value)}
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="Backend app registration client ID"
              value={backendClientId}
              onChange={(e) => setBackendClientId(e.target.value)}
              required
            />
            <input
              type="password"
              className="text-input"
              placeholder="Backend app registration client secret"
              value={backendClientSecret}
              onChange={(e) => setBackendClientSecret(e.target.value)}
              autoComplete="new-password"
              required
            />
            <input
              type="text"
              className="text-input"
              placeholder="API scope, e.g. api://<backend-client-id>/access_as_user"
              value={apiScope}
              onChange={(e) => setApiScope(e.target.value)}
              required
            />
            <div className="settings-actions">
              <button type="submit" className="btn btn-primary" disabled={saving}>
                {saving ? "Saving…" : "Save and restart"}
              </button>
            </div>
            {saveError && <p className="login-error">{saveError}</p>}
            <p className="fine-print">
              Run <code>deploy/setup-azure-ad.sh</code> to create these app registrations and get these values — it can
              also POST them here directly (set <code>SPECTRA_INSTANCE_URL</code>), skipping this form entirely.
            </p>
          </form>
        )}
      </main>
    </div>
  );
}
