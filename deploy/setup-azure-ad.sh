#!/usr/bin/env bash
#
# Bootstraps both Azure AD (Entra ID) app registrations Spectra needs — the
# frontend SPA and the backend API — fully automated via the Azure CLI, so
# nothing needs to be clicked by hand in the Azure Portal. See the README's
# "Azure AD (Entra ID) setup" section for what this replaces and why each
# piece is needed.
#
#   curl -fsSL https://raw.githubusercontent.com/TitanGameDev/Spectra/main/deploy/setup-azure-ad.sh | bash
#
# Doesn't need root, and doesn't need to run on the actual server — it's
# purely about your Microsoft 365/Entra tenant, so run it from anywhere
# (your own laptop is fine). You do need to sign in as a user who can
# create app registrations and grant admin consent (Application
# Administrator, Cloud Application Administrator, or Global Administrator).
#
# Ends by either POSTing the result straight to your running Spectra instance
# (set SPECTRA_INSTANCE_URL — it'll ask for the setup token install.sh
# generated), or printing the values to paste into the setup wizard at your
# instance's URL yourself. Either way, install.sh itself no longer takes any
# Azure AD values at all — see the README's "Azure AD (Entra ID) setup".
set -euo pipefail

SPECTRA_APP_NAME="${SPECTRA_APP_NAME:-Spectra}"
SPECTRA_DOMAIN="${SPECTRA_DOMAIN:-}"
SPECTRA_LOCAL_DEV_URL="${SPECTRA_LOCAL_DEV_URL:-http://localhost:5173}"
SPECTRA_INSTANCE_URL="${SPECTRA_INSTANCE_URL:-}"
SPECTRA_SETUP_TOKEN="${SPECTRA_SETUP_TOKEN:-}"
SPECTRA_POSTED="no"

GRAPH_SP_APP_ID="00000003-0000-0000-c000-000000000000"
EXO_SP_APP_ID="00000002-0000-0ff1-ce00-000000000000"

