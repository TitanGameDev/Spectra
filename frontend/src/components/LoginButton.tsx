import { useState } from "react";
import { useMsal } from "@azure/msal-react";
import { InteractionStatus } from "@azure/msal-browser";
import { getLoginRequest } from "../authConfig";

function MicrosoftLogo() {
  return (
    <svg className="ms-logo" width="18" height="18" viewBox="0 0 21 21" aria-hidden="true">
      <rect x="1" y="1" width="9" height="9" fill="#F25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7FBA00" />
      <rect x="1" y="11" width="9" height="9" fill="#00A4EF" />
      <rect x="11" y="11" width="9" height="9" fill="#FFB900" />
    </svg>
  );
}

export default function LoginButton() {
  const { instance, inProgress } = useMsal();
  const [error, setError] = useState<string | null>(null);

  const handleSignIn = async () => {
    setError(null);
    try {
      // Popup only, triggered by this click — no silent/redirect SSO.
      await instance.loginPopup(getLoginRequest());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Sign-in failed");
    }
  };

  return (
    <div className="login-panel">
      <button
        className="ms-signin-button"
        onClick={handleSignIn}
        disabled={inProgress !== InteractionStatus.None}
      >
        <MicrosoftLogo />
        {inProgress !== InteractionStatus.None ? "Signing in…" : "Sign in with Microsoft"}
      </button>
      <p className="fine-print">Opens a popup — no automatic sign-in.</p>
      {error && <p className="login-error">{error}</p>}
    </div>
  );
}
