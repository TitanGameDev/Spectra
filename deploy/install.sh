#!/usr/bin/env bash
#
# Spectra one-shot installer for a fresh Ubuntu/Debian server.
#
#   curl -fsSL https://<wherever-you-host-this>/install.sh | bash
#
# Installs and configures, end to end:
#   - .NET 8 SDK (to build/publish the backend) + ASP.NET Core runtime
#   - Node.js LTS (to build the frontend)
#   - MySQL Server, with a dedicated database + user created for Spectra
#   - nginx, configured from deploy/nginx/spectra.conf as a reverse proxy
#   - certbot, to issue a Let's Encrypt certificate (optional; supports
#     domains proxied through Cloudflare via a DNS-01 challenge)
#   - a systemd service running the backend as a non-root user
#   - ufw firewall rules limited to 22/80/443 (optional)
#
# What it deliberately does NOT do (these need a human, per the README):
#   - Create or configure the Azure AD (Entra ID) app registrations
#   - Grant per-customer Entra admin consent
#   - Set up the Exchange Online PowerShell certificate
#   - Complete the MySQL cutover in Settings -> Database (the script only
#     provisions the empty database + user; the actual schema creation and
#     data migration happens through the app's own provisioning flow, live,
#     once you're signed in)
#
# Safe to re-run: it skips work that's already done (packages, MySQL user,
# TLS cert) and just re-pulls/rebuilds/restarts the app.
#
# Configuration is via environment variables, with an interactive /dev/tty
# prompt fallback for anything required but unset (curl | bash consumes
# stdin, so prompts read from the controlling terminal directly instead).
# See the "Configuration" section below for the full list.
#
set -euo pipefail

# Prevents any transitively-installed package (tzdata is the classic culprit)
# from popping a debconf prompt — there's no tty to answer it when this is
# run as `curl | bash`, and that would otherwise hang the install forever.
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
# Configuration (env var name -> meaning, default)
# ---------------------------------------------------------------------------
SPECTRA_REPO_URL="${SPECTRA_REPO_URL:-https://github.com/TitanGameDev/Spectra.git}"
SPECTRA_GIT_REF="${SPECTRA_GIT_REF:-main}"
# Point this at an existing local checkout to skip the git clone entirely
# (e.g. if the repo is private and you've already cloned it by hand, or this
# script itself is being run from inside the repo).
SPECTRA_SRC_DIR="${SPECTRA_SRC_DIR:-}"

SPECTRA_INSTALL_DIR="${SPECTRA_INSTALL_DIR:-/opt/spectra}"
SPECTRA_DATA_DIR="${SPECTRA_DATA_DIR:-/var/lib/spectra}"
SPECTRA_CONFIG_DIR="${SPECTRA_CONFIG_DIR:-/etc/spectra}"
SPECTRA_WEB_ROOT="${SPECTRA_WEB_ROOT:-/var/www/spectra/frontend}"
SPECTRA_SYSTEM_USER="${SPECTRA_SYSTEM_USER:-spectra}"

SPECTRA_DOMAIN="${SPECTRA_DOMAIN:-}"
SPECTRA_SETUP_TLS="${SPECTRA_SETUP_TLS:-}"          # yes/no
SPECTRA_SETUP_FIREWALL="${SPECTRA_SETUP_FIREWALL:-}" # yes/no

# Only relevant when SPECTRA_SETUP_TLS=yes. Set this if the domain is
# proxied through Cloudflare (orange-cloud) — the standard HTTP-01 challenge
# certbot's --nginx plugin uses can't work in that setup (Let's Encrypt's
# validator hits Cloudflare's edge, not this server, and gets nothing to
# check), so a DNS-01 challenge via the Cloudflare API is used instead.
SPECTRA_USE_CLOUDFLARE_DNS="${SPECTRA_USE_CLOUDFLARE_DNS:-}" # yes/no
# Cloudflare API token, scoped to Zone:DNS:Edit for this domain only (create
# one at https://dash.cloudflare.com/profile/api-tokens — not the legacy
# global API key, which has far more blast radius than this needs).
SPECTRA_CLOUDFLARE_API_TOKEN="${SPECTRA_CLOUDFLARE_API_TOKEN:-}"

SPECTRA_MYSQL_DB="${SPECTRA_MYSQL_DB:-spectra}"
SPECTRA_MYSQL_USER="${SPECTRA_MYSQL_USER:-spectra}"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
log()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m==> WARNING:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m==> ERROR:\033[0m %s\n' "$*" >&2; exit 1; }

# Reads from the controlling terminal, not stdin — required because when
# this script is run as `curl ... | bash`, stdin is the script itself, not
# an interactive shell. Falls back to the given default if no terminal is
# attached at all (e.g. unattended/CI runs), so the script still completes
# rather than hanging on a read that can never succeed.
prompt() {
  local __var="$1" __msg="$2" __default="${3:-}" __answer
  if [ -n "${!__var:-}" ]; then
    return 0
  fi
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

# Same as prompt(), but for yes/no questions — normalizes y/Y/yes/Yes/YES (and
# equivalents for "no") so a natural single-letter answer doesn't silently get
# treated as the opposite of what was intended. A prior version of this script
# did a literal string comparison against "yes", so typing "y" instead of the
# full word "yes" silently skipped TLS setup entirely with no error — exactly
# the kind of mistake this exists to prevent.
prompt_yesno() {
  local __var="$1"
  prompt "$@"
  case "${!__var}" in
    y | Y | yes | Yes | YES) printf -v "$__var" 'yes' ;;
    *) printf -v "$__var" 'no' ;;
  esac
}

