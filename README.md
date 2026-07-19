# Spectra

## Layout

- `frontend/` — React + Vite + TypeScript SPA. Manual "Sign in with Microsoft" button using MSAL popup login.
- `backend/` — ASP.NET Core Web API (net8.0, runs on Linux). Validates the Microsoft-issued access token on every request via `Microsoft.Identity.Web`.

## Prerequisites

- Node.js 20+ and npm
- .NET SDK 8.0

## Azure AD (Entra ID) setup

You need **two** App Registrations in your tenant:

1. **Frontend (SPA)**
   - Platform: Single-page application
   - Redirect URI: `http://localhost:5173`
   - Note the **Application (client) ID** and **Directory (tenant) ID** → these go in `frontend/.env`.
2. **Backend (API)**
   - Expose an API → add a scope, e.g. `access_as_user` → this gives you `api://<backend-client-id>/access_as_user`.
   - Note the **Application (client) ID** → this goes in `backend/.env` as `AzureAd__ClientId`.
   - On the **frontend** App Registration, add API permissions → the backend API's `access_as_user` scope, and grant admin consent if required by your tenant.

## Admin access

Spectra has two ways someone becomes an admin (able to see **Settings** and configure the rest):

1. **Bootstrap admin** — the very first person to ever sign in to the app becomes a permanent admin automatically. This is the way into Settings on a fresh install; there's nothing to configure for it.
2. **Admin group** — once someone with admin access opens **Settings**, they can search for and assign an Entra ID security group (e.g. "Spectra Admin"); every member of that group then gets admin access too. This is checked by reading the `groups` claim directly off the already-validated access token — no extra API call — which requires one extra one-time setup step:

   In the Azure Portal, on the **backend (API) App Registration** (not the frontend one) → **Token configuration** → **Add groups claim** → check **Security groups** → save. This makes Entra include the signed-in user's group memberships in tokens issued for the API.

   > Caveat: if a user belongs to 200+ groups, Azure AD omits the groups list from the token entirely (an "overage" indicator instead). Not handled here — unlikely to matter for an MSP-internal tool, but worth knowing.

The searchable group picker in Settings calls Microsoft Graph directly from the browser and needs the `Group.Read.All` delegated permission on the **frontend** App Registration. This is requested on demand (only when an admin opens Settings and searches — not at every sign-in) and needs admin consent: **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated** → `Group.Read.All` → **Grant admin consent for \<your tenant\>**.

## Configure

```bash
cp frontend/.env.example frontend/.env
cp backend/.env.example backend/.env
```

Fill in the Tenant ID / Client ID / scope values from the App Registrations above. `.env` files are gitignored — for anything beyond local dev, move these values into your secure vault (Key Vault, etc.) instead of the `.env` file.

## Run (once tooling is installed)

```bash
# Terminal 1 — backend, http://localhost:5080
cd backend
dotnet restore
dotnet run

# Terminal 2 — frontend, http://localhost:5173
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`, click **Sign in with Microsoft** — this opens a popup (no automatic/silent SSO), you authenticate against your tenant, and the SPA gets an access token it attaches as a `Bearer` header to calls against the backend's `/api/me`, which validates the token per-request.

## Notes

- The backend is plain ASP.NET Core on Linux, so it can shell out to your PowerShell (`pwsh`) scripts via `System.Diagnostics.Process` when that work starts — not wired up yet, this first pass is just the login screen.
- The backend uses a SQLite database (`backend/spectra.db`, gitignored) for users and the admin group setting. Migrations apply automatically on startup — nothing to run by hand. If you change the data model later, generate a new migration with `dotnet ef migrations add <Name>` (needs the `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`).

## Production hardening

Built in, applies regardless of hosting:

- **Token storage**: MSAL uses `sessionStorage`, not `localStorage` — tokens don't persist past the tab/browser session or leak across tabs.
- **Least-privilege `/api/me` response**: returns curated `name`/`email` fields, not the full raw JWT claims set (tenant ID, object ID, app IDs, etc.).
- **CORS fails closed**: `Cors:AllowedOrigin` has no checked-in default. Outside `Development` the app throws on startup if it isn't explicitly set — it will never silently fall back to a dev origin. Allowed methods/headers are also scoped down (`GET, POST` / `Authorization, Content-Type`) instead of wildcarded.
- **Rate limiting**: 100 requests/minute per IP, backstop against abuse even if the edge proxy's own limiting is misconfigured or bypassed.
- **No leaked internals**: outside `Development`, unhandled exceptions return a generic JSON error instead of a stack trace.
- **`X-Forwarded-*` handling**: trusts `X-Forwarded-For`/`-Proto` from loopback only, matching nginx and Kestrel running on the same host — so the app sees the real client IP/scheme without trusting arbitrary upstream headers.
- **HSTS + HTTPS redirection** enabled outside `Development`.

Deployment checklist (nginx + systemd on Linux):

1. **Reverse proxy**: [`deploy/nginx/spectra.conf`](deploy/nginx/spectra.conf) — TLS termination, the full security header set (CSP, HSTS, X-Frame-Options, etc.), SPA static file serving, and `/api/` proxying with edge-level rate limiting. Replace `app.example.com` and the cert paths, and add the `limit_req_zone` line (noted in the file) to your main `nginx.conf`.
2. **Bind Kestrel to loopback only** — set `ASPNETCORE_URLS=http://127.0.0.1:5080` in production so the backend is never reachable except through nginx (see `backend/.env.example`).
3. **Same-origin API in prod** — since nginx proxies `/api/` under the same domain as the SPA, set `VITE_API_BASE_URL=""` in the production frontend build so calls are same-origin and need no CORS at all.
4. **Real secrets**: move `AzureAd__*` values out of `.env` and into a proper secret store (Key Vault, systemd `EnvironmentFile` with restricted permissions, etc.) for anything beyond local dev.
5. **Run the backend as a systemd service** as a non-root user, with `Restart=on-failure`, reading its environment from a root-owned, mode-600 env file — not the `.env` used for local dev.
6. **TLS certs** via certbot/Let's Encrypt (or your CA of choice); set up auto-renewal.
7. **Firewall**: only 80/443 open to the internet; the backend's port should not be reachable externally at all (enforced by binding to loopback in step 2, but a firewall rule is good defense in depth).
