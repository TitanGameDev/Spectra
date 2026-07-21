# Spectra

## Layout

- `frontend/` — React + Vite + TypeScript SPA. Manual "Sign in with Microsoft" button using MSAL popup login.
- `backend/` — ASP.NET Core Web API (net8.0, runs on Linux). Validates the Microsoft-issued access token on every request via `Microsoft.Identity.Web`.

## Prerequisites (not yet installed on this machine)

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

The searchable group picker in Settings, and the **Users** tab (lists every user in the tenant via Graph), both call Microsoft Graph directly from the browser and need delegated permissions on the **frontend** App Registration: `Group.Read.All` and `User.Read.All`. Both are requested on demand (only when Settings' group search or the Users tab is actually used — not at every sign-in) and need admin consent: **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated** → add both → **Grant admin consent for \<your tenant\>**.

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
- `InvariantGlobalization` is disabled (`Spectra.Api.csproj`) — `Microsoft.Data.SqlClient`, needed for the SQL Server integration, refuses to run at all under invariant mode (it relies on ICU for collation-aware operations). This makes the published app somewhat larger; not worth reverting unless the external-database feature is dropped entirely.
- The backend uses a SQLite database (`backend/spectra.db`, gitignored) by default for users, settings, and customers. Migrations apply automatically on startup — nothing to run by hand. If you change the data model later, generate a new migration with `dotnet ef migrations add <Name>` (needs the `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`). See **Database (SQL Server / MySQL)** below for moving off SQLite.

## Database (SQL Server / MySQL)

Settings → **Database** (admin-only) lets you connect a real SQL Server or MySQL instance and cut Spectra over to it, live, with no restart required.

**How it works**: admins pick a database type, then enter host, port, database name, username/password. Saving tests the connection and checks whether the named database exists. If it doesn't, Settings offers to create it — declining just leaves the saved connection sitting there with a **Create Database** button, so you can come back and finish the cutover whenever you're ready. When you do create it, the backend builds the schema, copies every existing user (including whoever is currently the bootstrap admin — this matters, otherwise the person doing the cutover could lock themselves out), the admin group setting, and all customers into the new database, then flips Spectra over to it for the very next request.

**Mechanics worth knowing**:
- The hot-swap works because the backend's EF Core registration reads a singleton "which database is active" service on every request rather than fixing a connection string at startup. That state is also written to a local file (`backend/database-provider.json`, gitignored) so a later restart — for any reason — comes back up on the same database instead of reverting to SQLite.
- The stored password is encrypted at rest via ASP.NET Core Data Protection (keys persisted to `backend/keys/`, also gitignored) and is never sent back to the frontend after saving.
- Authentication is username/password only (SQL Login for SQL Server, standard MySQL auth) — no Windows/Entra auth or MySQL socket auth.
- The form defaults the port per engine (1433 for SQL Server, 3306 for MySQL) specifically to avoid pointing the wrong client at the wrong port — the two protocols are completely incompatible, and the failure mode isn't obvious (SQL Server's `Microsoft.Data.SqlClient` doesn't error cleanly against a MySQL endpoint or vice versa; it manifests as a fairly opaque low-level connection/protocol error).
- SQL Server's connection defaults to `TrustServerCertificate=true`, convenient for self-signed/dev instances but skipping certificate validation — fine for a trusted internal network, worth tightening if the server is reachable more broadly.
- Schema creation on the external database uses EF Core's `EnsureCreated()`, not the migration history SQLite uses — simpler for a first cutover, but it means future model changes won't auto-migrate an external-database-backed install the way `dotnet ef migrations add` + restart does for SQLite. Revisit this if the schema needs to evolve after go-live.
- If the target database name already exists on the server but wasn't created by a prior Spectra provisioning run (i.e. it has unrelated tables), `EnsureCreated()` will not add Spectra's tables to it — point at an empty/dedicated database name instead.
- MySQL support uses `Pomelo.EntityFrameworkCore.MySql` + `MySqlConnector`; SQL Server uses `Microsoft.Data.SqlClient` directly for connection testing/creation, independent of EF Core, since the target database might not exist yet when that check runs.

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