# Same as prompt() but for values that shouldn't echo to the terminal or
# end up in shell history (API tokens, etc.) — read -s instead of read -r.
prompt_secret() {
  local __var="$1" __msg="$2" __answer
  if [ -n "${!__var:-}" ]; then
    return 0
  fi
  if [ -r /dev/tty ]; then
    read -rs -p "$__msg: " __answer < /dev/tty
    echo
    printf -v "$__var" '%s' "$__answer"
  else
    warn "No terminal attached to prompt for '$__msg' — leaving blank. Set \$$__var to provide it non-interactively."
    printf -v "$__var" ''
  fi
}

require_root() {
  if [ "$(id -u)" -ne 0 ]; then
    die "This script installs system packages and services — run it as root (e.g. sudo bash install.sh)."
  fi
}

require_apt() {
  command -v apt-get >/dev/null 2>&1 || die "This installer targets Ubuntu/Debian (apt-get not found). See the README's manual deployment checklist for other distros."
}

random_password() {
  openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | head -c 24
}

# nginx serves the web root as its own unprivileged worker user (www-data),
# not root — mkdir/cp as root only end up readable by that user if root's
# umask happens to allow it at every level of the path, which isn't
# guaranteed (seen in practice: /var/www itself coming out 700 on a hardened
# image, one level above the web root itself, which a chmod of just the web
# root and its immediate parent doesn't reach). Walks every ancestor
# directory up to "/" adding o+rx — only ever adds permissions, never
# removes, and directories that are already fine (almost always true above
# whatever this script itself created) are a harmless no-op.
ensure_world_traversable() {
  local dir
  dir="$(cd "$1" && pwd)"
  while [ "$dir" != "/" ]; do
    chmod o+rx "$dir"
    dir="$(dirname "$dir")"
  done
}

# ---------------------------------------------------------------------------
# Steps
# ---------------------------------------------------------------------------

install_base_packages() {
  log "Installing base packages (git, curl, openssl, ca-certificates)..."
  apt-get update -qq
  apt-get install -y -qq git curl wget ca-certificates gnupg lsb-release openssl apt-transport-https software-properties-common
}

fetch_source() {
  if [ -n "$SPECTRA_SRC_DIR" ]; then
    [ -d "$SPECTRA_SRC_DIR" ] || die "SPECTRA_SRC_DIR=$SPECTRA_SRC_DIR does not exist."
    log "Using existing local checkout at $SPECTRA_SRC_DIR (skipping git clone)."
    return 0
  fi

  SPECTRA_SRC_DIR="$SPECTRA_INSTALL_DIR/src"
  if [ -d "$SPECTRA_SRC_DIR/.git" ]; then
    log "Updating existing checkout at $SPECTRA_SRC_DIR..."
    git -C "$SPECTRA_SRC_DIR" fetch --quiet origin "$SPECTRA_GIT_REF"
    git -C "$SPECTRA_SRC_DIR" checkout --quiet "$SPECTRA_GIT_REF"
    git -C "$SPECTRA_SRC_DIR" reset --quiet --hard "origin/$SPECTRA_GIT_REF"
  else
    log "Cloning $SPECTRA_REPO_URL (ref: $SPECTRA_GIT_REF) into $SPECTRA_SRC_DIR..."
    mkdir -p "$SPECTRA_INSTALL_DIR"
    if ! git clone --quiet --branch "$SPECTRA_GIT_REF" "$SPECTRA_REPO_URL" "$SPECTRA_SRC_DIR"; then
      die "git clone failed. If this is a private repo, either: (1) set up root's git credentials for it (SSH deploy key or a credential helper) before re-running, or (2) clone it yourself and re-run with SPECTRA_SRC_DIR=/path/to/checkout."
    fi
  fi
}

install_dotnet() {
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
    log ".NET 8 SDK already installed, skipping."
    return 0
  fi
  log "Installing .NET 8 SDK + ASP.NET Core runtime (Microsoft package repo)..."
  local codename
  codename="$(lsb_release -cs)"
  wget -q "https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb \
    || wget -q "https://packages.microsoft.com/config/debian/$(lsb_release -rs)/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb \
    || die "Couldn't fetch the Microsoft package repo config for $codename. See https://learn.microsoft.com/dotnet/core/install/linux for manual instructions."
  dpkg -i /tmp/packages-microsoft-prod.deb >/dev/null
  rm -f /tmp/packages-microsoft-prod.deb
  apt-get update -qq
  # libicu: Spectra runs with InvariantGlobalization disabled (required by
  # Microsoft.Data.SqlClient for the optional SQL Server provider), which
  # needs ICU present — not guaranteed on minimal cloud images.
  apt-get install -y -qq dotnet-sdk-8.0 aspnetcore-runtime-8.0 libicu-dev
}

