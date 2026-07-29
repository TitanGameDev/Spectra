import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { PublicClientApplication, EventType } from "@azure/msal-browser";
import { MsalProvider } from "@azure/msal-react";
import { fetchAuthConfig, buildMsalConfig, setApiScope } from "./authConfig";
import { markSessionStarted } from "./session";
import SetupWizard from "./components/SetupWizard";
import App from "./App";
import "./index.css";

async function bootstrap() {
  const root = createRoot(document.getElementById("root")!);
  root.render(
    <StrictMode>
      <div className="centered-viewport">
        <p className="fine-print">Loading…</p>
      </div>
    </StrictMode>,
  );

  let authConfig;
  try {
    authConfig = await fetchAuthConfig();
  } catch (err) {
    root.render(
      <StrictMode>
        <div className="centered-viewport">
          <p className="login-error">
            Couldn't reach the Spectra server. {err instanceof Error ? err.message : String(err)}
          </p>
        </div>
      </StrictMode>,
    );
    return;
  }

  if (!authConfig.configured || !authConfig.clientId || !authConfig.tenantId || !authConfig.apiScope) {
    root.render(
      <StrictMode>
        <SetupWizard onComplete={() => window.location.reload()} />
      </StrictMode>,
    );
    return;
  }

  setApiScope(authConfig.apiScope);
  const msalInstance = new PublicClientApplication(buildMsalConfig(authConfig.clientId, authConfig.tenantId));

  if (!msalInstance.getActiveAccount() && msalInstance.getAllAccounts().length > 0) {
    msalInstance.setActiveAccount(msalInstance.getAllAccounts()[0]);
  }

  msalInstance.addEventCallback((event) => {
    if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
      const account = (event.payload as { account?: import("@azure/msal-browser").AccountInfo }).account;
      if (account) {
        msalInstance.setActiveAccount(account);
        // Only an interactive login fires this event (unlike silent token
        // refreshes), so this is the true start of the 1-hour session clock.
        markSessionStarted();
      }
    }
  });

  root.render(
    <StrictMode>
      <MsalProvider instance={msalInstance}>
        <App />
      </MsalProvider>
    </StrictMode>,
  );
}

bootstrap();
