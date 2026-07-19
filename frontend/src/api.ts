import type { IPublicClientApplication } from "@azure/msal-browser";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { loginRequest } from "./authConfig";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;

async function getAccessToken(instance: IPublicClientApplication): Promise<string> {
  const account = instance.getActiveAccount();
  if (!account) {
    throw new Error("No active account — sign in first");
  }

  try {
    const result = await instance.acquireTokenSilent({ ...loginRequest, account });
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      const result = await instance.acquireTokenPopup(loginRequest);
      return result.accessToken;
    }
    throw err;
  }
}

export async function callApi(instance: IPublicClientApplication, path: string): Promise<unknown> {
  const token = await getAccessToken(instance);
  const response = await fetch(`${API_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new Error(`API call failed: ${response.status} ${response.statusText}`);
  }
  return response.json();
}