install_node() {
  if command -v node >/dev/null 2>&1 && [ "$(node -v | sed -E 's/^v([0-9]+).*/\1/')" -ge 20 ] 2>/dev/null; then
    log "Node.js $(node -v) already installed, skipping."
    return 0
  fi
  log "Installing Node.js 20.x (NodeSource)..."
  curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1
  apt-get install -y -qq nodejs
}

install_mysql() {
  if command -v mysql >/dev/null 2>&1 && systemctl is-active --quiet mysql 2>/dev/null; then
    log "MySQL already installed and running, skipping install."
  else
    log "Installing MySQL Server..."
    apt-get install -y -qq mysql-server
    systemctl enable --now mysql
  fi

  local user_exists
  user_exists="$(mysql -N -e "SELECT COUNT(*) FROM mysql.user WHERE user = '${SPECTRA_MYSQL_USER}' AND host = 'localhost';" 2>/dev/null || echo 0)"

  if [ "$user_exists" = "1" ]; then
    log "MySQL user '${SPECTRA_MYSQL_USER}'@'localhost' already exists — leaving its password untouched (it may already be cut over in Settings -> Database, and re-generating it here would break that)."
    SPECTRA_MYSQL_PASSWORD="(unchanged — use the password you originally set)"
  else
    log "Creating MySQL database '${SPECTRA_MYSQL_DB}' and user '${SPECTRA_MYSQL_USER}'..."
    SPECTRA_MYSQL_PASSWORD="$(random_password)"
    mysql -e "CREATE DATABASE IF NOT EXISTS \`${SPECTRA_MYSQL_DB}\` CHARACTER SET utf8mb4;"
    mysql -e "CREATE USER '${SPECTRA_MYSQL_USER}'@'localhost' IDENTIFIED BY '${SPECTRA_MYSQL_PASSWORD}';"
    mysql -e "GRANT ALL PRIVILEGES ON \`${SPECTRA_MYSQL_DB}\`.* TO '${SPECTRA_MYSQL_USER}'@'localhost';"
    mysql -e "FLUSH PRIVILEGES;"

    mkdir -p "$SPECTRA_CONFIG_DIR"
    umask 077
    cat > "$SPECTRA_CONFIG_DIR/mysql-credentials.txt" <<EOF
# Generated by install.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ) — shown once here
# because Spectra itself never re-displays a saved database password after
# you enter it in Settings -> Database. Paste these into that form to
# complete the cutover from the default local SQLite database.
Host:     localhost
Port:     3306
Database: ${SPECTRA_MYSQL_DB}
Username: ${SPECTRA_MYSQL_USER}
Password: ${SPECTRA_MYSQL_PASSWORD}
EOF
    chmod 600 "$SPECTRA_CONFIG_DIR/mysql-credentials.txt"
    umask 022
    log "MySQL credentials written to $SPECTRA_CONFIG_DIR/mysql-credentials.txt (root-only, mode 600)."
  fi
}

setup_app_user_and_dirs() {
  if ! id -u "$SPECTRA_SYSTEM_USER" >/dev/null 2>&1; then
    log "Creating system user '$SPECTRA_SYSTEM_USER'..."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SPECTRA_SYSTEM_USER"
  fi

  # $SPECTRA_DATA_DIR/home becomes spectra.service's $HOME (see
  # write_systemd_unit) — needed because ProtectHome=true hides the real
  # /home/${SPECTRA_SYSTEM_USER} from the service entirely.
  mkdir -p "$SPECTRA_INSTALL_DIR/app" "$SPECTRA_DATA_DIR" "$SPECTRA_DATA_DIR/keys" "$SPECTRA_DATA_DIR/certs" "$SPECTRA_DATA_DIR/home" "$SPECTRA_CONFIG_DIR" "$SPECTRA_WEB_ROOT"
  chown -R "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_INSTALL_DIR/app" "$SPECTRA_DATA_DIR"
}

# Lets deploy/update.sh (run later, standalone, by the updater service below —
# see write_updater_units) find everything without re-running any prompts.
write_install_conf() {
  log "Writing $SPECTRA_CONFIG_DIR/install.conf..."
  cat > "$SPECTRA_CONFIG_DIR/install.conf" <<EOF
SPECTRA_SRC_DIR=${SPECTRA_SRC_DIR}
SPECTRA_INSTALL_DIR=${SPECTRA_INSTALL_DIR}
SPECTRA_WEB_ROOT=${SPECTRA_WEB_ROOT}
SPECTRA_DATA_DIR=${SPECTRA_DATA_DIR}
SPECTRA_GIT_REF=${SPECTRA_GIT_REF}
SPECTRA_SYSTEM_USER=${SPECTRA_SYSTEM_USER}
EOF
  chmod 644 "$SPECTRA_CONFIG_DIR/install.conf"
}

