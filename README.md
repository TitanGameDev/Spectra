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
