import { useState } from "react";
import { useMsal } from "@azure/msal-react";
import { callApi } from "../api";

export default function Welcome() {
  const { instance, accounts } = useMsal();
  const account = accounts[0];
  const [meResult, setMeResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleSignOut = () => {
    instance.logoutPopup();
  };

  const handleCallApi = async () => {
    setError(null);
    setMeResult(null);
    try {
      const data = await callApi(instance, "/api/me");
      setMeResult(JSON.stringify(data, null, 2));
    } catch (err) {
      setError(err instanceof Error ? err.message : "API call failed");
    }
  };

  const displayName = account?.name ?? account?.username ?? "";
  const initials = displayName
    .split(/\s+/)
    .map((part) => part[0])
    .filter(Boolean)
    .slice(0, 2)
    .join("")
    .toUpperCase();

  return (
    <div className="welcome-panel">
      <div className="avatar">{initials || "?"}</div>
      <div className="welcome-heading">
        <h2>{account?.name ?? "Signed in"}</h2>
        <span>{account?.username}</span>
      </div>
      <div className="welcome-actions">
        <button className="btn btn-primary" onClick={handleCallApi}>
          Call API
        </button>
        <button className="btn btn-ghost" onClick={handleSignOut}>
          Sign out
        </button>
      </div>
      {meResult && <pre className="api-result">{meResult}</pre>}
      {error && <p className="login-error">{error}</p>}
    </div>
  );
}
