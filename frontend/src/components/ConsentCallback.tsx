import { useEffect, useState } from "react";

export const CONSENT_MESSAGE_TYPE = "spectra-consent-complete";

// Detects whether the current URL is Entra's redirect back from the admin
// consent flow (see Settings' "Grant consent" button) — this always opens in
// a brand-new tab, which has no MSAL session of its own (tokens live in
// sessionStorage, scoped per-tab), so this has to work without being signed in.
export function isConsentCallback(): boolean {
  const params = new URLSearchParams(window.location.search);
  return params.has("admin_consent") || (params.has("error") && params.has("state"));
}

// Renders in that new tab: reports the outcome back to the Settings tab that
// opened it via postMessage, then closes itself — nothing here needs a login.
export default function ConsentCallback() {
  const [closed, setClosed] = useState(false);

  const params = new URLSearchParams(window.location.search);
  const customerId = params.has("state") ? Number(params.get("state")) : null;
  const success = params.get("admin_consent") === "True" && !params.get("error");
  const error = params.get("error_description") ?? params.get("error");

  useEffect(() => {
    window.opener?.postMessage(
      { type: CONSENT_MESSAGE_TYPE, customerId, success, error },
      window.location.origin,
    );

    const timer = setTimeout(() => {
      window.close();
      // If the browser blocks a script-initiated close (some do, depending on
      // how the tab was opened), fall back to telling the user to close it.
      setClosed(true);
    }, 1200);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="centered-viewport">
      <main className="app-shell">
        <div className="brand">
          <div className="brand-mark" />
          <h1>Spectra</h1>
          <p>{success ? "Consent granted" : "Consent failed"}</p>
        </div>

        <div className="login-panel">
          {!success && error && <p className="login-error">{error}</p>}
          <p className="fine-print">{closed ? "You can close this tab." : "Closing this tab…"}</p>
          {closed && (
            <button className="btn btn-ghost" onClick={() => window.close()}>
              Close this tab
            </button>
          )}
        </div>
      </main>
    </div>
  );
}