log()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m==> WARNING:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m==> ERROR:\033[0m %s\n' "$*" >&2; exit 1; }

prompt() {
  local __var="$1" __msg="$2" __default="${3:-}" __answer
  if [ -n "${!__var:-}" ]; then return 0; fi
  if [ -r /dev/tty ]; then
    if [ -n "$__default" ]; then
      read -r -p "$__msg [$__default]: " __answer < /dev/tty
    else
      read -r -p "$__msg: " __answer < /dev/tty
    fi
    printf -v "$__var" '%s' "${__answer:-$__default}"
  else
    warn "No terminal attached to prompt for '$__msg' — using default '$__default'. Set \$$__var to override."
    printf -v "$__var" '%s' "$__default"
  fi
}

prompt_secret() {
  local __var="$1" __msg="$2" __answer
  if [ -n "${!__var:-}" ]; then return 0; fi
  if [ -r /dev/tty ]; then
    read -rs -p "$__msg: " __answer < /dev/tty
    echo
    printf -v "$__var" '%s' "$__answer"
  else
    warn "No terminal attached to prompt for '$__msg' — leaving blank. Set \$$__var to provide it non-interactively."
    printf -v "$__var" ''
  fi
}

require() {
  command -v "$1" >/dev/null 2>&1 || die "'$1' is required but not found. $2"
}

ensure_az_cli() {
  if command -v az >/dev/null 2>&1; then
    return 0
  fi
  log "Azure CLI not found — installing..."
  if command -v apt-get >/dev/null 2>&1; then
    curl -sL https://aka.ms/InstallAzureCLIDeb | bash
  elif command -v brew >/dev/null 2>&1; then
    brew install azure-cli
  else
    die "Couldn't auto-install the Azure CLI on this platform — install it yourself: https://learn.microsoft.com/cli/azure/install-azure-cli"
  fi
}

az_login() {
  if az account show >/dev/null 2>&1; then
    log "Already signed in to Azure as $(az account show --query user.name -o tsv 2>/dev/null)."
    return 0
  fi
  log "Signing in to Azure — this opens a browser, or (over SSH with no browser here) gives you a code to enter at https://microsoft.com/devicelogin..."
  log "You need a role that can create app registrations and grant admin consent (Application Administrator, Cloud Application Administrator, or Global Administrator)."
  az login --allow-no-subscriptions >/dev/null
}

# Application permissions (app-only, "Role" type) live under the resource's
# service principal's appRoles — resolved live from Microsoft's own Graph SP
# rather than hardcoded, so this can never drift from what Microsoft
# actually has registered.
graph_app_role_id() {
  az ad sp show --id "$GRAPH_SP_APP_ID" --query "appRoles[?value=='$1'].id | [0]" -o tsv
}

exo_app_role_id() {
  az ad sp show --id "$EXO_SP_APP_ID" --query "appRoles[?value=='$1'].id | [0]" -o tsv
}

# Delegated permissions (user sign-in, "Scope" type) — a different GUID
# namespace from the application/Role permissions above, even for
# identically-named permissions like User.Read.All.
graph_delegated_scope_id() {
  az ad sp show --id "$GRAPH_SP_APP_ID" --query "oauth2PermissionScopes[?value=='$1'].id | [0]" -o tsv
}

# Looks up an existing app by display name so re-running this script
# doesn't create duplicates — reuses what's there instead.
find_existing_app() {
  az ad app list --display-name "$1" --query "[0].appId" -o tsv 2>/dev/null
}

create_or_reuse_backend_app() {
  local existing
  existing="$(find_existing_app "${SPECTRA_APP_NAME} API")"
  if [ -n "$existing" ]; then
    log "Reusing existing '${SPECTRA_APP_NAME} API' app registration ($existing) — re-applying config to it."
    BACKEND_APP_ID="$existing"
    BACKEND_APP_IS_NEW="no"
  else
    log "Creating backend app registration..."
    BACKEND_APP_ID="$(az ad app create --display-name "${SPECTRA_APP_NAME} API" --sign-in-audience AzureADMultipleOrgs --query appId -o tsv)"
    BACKEND_APP_IS_NEW="yes"
  fi
  BACKEND_OBJECT_ID="$(az ad app show --id "$BACKEND_APP_ID" --query id -o tsv)"
}

# Exposes the access_as_user delegated scope the frontend calls the backend
# API with — az ad app create has no flag for this, so it's a direct Graph
# PATCH. Re-runnable: reuses the existing scope's GUID if already exposed,
# rather than adding a duplicate each time.
expose_backend_api_scope() {
  log "Exposing the access_as_user API scope..."
  local existing_scope_id
  existing_scope_id="$(az ad app show --id "$BACKEND_APP_ID" --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv)"
  local scope_id="${existing_scope_id:-$(uuidgen | tr '[:upper:]' '[:lower:]')}"

  python3 -c '
import json, sys
scope_id = sys.argv[1]
body = {
    "identifierUris": [f"api://{sys.argv[2]}"],
    "api": {
        "oauth2PermissionScopes": [{
            "id": scope_id,
            "adminConsentDescription": "Allow the app to access Spectra API on behalf of the signed-in user.",
            "adminConsentDisplayName": "Access Spectra API",
            "userConsentDescription": "Allow the app to access Spectra API on your behalf.",
            "userConsentDisplayName": "Access Spectra API",
            "value": "access_as_user",
            "type": "User",
            "isEnabled": True,
        }]
    },
}
print(json.dumps(body))
' "$scope_id" "$BACKEND_APP_ID" > /tmp/spectra-backend-api.json

  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/${BACKEND_OBJECT_ID}" \
    --headers "Content-Type=application/json" --body @/tmp/spectra-backend-api.json
  rm -f /tmp/spectra-backend-api.json

  AZURE_API_SCOPE="api://${BACKEND_APP_ID}/access_as_user"
}

set_backend_permissions() {
  log "Adding required Graph/Exchange application permissions to the backend app..."
  local perms=(User.Read.All Reports.Read.All UserAuthenticationMethod.Read.All Policy.Read.All SecurityEvents.Read.All MailboxSettings.Read Application.Read.All RoleManagement.ReadWrite.Directory)
  local graph_ids=() role_id
  for p in "${perms[@]}"; do
    role_id="$(graph_app_role_id "$p")"
    if [ -z "$role_id" ] || [ "$role_id" = "None" ]; then
      warn "Couldn't resolve the '$p' Graph permission — skipping it, add manually if needed."
      continue
    fi
    graph_ids+=("$role_id")
  done

  local exo_role_id
  exo_role_id="$(exo_app_role_id "Exchange.ManageAsApp")"

  # Comma-joined GUID list passed as one plain string arg — simpler and less
  # error-prone than trying to build/pass a JSON array through bash.
  python3 -c '
import json, sys
graph_ids = sys.argv[1].split(",") if sys.argv[1] else []
exo_id = sys.argv[2] if sys.argv[2] not in ("", "None") else None
resource_access = [{"resourceAppId": sys.argv[3], "resourceAccess": [{"id": g, "type": "Role"} for g in graph_ids]}]
if exo_id:
    resource_access.append({"resourceAppId": sys.argv[4], "resourceAccess": [{"id": exo_id, "type": "Role"}]})
print(json.dumps({"requiredResourceAccess": resource_access}))
' "$(IFS=,; echo "${graph_ids[*]:-}")" "$exo_role_id" "$GRAPH_SP_APP_ID" "$EXO_SP_APP_ID" > /tmp/spectra-backend-perms.json

  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/${BACKEND_OBJECT_ID}" \
    --headers "Content-Type=application/json" --body @/tmp/spectra-backend-perms.json
  rm -f /tmp/spectra-backend-perms.json

  warn "These are application permissions — they only take effect once each customer's own Entra admin grants consent (Settings -> Customers -> Grant consent in the app itself), not now. That's expected, see the README."
}

set_backend_redirect_uris_and_claim() {
  log "Setting the backend app's Web redirect URI(s) and groups claim..."
  local uris="[\"${SPECTRA_LOCAL_DEV_URL}\""
  [ -n "$SPECTRA_DOMAIN" ] && uris="${uris},\"https://${SPECTRA_DOMAIN}\""
  uris="${uris}]"

  python3 -c '
import json, sys
body = {
    "web": {"redirectUris": json.loads(sys.argv[1])},
    "optionalClaims": {"idToken": [], "accessToken": [{"name": "groups", "essential": False}], "saml2Token": []},
}
print(json.dumps(body))
' "$uris" > /tmp/spectra-backend-web.json

  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/${BACKEND_OBJECT_ID}" \
    --headers "Content-Type=application/json" --body @/tmp/spectra-backend-web.json
  rm -f /tmp/spectra-backend-web.json
}

create_backend_secret() {
  # Secret values can never be read back after creation (by design — this
  # is standard OAuth practice, not a gap here), so re-running this script
  # against an app it already created would otherwise pile up a fresh,
  # never-cleaned-up secret every single time. Only mint one for a genuinely
  # new app; reusing an existing one just says how to add another by hand
  # if the original secret's been lost.
  if [ "$BACKEND_APP_IS_NEW" != "yes" ]; then
    warn "Reusing an existing app registration — not generating a new client secret (can't retrieve the original one either, that's normal). If you don't still have it, create a new one: az ad app credential reset --id $BACKEND_APP_ID --append --years 2"
    AZURE_BACKEND_CLIENT_SECRET=""
    return 0
  fi
  log "Creating a client secret for the backend app..."
  AZURE_BACKEND_CLIENT_SECRET="$(az ad app credential reset --id "$BACKEND_APP_ID" --append \
    --display-name "spectra-$(date -u +%Y-%m-%d)" --years 2 --query password -o tsv)"
}

create_or_reuse_frontend_app() {
  local existing
  existing="$(find_existing_app "$SPECTRA_APP_NAME")"
  if [ -n "$existing" ]; then
    log "Reusing existing '${SPECTRA_APP_NAME}' app registration ($existing) — re-applying config to it."
    FRONTEND_APP_ID="$existing"
  else
    log "Creating frontend app registration..."
    FRONTEND_APP_ID="$(az ad app create --display-name "$SPECTRA_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  fi
  FRONTEND_OBJECT_ID="$(az ad app show --id "$FRONTEND_APP_ID" --query id -o tsv)"
}

set_frontend_redirect_uris() {
  log "Setting the frontend app's SPA redirect URI(s)..."
  local uris="[\"${SPECTRA_LOCAL_DEV_URL}\""
  [ -n "$SPECTRA_DOMAIN" ] && uris="${uris},\"https://${SPECTRA_DOMAIN}\""
  uris="${uris}]"

  python3 -c '
import json, sys
print(json.dumps({"spa": {"redirectUris": json.loads(sys.argv[1])}}))
' "$uris" > /tmp/spectra-frontend-spa.json

  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/${FRONTEND_OBJECT_ID}" \
    --headers "Content-Type=application/json" --body @/tmp/spectra-frontend-spa.json
  rm -f /tmp/spectra-frontend-spa.json
}

set_frontend_permissions() {
  log "Adding delegated Graph permissions + backend API access to the frontend app..."
  local user_read group_read access_as_user
  user_read="$(graph_delegated_scope_id User.Read.All)"
  group_read="$(graph_delegated_scope_id Group.Read.All)"
  access_as_user="$(az ad app show --id "$BACKEND_APP_ID" --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv)"

  python3 -c '
import json, sys
graph_scopes = [s for s in [sys.argv[1], sys.argv[2]] if s not in ("", "None")]
resource_access = [{"resourceAppId": "'"$GRAPH_SP_APP_ID"'", "resourceAccess": [{"id": s, "type": "Scope"} for s in graph_scopes]}]
if sys.argv[3] not in ("", "None"):
    resource_access.append({"resourceAppId": sys.argv[4], "resourceAccess": [{"id": sys.argv[3], "type": "Scope"}]})
print(json.dumps({"requiredResourceAccess": resource_access}))
' "$user_read" "$group_read" "$access_as_user" "$BACKEND_APP_ID" > /tmp/spectra-frontend-perms.json

  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/${FRONTEND_OBJECT_ID}" \
    --headers "Content-Type=application/json" --body @/tmp/spectra-frontend-perms.json
  rm -f /tmp/spectra-frontend-perms.json

  log "Attempting to grant admin consent (needs Application/Cloud Application/Global Administrator)..."
  if ! az ad app permission admin-consent --id "$FRONTEND_APP_ID" 2>/tmp/spectra-consent-err; then
    warn "Admin consent couldn't be granted automatically ($(cat /tmp/spectra-consent-err 2>/dev/null | tail -1)) — do it manually: Entra ID -> App registrations -> ${SPECTRA_APP_NAME} -> API permissions -> Grant admin consent."
  fi
  rm -f /tmp/spectra-consent-err
}

# POSTs the collected values straight to a running Spectra instance's
# /api/setup/azure-ad, using the same one-time token install.sh generated —
# skips copy-pasting into the web wizard entirely. Never hard-fails: any
# outcome here just falls through to print_summary, which always prints the
# raw values too, so a failed POST never loses information you need.
post_config_to_instance() {
  [ -n "$SPECTRA_INSTANCE_URL" ] || return 0
  log "Posting Azure AD configuration to ${SPECTRA_INSTANCE_URL}..."

  python3 -c '
import json, sys
body = {
    "setupToken": sys.argv[1],
    "tenantId": sys.argv[2],
    "frontendClientId": sys.argv[3],
    "backendClientId": sys.argv[4],
    "backendClientSecret": sys.argv[5],
    "apiScope": sys.argv[6],
}
print(json.dumps(body))
' "$SPECTRA_SETUP_TOKEN" "$AZURE_TENANT_ID" "$FRONTEND_APP_ID" "$BACKEND_APP_ID" "$AZURE_BACKEND_CLIENT_SECRET" "$AZURE_API_SCOPE" > /tmp/spectra-setup-post.json

  local http_code
  http_code="$(curl -sS -o /tmp/spectra-setup-response.json -w '%{http_code}' -X POST "${SPECTRA_INSTANCE_URL%/}/api/setup/azure-ad" \
    -H 'Content-Type: application/json' --data @/tmp/spectra-setup-post.json 2>/dev/null || echo "000")"

  case "$http_code" in
    200)
      log "Configured successfully — the server is restarting to apply it."
      SPECTRA_POSTED="yes"
      ;;
    409)
      warn "That instance is already configured. Sign in and use Settings -> Authentication if you need to change it."
      ;;
    401)
      warn "The setup token was rejected — check setup-token.txt on the target server."
      ;;
    000)
      warn "Couldn't reach ${SPECTRA_INSTANCE_URL} — check the URL and that the server is up."
      ;;
    *)
      warn "Unexpected response ($http_code): $(cat /tmp/spectra-setup-response.json 2>/dev/null)"
      ;;
  esac

  rm -f /tmp/spectra-setup-post.json /tmp/spectra-setup-response.json
}

