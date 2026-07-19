import type { IPublicClientApplication } from "@azure/msal-browser";
import { InteractionRequiredAuthError } from "@azure/msal-browser";
import { apiScopes, directoryScopes, graphScopes } from "./authConfig";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;
const GRAPH_BASE_URL = "https://graph.microsoft.com/v1.0";

async function acquireToken(instance: IPublicClientApplication, scopes: string[]): Promise<string> {
  const account = instance.getActiveAccount();
  if (!account) {
    throw new Error("No active account — sign in first");
  }

  try {
    const result = await instance.acquireTokenSilent({ scopes, account });
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      const result = await instance.acquireTokenPopup({ scopes });
      return result.accessToken;
    }
    throw err;
  }
}

async function apiFetch<T>(instance: IPublicClientApplication, path: string, init?: RequestInit): Promise<T> {
  const token = await acquireToken(instance, apiScopes);
  const response = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    headers: {
      ...(init?.headers ?? {}),
      Authorization: `Bearer ${token}`,
    },
  });
  if (!response.ok) {
    throw new Error(`API call failed: ${response.status} ${response.statusText}`);
  }
  return response.json();
}

export function callApi<T>(instance: IPublicClientApplication, path: string): Promise<T> {
  return apiFetch<T>(instance, path);
}

export interface MeResponse {
  name: string | null;
  email: string | null;
  isAdmin: boolean;
}

export function getMe(instance: IPublicClientApplication): Promise<MeResponse> {
  return apiFetch<MeResponse>(instance, "/api/me");
}

export interface SettingsResponse {
  adminGroupId: string | null;
  adminGroupDisplayName: string | null;
  updatedAt: string | null;
  updatedByEmail: string | null;
}

export function getSettings(instance: IPublicClientApplication): Promise<SettingsResponse> {
  return apiFetch<SettingsResponse>(instance, "/api/settings");
}

export function updateSettings(
  instance: IPublicClientApplication,
  body: { adminGroupId: string | null; adminGroupDisplayName: string | null },
): Promise<SettingsResponse> {
  return apiFetch<SettingsResponse>(instance, "/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

// Object URL for the user's Microsoft profile photo, or null if they don't have one set
// (Graph returns 404 in that case — not an error, just "no photo").
export async function fetchProfilePhoto(instance: IPublicClientApplication): Promise<string | null> {
  const token = await acquireToken(instance, graphScopes);
  const response = await fetch(`${GRAPH_BASE_URL}/me/photo/$value`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    return null;
  }
  const blob = await response.blob();
  return URL.createObjectURL(blob);
}

export interface GraphGroup {
  id: string;
  displayName: string;
}

// Searches the tenant's security groups by name. First call of a session
// triggers a one-time consent popup for Group.Read.All (see authConfig.ts) —
// only admins opening Settings ever hit this path.
export async function searchGroups(instance: IPublicClientApplication, query: string): Promise<GraphGroup[]> {
  const trimmed = query.trim();
  if (!trimmed) {
    return [];
  }

  const token = await acquireToken(instance, directoryScopes);
  const escaped = trimmed.replace(/'/g, "''");
  const filter = encodeURIComponent(`startswith(displayName,'${escaped}')`);
  const response = await fetch(`${GRAPH_BASE_URL}/groups?$filter=${filter}&$select=id,displayName&$top=10`, {
    headers: { Authorization: `Bearer ${token}`, ConsistencyLevel: "eventual" },
  });
  if (!response.ok) {
    throw new Error(`Group search failed: ${response.status} ${response.statusText}`);
  }
  const data = await response.json();
  return (data.value ?? []) as GraphGroup[];
}
