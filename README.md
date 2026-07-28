# Spectra

## Deploy

For a fresh Ubuntu/Debian server, [`deploy/install.sh`](deploy/install.sh) automates the entire "Production hardening" checklist further down this doc end to end — it's the fast path; that checklist is still the reference for what it's actually doing and for any other distro.

```bash
curl -fsSL https://raw.githubusercontent.com/TitanGameDev/Spectra/main/deploy/install.sh | sudo bash
```

If you'd rather not fetch-and-pipe at all, cloning the repo and running `sudo bash deploy/install.sh` from inside it works identically (it detects it's already sitting in a checkout) — useful if you want to read the script first, which is generally good practice before piping anything into `sudo bash`.

**What it does**: installs the .NET 8 SDK, Node.js, MySQL Server, nginx, and (optionally) certbot; builds and publishes both the backend and frontend; creates a dedicated `spectra` system user and a `spectra.service` systemd unit (`Restart=on-failure`, hardened with `ProtectSystem=strict`/`NoNewPrivileges`); provisions an empty MySQL database + user for Spectra and writes the generated credentials to `/etc/spectra/mysql-credentials.txt` (root-only); and configures nginx from the same template as [`deploy/nginx/spectra.conf`](deploy/nginx/spectra.conf).

**What it deliberately leaves to you** (can't be automated generically — see [Azure AD (Entra ID) setup](#azure-ad-entra-id-setup) below): creating the Azure AD app registrations, granting per-customer admin consent, and the Exchange Online PowerShell certificate for the Email Security tab. It'll prompt for the Azure AD tenant/client IDs if you already have them (blank is fine — fill them into `/etc/spectra/backend.env` and re-run later), but it can't create the app registrations themselves. It also only *provisions* the MySQL database — the actual cutover from the default local SQLite database still happens through **Settings → Database** in the browser once you're signed in (see [Database](#database-sql-server--mysql) below), using the credentials the script printed.

**Non-interactive use**: every prompt (domain, TLS, firewall, Azure AD IDs) is also an environment variable — set `SPECTRA_DOMAIN=app.example.com SPECTRA_SETUP_TLS=yes ...` before piping it through `bash` to skip prompts entirely; see the script's own header comment for the full list. Safe to re-run — it skips packages/users/certs that already exist and just re-pulls, rebuilds, and restarts the app, same as clicking **Update now** in Settings → Updates (see [One-click update](#one-click-update-settings--updates) below) once it's installed.

**Domain behind Cloudflare's proxy?** Say so when prompted (or set `SPECTRA_USE_CLOUDFLARE_DNS=yes`) — the standard Let's Encrypt HTTP challenge can't work through it (the validation request hits Cloudflare's edge, not your server, and there's nothing there to answer it with; you'd see this as a Cloudflare `521` error even though the origin is fine), so the script uses a DNS challenge via the Cloudflare API instead. You'll need a Cloudflare API token scoped to **Zone → DNS → Edit** for just that domain (create one at [dash.cloudflare.com/profile/api-tokens](https://dash.cloudflare.com/profile/api-tokens) — not the legacy global API key). This also has a real advantage over the usual "temporarily grey-cloud the domain" workaround: certbot's renewal timer keeps working completely unattended afterwards, with the Cloudflare proxy left on the whole time. Once the certificate's issued, go set Cloudflare's **SSL/TLS → Overview** encryption mode to **Full** or **Full (strict)** — the script can't do that part, it's a Cloudflare dashboard setting.

Everything below this point covers local development and the full feature/permission reference.

## Layout

- `frontend/` — React + Vite + TypeScript SPA. Manual "Sign in with Microsoft" button using MSAL popup login.
- `backend/` — ASP.NET Core Web API (net8.0, runs on Linux). Validates the Microsoft-issued access token on every request via `Microsoft.Identity.Web`.

## Prerequisites

- Node.js 20+ and npm
- .NET SDK 8.0
- PowerShell 7+ (`pwsh`) with the `ExchangeOnlineManagement` module (`Install-Module ExchangeOnlineManagement -Scope AllUsers`) — only needed for the Exchange Online security checks (Security tab → **Email Security**); see [Exchange Online security checks](#exchange-online-security-checks-email-security-tab) below. Everything else runs fine without it.

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

- The backend is plain ASP.NET Core on Linux, so it can shell out to PowerShell (`pwsh`) scripts via `System.Diagnostics.Process` — this is exactly how the Exchange Online security checks are collected, see [Exchange Online security checks](#exchange-online-security-checks-email-security-tab) below.
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

**If the configured database becomes unreachable** (dropped tables/database, revoked credentials, server down) after a cutover, the backend no longer crashes on startup or 500s every request — it starts in a degraded state and every signed-in user sees a "Can't reach the database" screen instead of the normal app, with a **Reset to local database** button that switches Spectra back to local SQLite (creating it fresh if needed) so you can get back in without SSH/CLI access to the box. This bypass only exists while the database is actually unhealthy — once back on a working database, switching databases is admin-only again as usual. Under the hood: `DatabaseHealth` (`backend/Services/DatabaseHealth.cs`) is a singleton flipped by the startup schema check and by `SpectraClaimsTransformation` (which also degrades gracefully — admin status can't be determined without the database, so it falls back to "not admin" rather than throwing), and read by `GET /api/system/status`; `POST /api/system/reset-to-sqlite` does the actual switch.

## MSP customer tenants (Users tab, per customer)

Settings → **Customers** (admin-only) is how Spectra becomes multi-customer: each customer you add gets its own Entra ID tenant queried for users, stored locally, and shown in the **Users** tab for whichever customer is picked in the switcher next to Settings in the header. Every signed-in Spectra user sees the same data — nobody needs their own permissions against a customer's tenant.

**Why this needs its own app registration setup**: the delegated sign-in flow (the "Sign in with Microsoft" button) only ever has permissions in *your own* tenant. Querying a customer's tenant instead uses Microsoft Graph's **application permissions** (client-credentials / "app-only") flow — the backend authenticates as itself, not as whoever's logged in. That requires three one-time changes to the **backend** App Registration, plus one step per customer:

1. **Convert the backend App Registration to multi-tenant** — Azure Portal → **App registrations** → your backend app → **Authentication** → under **Supported account types**, choose **Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant)** → **Save**. Without this, Entra rejects token requests against any tenant other than your own.
2. **Create a client secret** — same app → **Certificates & secrets** → **New client secret** → give it a description and expiry → **Add**. Copy the **Value** immediately (not the Secret ID) — it's only shown once. Put it in `backend/.env` as `AzureAd__ClientSecret` (see `backend/.env.example`).
3. **Add Application permissions** — same app → **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions** (not Delegated) → add:
   - `User.Read.All` — the directory, mailbox metadata join key, and license details all use this.
   - `Reports.Read.All` — the **Mailboxes** sub-tab (size, item count, archive status) on Users.
   - `UserAuthenticationMethod.Read.All` — Security tab's **MFA** sub-tab (per-user registered method types — Authenticator, phone, FIDO2, etc.).
   - `Policy.Read.All` — Security tab's **Conditional Access** sub-tab (policy list + enabled/disabled state).
   - `SecurityEvents.Read.All` — Security tab's **Overview** sub-tab (Secure Score) and **Secure Score** sub-tab (the full per-control breakdown).
   - `MailboxSettings.Read` — Security tab's **Forwarding Rules** sub-tab (auto-forward/redirect inbox rules — the classic BEC/phishing persistence signal).
   - `Application.Read.All` — Azure tab's **Entra Apps** sub-tab (App Registrations + Enterprise Applications). Unlike everything else in the Azure tab, this one *is* a Graph permission needing the usual admin consent — see [Azure Resource Manager access](#azure-resource-manager-access-azure-tab) below for why the rest of that tab works completely differently.

   Each is independent — skip whichever ones you don't need. The corresponding sub-tab just says what's missing instead of showing data, and everything else keeps working.

   You do *not* need to grant admin consent here — these only take effect inside a customer's tenant once that tenant's own admin consents (next step), not in your own tenant. **If you add any of these after customers have already granted consent**, each of them needs to **Grant consent** again (Settings → Customers) for the new permissions to take effect — Entra doesn't retroactively apply new permissions to an existing grant.
4. **Register a redirect URI on the backend app** — same app → **Authentication** → **Add a platform** → **Web** → Redirect URI: the exact value of `Cors:AllowedOrigin` from `backend/.env` (e.g. `http://localhost:5173` for local dev, your real domain in production) → **Save**. Leave the ID tokens/access tokens checkboxes unchecked — this isn't used for any sign-in flow. Without this, clicking **Grant consent** below fails with `AADSTS500113: No reply address is registered for the application`, because the admin-consent redirect has to match a URI already registered on the app being consented to.
5. **Per customer: the customer's Entra admin grants consent** — after adding a customer (name + their Entra tenant ID) in Settings, click **Grant consent** next to that customer. This opens Microsoft's admin consent URL (`https://login.microsoftonline.com/<their-tenant-id>/adminconsent?client_id=...`) in a new tab — send that link to the customer's own Entra/Global admin, or open it yourself if you have admin rights in their tenant. They'll see a standard Microsoft consent screen for "Spectra" requesting `User.Read.All` and must approve it. Until this happens, data collection for that customer fails with an Entra error (shown as the customer's "last sync error" in Settings) — this is expected, not a bug, for a newly-added customer.

**What happens on add**: creating a customer immediately attempts a one-time Graph pull of their users via the client-credentials flow above. If consent hasn't been granted yet, this attempt fails harmlessly (the error is recorded, nothing else breaks) — click **Collect data** in Settings to retry once consent is in place, or just wait for the next automatic sync below.

**Automatic background sync**: every customer is re-collected on a timer (`CustomerSyncBackgroundService`), not just when someone clicks **Collect data** — the exact same collection logic either way, so all the per-source degrade-independently behavior described throughout this doc applies equally to automatic runs. Runs sequentially (one customer at a time), deliberately not in parallel — EXO/SCC PowerShell app-only sessions are capped per app/tenant, and there's no one waiting on a background job, so there's no reason to spin up several `pwsh` processes at once for unrelated customers. Configurable via `Sync__IntervalHours` in `.env` (defaults to 6 hours; set to `0` to disable and rely on manual **Collect data** only). A manual click and a scheduled run share the same per-customer lock, so they can never race each other into a corrupted partial state.

**Mechanics worth knowing**:
- Collected users are stored in Spectra's own database (`CustomerUsers` table), not queried live — this is what lets every Spectra user see a customer's directory without their own Graph permissions against that tenant.
- The customer picker's own data source (`/api/customers/summary`) is available to any signed-in user; the fuller admin management view (`/api/customers`, add/collect/consent-url) is admin-only.
- `AzureAd__ClientSecret` is only needed for this feature — the core sign-in flow never uses it. Leaving it unset is fine until you add your first customer; the error message you'll get is explicit about what's missing.

**Users tab sub-tabs**: Directory (job title, department, office, enabled/disabled, created date, and email aliases — every secondary address from Graph's `proxyAddresses` property, excluding the primary; uses `User.Read.All`, already granted, no new consent needed), Mailboxes (size, item count, archive enabled — needs `Reports.Read.All`, see above), and Licenses (assigned SKUs with friendly names via a small built-in lookup table, unlicensed users flagged distinctly). License details use `User.Read.All` — no extra permission there. If mailbox data is missing for every user on a customer whose consent otherwise succeeded, that's usually the `Reports.Read.All` permission not being granted yet, and the Mailboxes sub-tab says so directly — but **there's a second, easy-to-miss cause of the exact same symptom**: Microsoft conceals real user identities in usage-report data by default, tenant-wide, regardless of what permissions are granted. If `Reports.Read.All` is granted and the Graph call is succeeding (visible in the backend logs as a 200 from `reports/getMailboxUsageDetail`) but every mailbox still shows no data, that's almost certainly this — Spectra detects it automatically (a real row came back but matched no real user by UPN) and the Mailboxes sub-tab switches to the actual fix: in the Microsoft 365 admin center, go to **Settings → Org Settings → Services → Reports**, check **"Display Concealed user, group, and site names in all reports"**, and save (takes a few minutes to take effect, then re-collect). License/mailbox fetches are per-user Graph calls done with light parallelism during collection, so re-collecting a large tenant takes noticeably longer than just listing users did before this.

**Security tab sub-tabs**: Overview (Secure Score as a percentage, Conditional Access enabled/total count, MFA coverage, count of users with forwarding rules — all computed from the same collection pass, no extra API calls per sub-tab), Secure Score (every control Microsoft's Secure Score evaluates, biggest improvement opportunity first — click a row for remediation guidance, implementation cost, user impact, and threats mitigated; this is the Graph-native equivalent of what a tool like [ORCA](https://github.com/cammurray/orca) shows for Secure Score specifically), MFA (per-user registered method types, pulled live from `/users/{id}/authenticationMethods` — needs `UserAuthenticationMethod.Read.All`, see above; password isn't counted as a method, only actual second factors), Conditional Access (tenant-wide policy list, click a row to expand who's targeted/what's covered/controls — needs `Policy.Read.All`; specific users/groups/roles show as counts rather than resolved names, since that needs `Group.Read.All` on top of everything else), Forwarding Rules (every mailbox with an active auto-forward or redirect configured, from **either** of the two distinct mechanisms that produce it: an inbox rule with a forward/redirect action — Graph-sourced, needs `MailboxSettings.Read` — **and** mailbox-level auto-forwarding set directly on the mailbox via the Exchange admin center's Mailboxes → a user → Mail flow → Edit forwarding panel, or a user's own OWA "Forwarding" self-service setting — EXO PowerShell-sourced, part of the Email Security collection below. Neither setup path creates the other's artifact, so both are collected and shown together in one table with a Type column distinguishing them, since they need different remediation — disable/delete the rule vs. clear the mailbox's forwarding address), Exchange Rules (every inbox rule for every user, not just forwarding ones — same Graph call and permission as the inbox-rule half of Forwarding Rules, just unfiltered; condition/action types are Graph's raw property names given a readable label, e.g. "subjectContains" → "Subject contains"), Mail Flow Rules (the tenant's actual mail flow / "transport" rules from the Exchange admin center — name, state, priority, Exchange's own auto-generated description of what the rule does, and whether it silently deletes messages — browsable as a plain table rather than folded into a single pass/fail check; same collection as Email Security below, needs no separate setup), Mailbox Access (every explicit delegate grant on every mailbox, in two tables: Full Access — who besides the owner can open a mailbox, via `Get-EXOMailboxPermission`, with a Denied column for explicit deny entries — and Send As — who can send mail that appears to come from the mailbox, via `Get-EXORecipientPermission`. Both cmdlets are called once, tenant-wide, rather than per-mailbox; the default self-grant every mailbox has (`NT AUTHORITY\SELF`) and inherited Full Access entries are filtered out server-side, so every row shown is a genuine, explicitly-granted permission. Same collection and setup gate as Email Security below, no separate step), Email Security (ORCA-style anti-phishing/Safe Links/Safe Attachments/anti-spam/anti-malware/DKIM/mail-flow checks — needs the separate Exchange Online PowerShell setup below, not just Graph permissions), and Identity (ORCA's "Role Based Access Control" category, but Graph-sourced rather than EXO PowerShell-sourced: Global Administrator count is 2–4 per Microsoft's own recommendation, every Global Administrator has MFA registered — cross-referenced against the same per-user MFA data the MFA sub-tab already collects, no extra call — and Security Defaults or an enabled Conditional Access policy actually enforces MFA tenant-wide. Unlike Email Security, this needs **no separate setup at all**: `RoleManagement.ReadWrite.Directory` (already added for the EXO Global Reader self-assignment) covers reading directory role membership too, and `Policy.Read.All` is already used for Conditional Access — so this works as soon as normal Graph consent succeeds, same as the rest of the tab), and Domains (SPF/DMARC checks against every verified domain on the tenant — SPF record exists and hard-fails with `-all` rather than a soft `~all`/`+all`/`?all`, DMARC record exists and is actually enforced with `p=quarantine`/`p=reject` rather than just monitoring with `p=none`. Sourced from **live public DNS TXT lookups**, not Graph or Exchange Online PowerShell — the verified domain list itself is fetched via Graph (reusing the same `/organization` call already made for Exchange Online's tenant-domain resolution, so **no new permission or consent** is needed), then each domain's SPF and `_dmarc.<domain>` TXT records are queried directly. Needs outbound DNS (port 53) access from wherever the backend runs — essentially always available, unlike the Exchange Online PowerShell prerequisites below), and Compliance (DLP/retention/alert policy checks via **Security & Compliance PowerShell**, a sibling session to Email Security's Exchange Online PowerShell — see [Security & Compliance checks](#security--compliance-checks-compliance-tab) below; shares Email Security's setup gate, so it starts working automatically once that's set up, no extra step). Like the Users sub-tabs, each data source degrades independently — a missing permission shows a clear inline message on just that sub-tab rather than breaking collection for everything else.

The Secure Score control catalog (`secureScoreControlProfiles`) is joined against the tenant's achieved score per control (`secureScores.controlScores`) server-side — both come from `SecurityEvents.Read.All` but are two separate Graph calls/storage fields, so either can independently be missing if something goes wrong with just one of them.

There's also a tenant-wide Reports API endpoint (`/reports/authenticationMethods/userRegistrationDetails`) that looks like the obvious choice for this and needs yet another permission (`AuditLog.Read.All`) — it was tried first here and dropped in favor of the per-user endpoint above, which proved more reliable in practice.

## Azure Resource Manager access (Azure tab)

**This works completely differently from every other tab.** Users/Security/EXO/SCC all flow through Entra admin consent — the customer's admin approves an API permission once, and Spectra's app can then call that API for their tenant. Azure resource data (VMs, App Services) doesn't work that way: **Azure Resource Manager authorizes by Azure RBAC role assignment on a subscription, not by Entra API permission consent** — there's no admin-consent screen for it at all, and no new App Registration permission to add. The one thing that does carry over: Spectra's app already exists as a service principal ("Enterprise Application") in the customer's tenant once they've granted the ordinary Graph consent above, so there's no new app-registration step — the customer's Azure admin just needs to grant it a role.

**Per customer, for VMs and App Services**: the customer's Azure admin goes to the target **Subscription** (or a Management Group, to cover several at once) → **Access control (IAM)** → **Add** → **Add role assignment** → select the **Reader** role → **Members** → search for "Spectra" (or your app's display name) → **Select** → **Review + assign**. That's it — no PowerShell, no consent screen, just a normal Azure RBAC grant. Until this is done, the Azure tab's Overview/Virtual Machines/App Services sub-tabs show an empty-subscriptions message rather than an error, since an empty result from `/subscriptions` is indistinguishable from "no subscriptions exist" and "Reader not assigned yet" at the API level.

**Reservations need a second, separate, higher-privilege role**: Reserved Instances/savings plans are a **tenant-level** resource, not scoped to any one subscription, so they use their own RBAC surface — **Reservations Reader**, assigned via a completely different screen: Azure Portal → **Home** → **Reservations** → **Role assignment** (top nav) → pick **Reservations Reader** → add Spectra's service principal. This needs the person doing it to already have delegate rights over reservations (typically whoever purchased them, or someone with elevated/User Access Administrator rights) — a bigger, rarer ask than the subscription Reader grant above, confirmed against [Microsoft's own reservation-permissions docs](https://learn.microsoft.com/en-us/azure/cost-management-billing/reservations/view-reservations). It's optional and degrades independently: everything else in the Azure tab works fine without it, and the Reservations sub-tab just explains what's missing until it's granted.

**Azure tab sub-tabs**: Overview (counts across subscriptions/VMs/App Services/reservations), Virtual Machines (name, subscription, resource group, size, OS, power state, location — one call per subscription with `statusOnly=true` so power state comes back without a separate per-VM round trip), App Services (Web Apps and Function Apps, both `Microsoft.Web/sites` under the hood — name, subscription, resource group, kind, state, hostname), Reservations (SKU, quantity, term, provisioning state, expiry, applied scope — gated on the Reservations Reader role above), and Entra Apps (two tables: **App Registrations** — apps owned by this tenant, with the soonest-expiring password/certificate credential flagged the same way the Exchange Online certificate banner elsewhere flags its own expiry — and **Enterprise Applications** — every service principal in the tenant including third-party/gallery apps, not just locally-registered ones; both need `Application.Read.All`, see above).

**Not collected yet** (reasonable follow-ups, not oversights): the VM SKU catalog isn't cross-referenced, so **Size** shows the raw Azure size name (e.g. `Standard_D4s_v5`) rather than decoded vCPU/RAM; resource types beyond VMs and App Services (storage accounts, networking, SQL, containers, etc.); cost/spend data; reservation utilization percentage; and Azure Policy compliance.

## Exchange Online security checks (Email Security tab)

Exchange's own tenant-wide mail flow ("transport") rules, anti-phishing, Safe Links, Safe Attachments, anti-spam/anti-malware policies, and DKIM signing aren't exposed via Microsoft Graph **at all** — Security tab → **Email Security** reaches them instead via app-only **Exchange Online PowerShell**, a separate authentication mechanism (certificate-based, not the client secret used everywhere else above) and a separate access grant (an Entra directory role, not a Graph API permission). This is Spectra's take on what [ORCA](https://github.com/cammurray/orca) checks beyond Secure Score — 35 checks across anti-phishing, Safe Links, Safe Attachments, anti-spam, anti-malware, DKIM, mail-flow rules, org config, and sharing. Every policy-backed check (all categories except DKIM, mail-flow, org config, and sharing) evaluates **every policy in the tenant, not just the default** — a custom-scoped policy for one department with Safe Links disabled shows up as a failure just as clearly as a bad tenant-wide default, with the offending policy named in the result. Remediation text is written fresh, not copied from ORCA's own output.

**One-time setup, in addition to everything above:**

1. **Generate a certificate** (self-signed is fine — this only needs to prove Spectra's backend holds the matching private key, not establish public trust):
   ```bash
   openssl req -x509 -newkey rsa:2048 -keyout exo-key.pem -out exo-cert.pem -days 730 -nodes -subj "/CN=Spectra EXO App-Only"
   openssl pkcs12 -export -out exo-cert.pfx -inkey exo-key.pem -in exo-cert.pem -passout pass:<choose-a-password>
   ```
2. **Upload the public half** (`exo-cert.pem`) to the backend App Registration → **Certificates & secrets** → **Certificates** tab → **Upload certificate**.
3. **Deploy the private half** (`exo-cert.pfx`) to the server, e.g. `backend/certs/exo-cert.pfx` (that folder is gitignored, same as `keys/`) — readable only by the account the backend runs as.
4. **Add two more Application permissions** on the same backend App Registration → **API permissions** → **Add a permission**:
   - **Microsoft Graph** → Application → `RoleManagement.ReadWrite.Directory` — lets the backend automatically assign itself the **Global Reader** directory role in a customer's tenant after consent (see step 6), so nobody has to run PowerShell by hand per customer.
   - **APIs my organization uses** → search **Office 365 Exchange Online** → Application → `Exchange.ManageAsApp` — what actually authorizes the certificate above to run Exchange Online PowerShell cmdlets once Global Reader is assigned.

   Same rule as the Graph permissions above: **customers who already granted consent need to grant it again** for these two to take effect — Entra doesn't retroactively apply new permissions to an existing grant.
5. **`.env` additions** (`backend/.env.example` has the placeholders):
   ```
   Exo__CertificatePath=/path/to/exo-cert.pfx
   Exo__CertificatePassword=<the password you chose in step 1>
   ```
6. **Nothing else, per customer.** Unlike the Global Reader/Exchange RBAC assignment some tools require an admin to run by hand, Spectra assigns itself the Global Reader role automatically the next time it collects data for a customer that has both granted Graph consent and doesn't have it yet — no extra click, no PowerShell for the customer's admin to run. Expect the very first Exchange Online PowerShell collection after a fresh role assignment to sometimes fail and self-heal on the next run — Entra role propagation can take up to ~15 minutes, and that's expected, not a bug (same as the "hasn't granted consent yet" state elsewhere in Settings).

**What you'll see in the Email Security sub-tab before this is set up**: a clear "needs a one-time setup step" message, distinguishing "waiting for Exchange Online access to be granted automatically" (role assignment hasn't happened/propagated yet) from "access granted — waiting for the first successful collection." Once checks exist, they keep showing even if a later refresh fails — Exchange Online PowerShell (an external process, cert auth, occasional role-propagation lag) is meaningfully flakier than a plain Graph HTTP call, so a transient failure shows as "most recent refresh failed, showing older data" rather than wiping results that were working a minute ago.

**Certificate expiry**: the Email Security sub-tab checks the certificate's actual expiry date on every load (`GET /api/system/exo-certificate-status`, a plain local file read — no PowerShell involved) and shows a warning banner once it's within 60 days of expiring, or immediately if the configured cert can't be loaded at all. Regenerate it the same way as step 1 above, re-upload the public half, and replace the PFX before it expires — nothing auto-renews.

**Known gaps, deliberate scope**:
- 35 checks, not ORCA's full ~100 — the architecture (one evaluator method per check) is built so adding more later is cheap.
- DKIM, mail-flow rules, org config, and sharing checks are still tenant-wide/single-object by nature and aren't evaluated per-policy the way the rest of the catalog is — there's no per-user-group "custom scope" concept for these.

## Security & Compliance checks (Compliance tab)

**No extra setup.** DLP policies, retention policies, and alert policies live in Microsoft Purview, reached via a second PowerShell surface — **Security & Compliance PowerShell** (`Connect-IPPSSession`) — that's a genuinely separate session from Exchange Online PowerShell above, but uses the *exact same* certificate, the *exact same* `Exchange.ManageAsApp` permission, and the *exact same* Global Reader role assignment. If Email Security is already working for a customer, Compliance checks start working on the next collection with nothing further to configure — confirmed against Microsoft's own [app-only auth documentation](https://learn.microsoft.com/en-us/powershell/exchange/app-only-auth-powershell-v2), which lists Global Reader as a supported role for both PowerShell surfaces.

6 checks: at least one DLP policy is enabled, no enabled DLP policy is stuck in Test mode, at least one retention policy is enabled, no enabled retention policy is stuck in Test mode, no high-severity alert policy is disabled, and every enabled high-severity alert policy actually notifies someone. The alert checks deliberately key off `Severity`/`Disabled`/`NotifyUser` rather than matching specific built-in policy names by string — that way they apply to whatever a tenant actually has configured, custom or default, without risking a check that silently never fires because a name didn't match exactly.

Implemented as a sibling to the Email Security pipeline, not merged into it — `Collect-SccSecurityData.ps1` / `SccPowerShellClient.cs` / `SccCheckEvaluator.cs`, mirroring `Collect-ExoSecurityData.ps1` / `ExoPowerShellClient.cs` / `OrcaCheckEvaluator.cs`. Kept separate because `Connect-ExchangeOnline` and `Connect-IPPSSession` are different sessions against different endpoints, and loading both modules' cmdlets into one PowerShell session risks name collisions (`Get-OrganizationConfig` exists in both). Gated on the same `ExoRoleAssigned` flag as Email Security — same "needs a one-time setup step" message while waiting on role propagation, same "most recent refresh failed, showing older data" behavior on a transient failure.

## PDF reports

**Download Report** (top of the Security tab and the Users tab, next to any customer) renders a branded PDF snapshot of that tab's data for the selected customer, styled to match the app's own dark theme — same background/card/text colors as `frontend/src/index.css`'s dark-mode palette, and the header's logo mark uses the same color stops as the login screen's `.brand-mark` gradient, applied as a linear gradient since PDF rendering doesn't lend itself to replicating a conic one. Shared styling/layout (`ReportTheme.cs`, `ReportComponents.cs` — colors, the header/footer, summary tiles) is factored out so every report looks consistent automatically rather than each one copying the same code.

- **Security report** (`GET /api/customers/{id}/report`, `SecurityReportPdfGenerator.cs`): Secure Score, MFA coverage, Conditional Access, and every check from Email Security/Identity/Domains/Compliance, plus a "Recommended Actions" section listing just the failing checks with their remediation text.
- **User report** (`GET /api/customers/{id}/users-report`, `UserReportPdfGenerator.cs`): Directory, Licenses, and Mailboxes (the Mailboxes section is omitted if no mailbox data was ever collected or it came back concealed — see the Mailboxes tab note above — rather than shown full of dashes).

Both exclude disabled user accounts entirely (Security report's MFA coverage, User report's Directory/Licenses/Mailboxes) — a disabled account's stale MFA/license state would otherwise skew the picture of the tenant's actual security posture; the User report's summary tiles show how many were excluded. Both re-evaluate/reuse the exact same stored data their on-screen tab already serves — no separate collection or storage path, so a report always matches what the dashboard shows. Rendered via [QuestPDF](https://www.questpdf.com/), a fluent, code-first .NET PDF library — free under its **Community license** for individuals/businesses under **$1M USD annual gross revenue** (see [questpdf.com/license](https://www.questpdf.com/license/community.html) for the full terms; a Professional/Enterprise license is available if that threshold doesn't fit). The frontend can't reuse the normal `apiFetch` helper for either (it always parses JSON) — a shared `downloadFile` helper in `api.ts` fetches the PDF as a blob and triggers a standard browser download instead.

## One-click update (Settings → Updates)

Once a server is set up via `deploy/install.sh`, updating it day-to-day doesn't need SSH — **Settings → Updates** shows the currently running commit, whether a newer one exists upstream, and an **Update now** button that pulls, rebuilds, and restarts in place.

**Why this needed its own design, not just "run a shell command from the API"**: `install.sh` deliberately hardens `spectra.service` to run as an unprivileged `spectra` system user with `ProtectSystem=strict`/`NoNewPrivileges=true` — no write access outside its own app/data directories, no ability to restart services. An update needs `git pull`, `dotnet publish`, `npm run build`, and `systemctl restart`, none of which that user can do. Rather than grant the web app process sudo (even scoped to one script), **the backend never gains any new privilege at all**:

1. Clicking **Update now** (`POST /api/settings/update`, admin-only) just writes `$SPECTRA_DATA_DIR/update-requested` — a file the `spectra` user already has write access to (it's covered by `spectra.service`'s existing `ReadWritePaths`).
2. `spectra-updater.path`, a systemd path unit installed by `install.sh` and running as **root**, watches for that file and triggers `spectra-updater.service` the moment it appears.
3. That service runs [`deploy/update.sh`](deploy/update.sh) — a leaner sibling of `install.sh` that deletes the request flag first (so the watcher doesn't re-trigger), then does the actual `git pull` → `dotnet publish` → `npm run build` → `systemctl restart spectra`, and finally writes `$SPECTRA_DATA_DIR/update-status.json` (`state: succeeded`/`failed` + a message) and `version.json` (`{commit, ref, deployedAt}`) for the backend to read back.

A failed build never takes down the running instance — `spectra.service` is only restarted *after* a successful build, so a broken commit just leaves `update-status.json` reporting `failed` with the old version still running. `GET /api/settings/update-status` (also admin-only) is what **Settings → Updates** polls — it reads those two JSON files and does a best-effort `git ls-remote` to report whether an update's available; on any deployment not set up via `install.sh` (e.g. local dev), it just reports itself as unavailable rather than erroring, same degrade-gracefully convention as everything else in this app. Both scripts are covered by `install.sh`'s `SPECTRA_*` config, persisted to `/etc/spectra/install.conf` so `update.sh` can run standalone without re-prompting for anything.

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
4. **Real secrets**: move `AzureAd__*` and `Exo__CertificatePassword` values out of `.env` and into a proper secret store (Key Vault, systemd `EnvironmentFile` with restricted permissions, etc.) for anything beyond local dev.
5. **Run the backend as a systemd service** as a non-root user, with `Restart=on-failure`, reading its environment from a root-owned, mode-600 env file — not the `.env` used for local dev.
6. **TLS certs** via certbot/Let's Encrypt (or your CA of choice); set up auto-renewal.
7. **Firewall**: only 80/443 open to the internet; the backend's port should not be reachable externally at all (enforced by binding to loopback in step 2, but a firewall rule is good defense in depth).
8. **If using the Email Security tab**: install PowerShell 7+ (`pwsh`) and `Install-Module ExchangeOnlineManagement -Scope AllUsers` on the server itself (not just your dev machine), and deploy the `exo-cert.pfx` from [Exchange Online security checks](#exchange-online-security-checks-email-security-tab) with the same mode-600, service-user-only permissions as the env file in step 5.