# Shared convention with deploy/update.sh's write_version — read by
# GET /api/settings/update-status (Settings -> Updates panel) to show what's
# currently deployed. Written only after a successful build, so a failed
# build/update never makes the version display lie about what's running.
write_version_file() {
  local commit deployed_at
  commit="$(git -C "$SPECTRA_SRC_DIR" rev-parse HEAD)"
  deployed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  cat > "$SPECTRA_DATA_DIR/version.json" <<EOF
{"commit": "${commit}", "ref": "${SPECTRA_GIT_REF}", "deployedAt": "${deployed_at}"}
EOF
  chown "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_DATA_DIR/version.json"
  chmod 644 "$SPECTRA_DATA_DIR/version.json"
}

# The Settings -> Updates "Update now" button (see README) only ever writes
# $SPECTRA_DATA_DIR/update-requested — a file the unprivileged 'spectra' user
# already has write access to (ReadWritePaths in spectra.service covers
# $SPECTRA_DATA_DIR). This .path unit is what turns that into an actual
# privileged update: it watches for the flag and triggers the oneshot
# .service below, which runs deploy/update.sh AS ROOT. The web app process
# itself never gains any new capability — deploy/update.sh deletes the flag
# file as its first action, which is what stops the .path unit from
# re-triggering in a loop once the file exists.
write_updater_units() {
  log "Installing the update-watcher systemd units..."
  cat > /etc/systemd/system/spectra-updater.path <<EOF
[Unit]
Description=Watch for Spectra update requests

[Path]
PathExists=${SPECTRA_DATA_DIR}/update-requested
Unit=spectra-updater.service

[Install]
WantedBy=multi-user.target
EOF

  cat > /etc/systemd/system/spectra-updater.service <<EOF
[Unit]
Description=Spectra update (git pull, rebuild, restart)

[Service]
Type=oneshot
User=root
ExecStart=/usr/bin/env bash ${SPECTRA_SRC_DIR}/deploy/update.sh
EOF

  systemctl daemon-reload
  systemctl enable --now spectra-updater.path
}

# A lighter sibling to write_updater_units — saving Azure AD config from the
# setup wizard or Settings -> Authentication only ever writes
# $SPECTRA_DATA_DIR/restart-requested, a separate flag from update-requested so
# it doesn't trigger update.sh's full git-pull/rebuild cycle (see
# AppUpdateService.RequestRestart's doc comment for why a restart is needed at
# all: Microsoft.Identity.Web reads Tenant/ClientId once at process start).
# The oneshot service removes the flag itself before restarting, same
# loop-prevention convention as deploy/update.sh.
write_restarter_units() {
  log "Installing the restart-watcher systemd units..."
  cat > /etc/systemd/system/spectra-restarter.path <<EOF
[Unit]
Description=Watch for Spectra restart requests

[Path]
PathExists=${SPECTRA_DATA_DIR}/restart-requested
Unit=spectra-restarter.service

[Install]
WantedBy=multi-user.target
EOF

  cat > /etc/systemd/system/spectra-restarter.service <<EOF
[Unit]
Description=Spectra restart (picks up new Azure AD config)

[Service]
Type=oneshot
User=root
ExecStart=/usr/bin/env bash -c 'rm -f ${SPECTRA_DATA_DIR}/restart-requested; systemctl restart spectra'
EOF

  systemctl daemon-reload
  systemctl enable --now spectra-restarter.path
}

build_backend() {
  log "Publishing the backend (dotnet publish -c Release)..."
  # Staged into app.new and swapped in, not published straight into
  # $SPECTRA_INSTALL_DIR/app — on a fresh install nothing has that directory
  # open yet, but install.sh is also safe to rerun against an already-running
  # instance (e.g. to pick up new deploy-script changes), and overwriting the
  # live directory in place while spectra.service still holds the old
  # Spectra.Api.dll/.pdb open can fail partway (see update.sh's equivalent
  # step for the full explanation).
  rm -rf "$SPECTRA_INSTALL_DIR/app.new"
  dotnet publish "$SPECTRA_SRC_DIR/backend/Spectra.Api.csproj" -c Release -o "$SPECTRA_INSTALL_DIR/app.new" --nologo
  chown -R "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_INSTALL_DIR/app.new"

  # None of these are build output — they're runtime state the running app
  # writes into its own ContentRootPath (== $SPECTRA_INSTALL_DIR/app, see
  # Program.cs/AzureAdBootstrapStore.cs/ActiveDatabaseProvider.cs) and must
  # survive a rerun against an already-installed instance: the Data
  # Protection key ring (encrypts the Azure AD client secret and any
  # external DB password stored in the database — losing it makes that data
  # permanently undecryptable, not just briefly broken), the Azure AD JWT
  # bootstrap marker (inbound token audience validation reads this once at
  # startup), and the active-database marker. No-op on a genuinely fresh
  # install, since none of these exist yet.
  for item in keys azuread-bootstrap.json database-provider.json; do
    if [ -e "$SPECTRA_INSTALL_DIR/app/$item" ]; then
      cp -a "$SPECTRA_INSTALL_DIR/app/$item" "$SPECTRA_INSTALL_DIR/app.new/$item"
    fi
  done

  rm -rf "$SPECTRA_INSTALL_DIR/app"
  mv "$SPECTRA_INSTALL_DIR/app.new" "$SPECTRA_INSTALL_DIR/app"
}

