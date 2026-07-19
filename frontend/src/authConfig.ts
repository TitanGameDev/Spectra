import type { Configuration } from "@azure/msal-browser";

const tenantId = import.meta.env.VITE_MSAL_TENANT_ID;

export const msalConfig: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_MSAL_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: import.meta.env.VITE_MSAL_REDIRECT_URI,
  },
  cache: {
    cacheLocation: "sessionStorage",
    storeAuthStateInCookie: false,
  },
};

// Scope for the backend API. Requested up front so the popup login
// also grants an access token we can attach to API calls.
export const loginRequest = {
  scopes: [import.meta.env.VITE_API_SCOPE],
};
