const SESSION_STARTED_KEY = "spectra_session_started";

// Absolute session lifetime — the user is signed out and returned to the login
// screen exactly 1 hour after signing in, regardless of activity.
export const MAX_SESSION_MS = 60 * 60 * 1000;

export function markSessionStarted(): void {
  sessionStorage.setItem(SESSION_STARTED_KEY, Date.now().toString());
}

// Milliseconds remaining in the current session. If no start time is recorded
// (e.g. the app loaded with an already-cached MSAL session), starts the clock now.
export function getSessionRemainingMs(): number {
  const startedAt = Number(sessionStorage.getItem(SESSION_STARTED_KEY));
  if (!startedAt) {
    markSessionStarted();
    return MAX_SESSION_MS;
  }
  return MAX_SESSION_MS - (Date.now() - startedAt);
}

// Local-only sign-out: wipes this app's session (MSAL's cache lives in
// sessionStorage too, so this clears it) without contacting Microsoft's logout
// endpoint — signing out of Spectra never signs the user out of other
// Microsoft apps/tabs sharing the browser.
export function endLocalSession(): void {
  sessionStorage.clear();
  window.location.assign("/");
}