build_frontend() {
  log "Building the frontend (npm ci && npm run build)..."
  # No VITE_MSAL_*/VITE_API_SCOPE env vars needed here anymore — the frontend
  # fetches its Azure AD config from the backend (/api/public/auth-config) at
  # load time instead of having it baked in at build time. Builds the same way
  # regardless of domain or Azure AD setup state.
  #
  # VITE_API_BASE_URL is still needed though: Vite bakes it in at build time,
  # and it must be "" so API calls are same-origin through the nginx proxy
  # (see frontend/.env.example) rather than literally fetching "undefined/...".
  ( cd "$SPECTRA_SRC_DIR/frontend" && npm ci --silent && VITE_API_BASE_URL="" npm run build --silent )
  rm -rf "${SPECTRA_WEB_ROOT:?}"/*
  cp -r "$SPECTRA_SRC_DIR/frontend/dist/." "$SPECTRA_WEB_ROOT/"
  ensure_world_traversable "$SPECTRA_WEB_ROOT"
  chmod -R o+rX "$SPECTRA_WEB_ROOT"
}

# Azure AD config (tenant/client IDs, the backend secret) no longer lives here
# at all — it's set through the web setup wizard after install (or
# deploy/setup-azure-ad.sh's auto-POST) and stored in the database. Only the
# setup token itself lives in backend.env, so the backend can check it — see
# write_setup_token_file, which must run before this so SPECTRA_SETUP_TOKEN
# is already set. Only generates a new token if one doesn't already exist —
# re-running install.sh shouldn't invalidate a token someone's already using,
# same convention as install_mysql's password handling.
write_setup_token_file() {
  mkdir -p "$SPECTRA_CONFIG_DIR"
  if [ -f "$SPECTRA_CONFIG_DIR/setup-token.txt" ]; then
    log "Setup token already exists at $SPECTRA_CONFIG_DIR/setup-token.txt, leaving it untouched."
    SPECTRA_SETUP_TOKEN="$(tail -n1 "$SPECTRA_CONFIG_DIR/setup-token.txt")"
  else
    log "Generating the Azure AD setup token..."
    SPECTRA_SETUP_TOKEN="$(openssl rand -hex 32)"
    umask 077
    cat > "$SPECTRA_CONFIG_DIR/setup-token.txt" <<EOF
# Generated by install.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ) — one-time token for
# completing Azure AD setup, either via the web wizard at https://${SPECTRA_DOMAIN}/
# or by running deploy/setup-azure-ad.sh with SPECTRA_INSTANCE_URL set (it will
# POST directly using this token, no copy-paste needed). Safe to delete once
# setup is complete — the server rejects this token unconditionally afterward.
${SPECTRA_SETUP_TOKEN}
EOF
    chmod 600 "$SPECTRA_CONFIG_DIR/setup-token.txt"
    umask 022
    log "Setup token written to $SPECTRA_CONFIG_DIR/setup-token.txt (root-only, mode 600)."
  fi
}

write_backend_env() {
  log "Writing backend environment file to $SPECTRA_CONFIG_DIR/backend.env..."
  umask 077
  cat > "$SPECTRA_CONFIG_DIR/backend.env" <<EOF
# Generated by install.sh — root-owned, mode 600, read by the spectra.service
# systemd unit (EnvironmentFile=). Azure AD itself is configured through the
# web setup wizard, not here — see write_setup_token_file's doc comment.
Setup__Token=${SPECTRA_SETUP_TOKEN}

# Required only for the Email Security sub-tab — see README "Exchange Online
# security checks". Deploy the .pfx to $SPECTRA_DATA_DIR/certs/ and set both here.
Exo__CertificatePath=
Exo__CertificatePassword=

Cors__AllowedOrigin=https://${SPECTRA_DOMAIN}
ASPNETCORE_URLS=http://127.0.0.1:5080
ConnectionStrings__Default=Data Source=${SPECTRA_DATA_DIR}/spectra.db

# Settings -> Updates panel (see README's "One-click update") — lets the
# backend read version.json/update-status.json and request an update by
# writing the flag file spectra-updater.path watches for. Never grants the
# backend any elevated privilege itself.
Update__DataDir=${SPECTRA_DATA_DIR}
Update__SrcDir=${SPECTRA_SRC_DIR}
EOF
  chmod 600 "$SPECTRA_CONFIG_DIR/backend.env"
  chown root:root "$SPECTRA_CONFIG_DIR/backend.env"
  umask 022
}

write_systemd_unit() {
  log "Writing systemd unit spectra.service..."
  cat > /etc/systemd/system/spectra.service <<EOF
[Unit]
Description=Spectra backend API
After=network.target mysql.service

[Service]
Type=simple
User=${SPECTRA_SYSTEM_USER}
Group=${SPECTRA_SYSTEM_USER}
WorkingDirectory=${SPECTRA_INSTALL_DIR}/app
ExecStart=/usr/bin/dotnet ${SPECTRA_INSTALL_DIR}/app/Spectra.Api.dll
EnvironmentFile=${SPECTRA_CONFIG_DIR}/backend.env
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
# ProtectSystem=strict makes /tmp read-only along with everything else not
# listed in ReadWritePaths below — without PrivateTmp, .NET's Data Protection
# system fails the moment it needs to create its first encryption key (it
# writes a temp file via Path.GetTempFileName() as part of that, regardless
# of where PersistKeysToFileSystem actually points). PrivateTmp gives this
# service its own isolated, writable tmpfs /tmp instead of exposing (or
# blocking) the host's real one — the standard, recommended pairing with
# ProtectSystem=strict, not a loosening of it.
PrivateTmp=true
# Data Protection keys and database-provider.json are both written relative
# to the app's own ContentRootPath (see Program.cs), not just \$SPECTRA_DATA_DIR
# — both need to stay writable across restarts, the keys directory especially,
# since losing it makes any previously-encrypted data (e.g. the saved MySQL
# password in Settings -> Database) permanently undecryptable.
ReadWritePaths=${SPECTRA_DATA_DIR} ${SPECTRA_INSTALL_DIR}/app
ProtectHome=true
# ProtectHome=true hides /home entirely — including the 'spectra' system
# user's own passwd-entry \$HOME (/home/${SPECTRA_SYSTEM_USER}, per useradd's
# default, even though --no-create-home means it was never created), which
# the ExchangeOnlineManagement PowerShell module needs a real writable value
# for (its own cache/config files) or it fails to import at all ("Cannot bind
# argument to parameter 'Path' because it is an empty string"). Pointed at a
# subdirectory of \$SPECTRA_DATA_DIR instead, already covered by
# ReadWritePaths above, rather than loosening ProtectHome.
Environment=HOME=${SPECTRA_DATA_DIR}/home

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
}

# Always starts from a plain-HTTP config, whether or not TLS was requested —
# certbot's HTTP-01 challenge (setup_certbot below) needs a working port-80
# vhost to answer it, and pre-writing a config that references a certificate
# certbot hasn't issued yet would just make `nginx -t` fail before certbot
# ever runs. This is a hand-written equivalent of deploy/nginx/spectra.conf
# (which keeps all its actual serving logic inside the :443 block only, since
# it's meant for a server that already has a cert) rather than a sed
# transform of that file — much easier to keep correct than surgically
# stripping SSL-only directives out of the real config in place. That
# checked-in file stays as the reference template for anyone deploying
# without this script; setup_certbot below upgrades this one to TLS in place
# when requested, via `certbot --nginx`, which preserves everything below
# (including the security headers) and just adds the SSL directives/redirect.
setup_nginx() {
  log "Installing and configuring nginx..."
  apt-get install -y -qq nginx

  [ -f "$SPECTRA_SRC_DIR/deploy/nginx/spectra.conf" ] || die "Expected $SPECTRA_SRC_DIR/deploy/nginx/spectra.conf to exist — is SPECTRA_SRC_DIR pointing at a full Spectra checkout?"

  cat > /etc/nginx/sites-available/spectra.conf <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name ${SPECTRA_DOMAIN};

    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    add_header Permissions-Policy "geolocation=(), microphone=(), camera=()" always;
    add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; font-src 'self'; connect-src 'self' https://login.microsoftonline.com https://graph.microsoft.com; frame-src https://login.microsoftonline.com; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'" always;

    root ${SPECTRA_WEB_ROOT};
    index index.html;

    location / {
        try_files \$uri \$uri/ /index.html;
    }

    location = /index.html {
        add_header Cache-Control "no-cache";
    }

    location /assets/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }

    location /api/ {
        limit_req zone=api_zone burst=20 nodelay;

        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
EOF
  ln -sf /etc/nginx/sites-available/spectra.conf /etc/nginx/sites-enabled/spectra.conf
  [ -f /etc/nginx/sites-enabled/default ] && rm -f /etc/nginx/sites-enabled/default

  # spectra.conf documents this zone must live in the main http{} block.
  if ! grep -rq 'limit_req_zone.*api_zone' /etc/nginx/nginx.conf /etc/nginx/conf.d/*.conf 2>/dev/null; then
    sed -i '/^http {/a\    limit_req_zone $binary_remote_addr zone=api_zone:10m rate=5r/s;' /etc/nginx/nginx.conf
  fi

  nginx -t
  # certbot's HTTP-01 challenge (setup_certbot below) needs nginx already
  # serving this config, not just syntactically valid on disk.
  systemctl enable --now nginx >/dev/null 2>&1 || true
  systemctl reload nginx
}

setup_certbot() {
  [ "$SPECTRA_SETUP_TLS" = "yes" ] || { log "Skipping certbot (SPECTRA_SETUP_TLS != yes) — serving plain HTTP. Re-run with SPECTRA_SETUP_TLS=yes once DNS is ready."; return 0; }

  if [ "$SPECTRA_USE_CLOUDFLARE_DNS" = "yes" ]; then
    setup_certbot_cloudflare_dns
    return 0
  fi

  log "Installing certbot and requesting a certificate for ${SPECTRA_DOMAIN}..."
  apt-get install -y -qq certbot python3-certbot-nginx
  warn "certbot needs ${SPECTRA_DOMAIN} to already resolve to this server's public IP over port 80 — if it doesn't yet, this step will fail; fix DNS and re-run with SPECTRA_SETUP_TLS=yes."
  # --nginx edits the port-80 server block setup_nginx just wrote: adds a
  # 443/ssl server block with the new cert and, via --redirect, turns the
  # existing port-80 block into a redirect to it. Also adds HSTS itself.
  certbot --nginx -d "$SPECTRA_DOMAIN" --non-interactive --agree-tos -m "admin@${SPECTRA_DOMAIN}" --redirect
}

# Standard HTTP-01 (the `certbot --nginx` path above) needs Let's Encrypt's
# own validator to reach this server directly on port 80 — that breaks the
# moment the domain is proxied through Cloudflare (orange-cloud), since the
# validation request hits Cloudflare's edge, not this box, and Cloudflare
# has nothing to answer the challenge with. A DNS-01 challenge sidesteps
# this entirely — it proves domain ownership via a TXT record instead of an
# HTTP request, so it works regardless of proxy state, and (unlike the
# HTTP-01 + temporarily-disable-the-proxy workaround some guides suggest)
# certbot's renewal timer keeps working completely unattended afterwards,
# with the proxy left on the whole time.
setup_certbot_cloudflare_dns() {
  log "Installing certbot's Cloudflare DNS plugin and requesting a certificate for ${SPECTRA_DOMAIN}..."
  apt-get install -y -qq certbot python3-certbot-dns-cloudflare

  local cred_file="/etc/letsencrypt/cloudflare.ini"
  mkdir -p /etc/letsencrypt
  umask 077
  cat > "$cred_file" <<EOF
dns_cloudflare_api_token = ${SPECTRA_CLOUDFLARE_API_TOKEN}
EOF
  chmod 600 "$cred_file"
  umask 022

  # certonly (not --nginx) just obtains the cert — it deliberately doesn't
  # touch nginx config at all, since the --nginx plugin's HTTP-01 auto-config
  # assumptions don't apply here. write_tls_nginx_config below does that part.
  certbot certonly --dns-cloudflare --dns-cloudflare-credentials "$cred_file" \
    --dns-cloudflare-propagation-seconds 30 \
    -d "$SPECTRA_DOMAIN" --non-interactive --agree-tos -m "admin@${SPECTRA_DOMAIN}"

  write_tls_nginx_config
  nginx -t
  systemctl reload nginx

  warn "Certificate issued. Now go to your Cloudflare dashboard -> SSL/TLS -> Overview and set the encryption mode to \"Full\" or \"Full (strict)\" — it was likely on \"Flexible\", or \"Full\" was failing before this cert existed. Until you do, Cloudflare will keep refusing to connect to this origin over HTTPS."
}

# Only used by the Cloudflare DNS-01 path above — certbot's --nginx plugin
# (the non-Cloudflare path in setup_certbot) edits nginx's config itself;
# `certbot certonly` deliberately doesn't, so this writes the TLS server
# block ourselves once the certificate files exist. Same shape as
# deploy/nginx/spectra.conf, just generated rather than sed'd from a
# template, and pointed at certbot's own cert paths instead of placeholders.
write_tls_nginx_config() {
  local cert_dir="/etc/letsencrypt/live/${SPECTRA_DOMAIN}"
  [ -f "$cert_dir/fullchain.pem" ] || die "Expected $cert_dir/fullchain.pem to exist after certbot ran — something went wrong."

  cat > /etc/nginx/sites-available/spectra.conf <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name ${SPECTRA_DOMAIN};
    return 301 https://\$host\$request_uri;
}

server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name ${SPECTRA_DOMAIN};

    ssl_certificate     ${cert_dir}/fullchain.pem;
    ssl_certificate_key ${cert_dir}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;

    add_header Strict-Transport-Security "max-age=63072000; includeSubDomains; preload" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    add_header Permissions-Policy "geolocation=(), microphone=(), camera=()" always;
    add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; font-src 'self'; connect-src 'self' https://login.microsoftonline.com https://graph.microsoft.com; frame-src https://login.microsoftonline.com; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'" always;

    root ${SPECTRA_WEB_ROOT};
    index index.html;

    location / {
        try_files \$uri \$uri/ /index.html;
    }

    location = /index.html {
        add_header Cache-Control "no-cache";
    }

    location /assets/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }

    location /api/ {
        limit_req zone=api_zone burst=20 nodelay;

        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
EOF
}

setup_firewall() {
  [ "$SPECTRA_SETUP_FIREWALL" = "yes" ] || { log "Skipping firewall setup (SPECTRA_SETUP_FIREWALL != yes)."; return 0; }
  command -v ufw >/dev/null 2>&1 || apt-get install -y -qq ufw
  log "Configuring ufw (allow SSH, 80, 443; deny everything else)..."
  ufw allow OpenSSH >/dev/null
  ufw allow 80/tcp >/dev/null
  ufw allow 443/tcp >/dev/null
  ufw --force enable >/dev/null
}

start_services() {
  log "Starting services..."
  systemctl enable --now spectra
  systemctl reload-or-restart nginx
  systemctl restart spectra
}

print_summary() {
  echo
  log "Done."
  echo "  App URL:            https://${SPECTRA_DOMAIN}  (or http:// if TLS wasn't set up yet)"
  echo "  Backend service:    systemctl status spectra"
  echo "  Backend logs:       journalctl -u spectra -f"
  echo "  Backend env file:   ${SPECTRA_CONFIG_DIR}/backend.env"
  echo "  Updates:            Settings -> Updates in the app from now on (git pull/rebuild/restart, no SSH needed)"
  echo "  Setup token:        ${SPECTRA_CONFIG_DIR}/setup-token.txt (needed once, for the Azure AD setup wizard below)"
  if [ -f "$SPECTRA_CONFIG_DIR/mysql-credentials.txt" ]; then
    echo "  MySQL credentials:  ${SPECTRA_CONFIG_DIR}/mysql-credentials.txt (paste into Settings -> Database to cut over from SQLite)"
  fi
  echo
  echo "Still needs a human, before the app is fully usable:"
  echo "  1. Run deploy/setup-azure-ad.sh (from anywhere with the Azure CLI, not necessarily this server) to"
  echo "     create the Azure AD app registrations, then either let it auto-POST the result here (set"
  echo "     SPECTRA_INSTANCE_URL=https://${SPECTRA_DOMAIN}) or open https://${SPECTRA_DOMAIN}/ yourself and paste"
  echo "     the values it prints into the setup wizard, using the token from setup-token.txt above."
  echo "  2. Once a customer's admin consent is needed, Settings -> Customers handles that per-customer —"
  echo "     see the README, nothing generic to automate there."
  echo "  3. Sign in, then Settings -> Database to cut over to MySQL using the credentials above."
  echo "  4. Optional: Exchange Online PowerShell setup (pwsh + ExchangeOnlineManagement module + the"
  echo "     .pfx certificate) for the Email Security tab — see the README."
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
  require_root
  require_apt

  echo "Spectra installer"
  echo "=================="
  prompt SPECTRA_DOMAIN "Domain this app will be served from (e.g. spectra.example.com)"
  [ -n "$SPECTRA_DOMAIN" ] || die "A domain is required (nginx/certbot need it) — set SPECTRA_DOMAIN or answer the prompt."
  prompt_yesno SPECTRA_SETUP_TLS "Set up HTTPS now via Let's Encrypt? DNS for $SPECTRA_DOMAIN must already point here (yes/no)" "no"
  if [ "$SPECTRA_SETUP_TLS" = "yes" ]; then
    prompt_yesno SPECTRA_USE_CLOUDFLARE_DNS "Is $SPECTRA_DOMAIN proxied through Cloudflare (orange-cloud)? The standard HTTP challenge can't work through that, so we'll use a DNS challenge via the Cloudflare API instead (yes/no)" "no"
    if [ "$SPECTRA_USE_CLOUDFLARE_DNS" = "yes" ]; then
      prompt_secret SPECTRA_CLOUDFLARE_API_TOKEN "Cloudflare API token (Zone:DNS:Edit, scoped to this domain — create one at https://dash.cloudflare.com/profile/api-tokens)"
      [ -n "$SPECTRA_CLOUDFLARE_API_TOKEN" ] || die "A Cloudflare API token is required for the DNS challenge (or answer 'no' above to use the standard HTTP challenge instead, if the domain isn't actually proxied)."
    fi
  fi
  prompt_yesno SPECTRA_SETUP_FIREWALL "Configure ufw to only allow SSH/80/443? (yes/no)" "no"

  install_base_packages
  fetch_source
  install_dotnet
  install_node
  install_mysql
  setup_app_user_and_dirs
  write_install_conf
  build_backend
  build_frontend
  write_version_file
  write_setup_token_file
  write_backend_env
  write_systemd_unit
  write_updater_units
  write_restarter_units
  setup_nginx
  setup_certbot
  setup_firewall
  start_services
  print_summary
}

main "$@"
