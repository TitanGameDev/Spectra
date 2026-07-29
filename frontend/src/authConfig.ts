import { LogLevel, type Configuration } from "@azure/msal-browser";

export interface AuthConfigResponse {
  configured: boolean;
  clientId: string | null;
  tenantId: string | null;
  apiScope: string | null;
}

// Fetched once, before main.tsx ever constructs a PublicClientApplication —
// clientId/tenantId/apiScope used to live in build-time VITE_* env vars, but
// that meant changing them required a full rebuild+redeploy. Now they're
// stored in the database and set through the setup wizard (or Settings ->
// Authentication later), so the frontend has to ask the backend for them at
// load time instead.
export async function fetchAuthConfig(): Promise<AuthConfigResponse> {
  const base = import.meta.env.VITE_API_BASE_URL ?? "";
  const response = await fetch(`${base}/api/public/auth-config`);
  if (!response.ok) {
    throw new Error(`Couldn't reach the Spectra server (${response.status}).`);
  }
  return response.json();
}

export function buildMsalConfig(clientId: string, tenantId: string): Configuration {
  return {
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId}`,
      // Was a VITE_MSAL_REDIRECT_URI build-time value — window.location.origin
      // always matches whichever origin is actually running, and
      // setup-azure-ad.sh already registers both localhost:5173 and the
      // production domain as SPA redirect URIs, so either one just works.
      redirectUri: window.location.origin,
    },
    cache: {
      // sessionStorage, not localStorage — tokens don't persist past the tab/browser
      // session, and aren't shared across tabs, which limits exposure if the page
      // is ever compromised via XSS.
      cacheLocation: "sessionStorage",
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        piiLoggingEnabled: false,
        logLevel: LogLevel.Warning,
        loggerCallback: (level, message) => {
          if (level === LogLevel.Error || level === LogLevel.Warning) {
            console.warn(message);
          }
        },
      },
    },
  };
}

// Set once in main.tsx right after fetchAuthConfig resolves, before
// MsalProvider/App render — simpler than threading the value through React
// context purely so api.ts's plain functions (not components) can read it.
let currentApiScope: string | null = null;

export function setApiScope(scope: string) {
  currentApiScope = scope;
}

export function getApiScopes(): string[] {
  if (!currentApiScope) {
    throw new Error("apiScope isn't set yet — fetchAuthConfig must resolve before this is called.");
  }
  return [currentApiScope];
}

// Microsoft Graph, used only to fetch the signed-in user's profile photo.
export const graphScopes = ["User.Read"];

// Requested up front so the popup login grants consent for both resources
// at once; each resource still gets its own access token via acquireTokenSilent.
export function getLoginRequest() {
  return { scopes: [...getApiScopes(), ...graphScopes] };
}

// Deliberately NOT in getLoginRequest() — these are elevated, tenant-wide read
// permissions (searching groups in Settings, listing users on the Users tab),
// so they're requested on demand (incremental consent) rather than prompting
// every user for them at sign-in.
export const directoryScopes = ["Group.Read.All", "User.Read.All"];