print_summary() {
  echo
  log "Done."
  echo "  Frontend app:      ${SPECTRA_APP_NAME} (${FRONTEND_APP_ID})"
  echo "  Backend app:       ${SPECTRA_APP_NAME} API (${BACKEND_APP_ID})"
  echo "  Tenant:            ${AZURE_TENANT_ID}"
  echo
  echo "Still needs a human (can't be automated generically):"
  echo "  - If admin consent above failed, grant it manually (steps printed above, in the warning)."
  echo "  - Exchange Online PowerShell certificate, for the Email Security tab — see the README."
  echo

  if [ "$SPECTRA_POSTED" = "yes" ]; then
    echo "Configured automatically — visit https://${SPECTRA_DOMAIN:-your-domain}/ and sign in."
  else
    echo "Paste these into the setup wizard the first time you visit your Spectra URL (or Settings -> Authentication"
    echo "if it's already past first-run), or re-run this script with SPECTRA_INSTANCE_URL set to POST them directly:"
    echo
    echo "  Tenant ID:              ${AZURE_TENANT_ID}"
    echo "  Frontend client ID:     ${FRONTEND_APP_ID}"
    echo "  Backend client ID:      ${BACKEND_APP_ID}"
    echo "  Backend client secret:  ${AZURE_BACKEND_CLIENT_SECRET:-(unchanged — reusing an existing app, see the warning above)}"
    echo "  API scope:              ${AZURE_API_SCOPE}"
    echo
    echo "(the client secret above is only shown this once — save it now if you're not pasting it in right away)"
  fi
}

