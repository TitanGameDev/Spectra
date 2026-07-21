import { LogLevel, type Configuration } from "@azure/msal-browser";

const tenantId = import.meta.env.VITE_MSAL_TENANT_ID;

export const msalConfig: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_MSAL_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: import.meta.env.VITE_MSAL_REDIRECT_URI,
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

export const apiScopes = [import.meta.env.VITE_API_SCOPE];

// Microsoft Graph, used only to fetch the signed-in user's profile photo.
export const graphScopes = ["User.Read"];

// Requested up front so the popup login grants consent for both resources
// at once; each resource still gets its own access token via acquireTokenSilent.
export const loginRequest = {
  scopes: [...apiScopes, ...graphScopes],
};

// Deliberately NOT in loginRequest — these are elevated, tenant-wide read
// permissions (searching groups in Settings, listing users on the Users tab),
// so they're requested on demand (incremental consent) rather than prompting
// every user for them at sign-in.
export const directoryScopes = ["Group.Read.All", "User.Read.All"];
