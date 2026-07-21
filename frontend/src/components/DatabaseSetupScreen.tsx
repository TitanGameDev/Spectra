import { useState } from "react";
import { useMsal } from "@azure/msal-react";
import { resetToSqlite, type SystemStatus } from "../api";

export default function DatabaseSetupScreen({
  status,
  onResolved,
}: {
  status: SystemStatus;
  onResolved: () => void;
}) {
  const { instance } = useMsal();
  const [resetting, setResetting] = useState(false);
  const [resetError, setResetError] = useState<string | null>(null);

  const handleReset = async () => {
    setResetting(true);
    setResetError(null);
    try {
      await resetToSqlite(instance);
      onResolved();
    } catch (err) {
      setResetError(err instanceof Error ? err.message : "Failed to reset the database");
    } finally {
      setResetting(false);
    }
  };

  return (
    <div className="centered-viewport">
      <main className="app-shell">
        <div className="brand">
          <div className="brand-mark" />
          <h1>Spectra</h1>
          <p>Can't reach the database</p>
        </div>

        <div className="login-panel">
          <p className="fine-print">
            Spectra is configured to use an external database, but couldn't connect to it just now — the server may
            be down, credentials may have changed, or the database itself may no longer exist.
          </p>
          {status.databaseError && <p className="login-error">{status.databaseError}</p>}

          <button className="btn btn-primary" onClick={handleReset} disabled={resetting}>
            {resetting ? "Resetting…" : "Reset to local database"}
          </button>
          <p className="fine-print">
            This switches Spectra back to a local SQLite database so you can get back in — you can reconnect to SQL
            Server or MySQL again afterward from Settings.
          </p>
          {resetError && <p className="login-error">{resetError}</p>}
        </div>
      </main>
    </div>
  );
}