main() {
  ensure_az_cli
  az_login

  AZURE_TENANT_ID="$(az account show --query tenantId -o tsv)"

  echo "Spectra Azure AD setup"
  echo "======================"
  prompt SPECTRA_DOMAIN "Production domain (blank to only set up local dev, e.g. app.example.com)" ""

  create_or_reuse_backend_app
  expose_backend_api_scope
  set_backend_permissions
  set_backend_redirect_uris_and_claim
  create_backend_secret

  create_or_reuse_frontend_app
  set_frontend_redirect_uris
  set_frontend_permissions

  prompt SPECTRA_INSTANCE_URL "URL of your running Spectra instance to auto-configure (blank to skip and just print the values — e.g. https://app.example.com, or http://localhost:5080 for local dev)" ""
  if [ -n "$SPECTRA_INSTANCE_URL" ]; then
    prompt_secret SPECTRA_SETUP_TOKEN "Setup token (from setup-token.txt on the server — leave blank to read it locally if this script is running on that same server)"
    if [ -z "$SPECTRA_SETUP_TOKEN" ] && [ -r /etc/spectra/setup-token.txt ]; then
      SPECTRA_SETUP_TOKEN="$(tail -n1 /etc/spectra/setup-token.txt)"
    fi
  fi

  post_config_to_instance
  print_summary
}

# Not az here — ensure_az_cli (called from main) installs it if missing;
# requiring it up front would defeat that.
require python3 "This script builds Graph API request bodies with it."
require uuidgen "Should ship with your OS — on Debian/Ubuntu: apt-get install uuid-runtime."

main "$@"
