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
#   - certbot, to issue a Let's Encrypt certificate (optional)
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

SPECTRA_MYSQL_DB="${SPECTRA_MYSQL_DB:-spectra}"
SPECTRA_MYSQL_USER="${SPECTRA_MYSQL_USER:-spectra}"

# Azure AD values for the backend/frontend .env files — optional at install
# time (the app starts fine without them, individual features just stay
# disabled until you fill these in), but worth capturing up front if you
# already have them. See README's "Azure AD (Entra ID) setup".
AZURE_TENANT_ID="${AZURE_TENANT_ID:-}"
AZURE_BACKEND_CLIENT_ID="${AZURE_BACKEND_CLIENT_ID:-}"
AZURE_FRONTEND_CLIENT_ID="${AZURE_FRONTEND_CLIENT_ID:-}"
AZURE_API_SCOPE="${AZURE_API_SCOPE:-}"

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
    log "MySQL credentials written to $SPECTRA_CONFIG_DIR/mysql-credentials.txt (root-only, mode 600)."
  fi
}

setup_app_user_and_dirs() {
  if ! id -u "$SPECTRA_SYSTEM_USER" >/dev/null 2>&1; then
    log "Creating system user '$SPECTRA_SYSTEM_USER'..."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SPECTRA_SYSTEM_USER"
  fi

  mkdir -p "$SPECTRA_INSTALL_DIR/app" "$SPECTRA_DATA_DIR" "$SPECTRA_DATA_DIR/keys" "$SPECTRA_DATA_DIR/certs" "$SPECTRA_CONFIG_DIR" "$SPECTRA_WEB_ROOT"
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

build_backend() {
  log "Publishing the backend (dotnet publish -c Release)..."
  dotnet publish "$SPECTRA_SRC_DIR/backend/Spectra.Api.csproj" -c Release -o "$SPECTRA_INSTALL_DIR/app" --nologo
  chown -R "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_INSTALL_DIR/app"
}

build_frontend() {
  log "Building the frontend (npm ci && npm run build)..."
  local env_file="$SPECTRA_SRC_DIR/frontend/.env.production.local"
  cat > "$env_file" <<EOF
VITE_MSAL_CLIENT_ID=${AZURE_FRONTEND_CLIENT_ID}
VITE_MSAL_TENANT_ID=${AZURE_TENANT_ID}
VITE_MSAL_REDIRECT_URI=https://${SPECTRA_DOMAIN}
VITE_API_BASE_URL=
VITE_API_SCOPE=${AZURE_API_SCOPE}
EOF
  ( cd "$SPECTRA_SRC_DIR/frontend" && npm ci --silent && npm run build --silent )
  rm -rf "${SPECTRA_WEB_ROOT:?}"/*
  cp -r "$SPECTRA_SRC_DIR/frontend/dist/." "$SPECTRA_WEB_ROOT/"
}

write_backend_env() {
  log "Writing backend environment file to $SPECTRA_CONFIG_DIR/backend.env..."
  umask 077
  cat > "$SPECTRA_CONFIG_DIR/backend.env" <<EOF
# Generated by install.sh — root-owned, mode 600, read by the spectra.service
# systemd unit (EnvironmentFile=). Fill in any blanks below per the README's
# "Azure AD (Entra ID) setup" and re-run: systemctl restart spectra.
AzureAd__Instance=https://login.microsoftonline.com/
AzureAd__TenantId=${AZURE_TENANT_ID}
AzureAd__ClientId=${AZURE_BACKEND_CLIENT_ID}
AzureAd__ClientSecret=

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
# Data Protection keys and database-provider.json are both written relative
# to the app's own ContentRootPath (see Program.cs), not just \$SPECTRA_DATA_DIR
# — both need to stay writable across restarts, the keys directory especially,
# since losing it makes any previously-encrypted data (e.g. the saved MySQL
# password in Settings -> Database) permanently undecryptable.
ReadWritePaths=${SPECTRA_DATA_DIR} ${SPECTRA_INSTALL_DIR}/app
ProtectHome=true

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
  log "Installing certbot and requesting a certificate for ${SPECTRA_DOMAIN}..."
  apt-get install -y -qq certbot python3-certbot-nginx
  warn "certbot needs ${SPECTRA_DOMAIN} to already resolve to this server's public IP over port 80 — if it doesn't yet, this step will fail; fix DNS and re-run with SPECTRA_SETUP_TLS=yes."
  # --nginx edits the port-80 server block setup_nginx just wrote: adds a
  # 443/ssl server block with the new cert and, via --redirect, turns the
  # existing port-80 block into a redirect to it. Also adds HSTS itself.
  certbot --nginx -d "$SPECTRA_DOMAIN" --non-interactive --agree-tos -m "admin@${SPECTRA_DOMAIN}" --redirect
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
  if [ -f "$SPECTRA_CONFIG_DIR/mysql-credentials.txt" ]; then
    echo "  MySQL credentials:  ${SPECTRA_CONFIG_DIR}/mysql-credentials.txt (paste into Settings -> Database to cut over from SQLite)"
  fi
  echo
  echo "Still needs a human, before the app is fully usable:"
  echo "  1. If AzureAd__ClientId/TenantId weren't provided, fill them into ${SPECTRA_CONFIG_DIR}/backend.env"
  echo "     and the frontend build's env, then re-run this script (or 'systemctl restart spectra')."
  echo "  2. Complete the Azure AD app registration steps in the README (redirect URIs, API permissions,"
  echo "     per-customer admin consent) — none of that can be automated generically."
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
  prompt SPECTRA_SETUP_TLS "Set up HTTPS now via Let's Encrypt? DNS for $SPECTRA_DOMAIN must already point here (yes/no)" "no"
  prompt SPECTRA_SETUP_FIREWALL "Configure ufw to only allow SSH/80/443? (yes/no)" "no"
  prompt AZURE_TENANT_ID "Azure AD tenant ID (blank to fill in later)" ""
  prompt AZURE_BACKEND_CLIENT_ID "Azure AD backend app registration client ID (blank to fill in later)" ""
  prompt AZURE_FRONTEND_CLIENT_ID "Azure AD frontend app registration client ID (blank to fill in later)" ""
  prompt AZURE_API_SCOPE "Backend API scope, e.g. api://<backend-client-id>/access_as_user (blank to fill in later)" ""

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
  write_backend_env
  write_systemd_unit
  write_updater_units
  setup_nginx
  setup_certbot
  setup_firewall
  start_services
  print_summary
}

main "$@"
