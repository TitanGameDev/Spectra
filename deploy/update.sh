#!/usr/bin/env bash
#
# Pulls the latest code, rebuilds, and restarts Spectra in place. Run as root
# by the spectra-updater.service systemd unit (see install.sh), which is
# itself triggered by spectra-updater.path watching for the existence of
# $SPECTRA_DATA_DIR/update-requested — that file is the only thing the web
# app process (running unprivileged as the 'spectra' user) ever touches.
# This script is what actually does the privileged work: git pull, dotnet
# publish, npm build, systemctl restart.
#
# Not meant to be run interactively — sources config written by install.sh
# rather than prompting. If you're setting Spectra up for the first time,
# run install.sh instead.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

CONFIG_FILE="/etc/spectra/install.conf"
[ -f "$CONFIG_FILE" ] || { echo "ERROR: $CONFIG_FILE not found — this script expects to run on a box already set up by install.sh." >&2; exit 1; }
# shellcheck source=/dev/null
source "$CONFIG_FILE"

: "${SPECTRA_SRC_DIR:?missing from $CONFIG_FILE}"
: "${SPECTRA_INSTALL_DIR:?missing from $CONFIG_FILE}"
: "${SPECTRA_WEB_ROOT:?missing from $CONFIG_FILE}"
: "${SPECTRA_DATA_DIR:?missing from $CONFIG_FILE}"
: "${SPECTRA_GIT_REF:?missing from $CONFIG_FILE}"
: "${SPECTRA_SYSTEM_USER:?missing from $CONFIG_FILE}"

STATUS_FILE="$SPECTRA_DATA_DIR/update-status.json"
VERSION_FILE="$SPECTRA_DATA_DIR/version.json"
REQUEST_FLAG="$SPECTRA_DATA_DIR/update-requested"

log() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }

# Same helper as install.sh's ensure_world_traversable — walks every
# ancestor directory of $1 up to "/" adding o+rx, so nginx's unprivileged
# www-data worker can traverse into the web root regardless of what umask
# was in effect (or what an ancestor directory's existing permissions were)
# when it was created. Only ever adds permissions, never removes.
ensure_world_traversable() {
  local dir
  dir="$(cd "$1" && pwd)"
  while [ "$dir" != "/" ]; do
    chmod o+rx "$dir"
    dir="$(dirname "$dir")"
  done
}

# A JSON string value is only ever one of: a fixed literal ("running" etc), an
# ISO-8601 timestamp, or command output that might contain quotes/newlines
# (a build failure's error text) — escape defensively rather than assume.
json_escape() {
  printf '%s' "$1" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))' 2>/dev/null \
    || printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e ':a;N;$!ba;s/\n/\\n/g')"
}

write_status() {
  local state="$1" message="${2:-}" started="${3:-}" finished="${4:-}"
  cat > "$STATUS_FILE" <<EOF
{"state": $(json_escape "$state"), "message": $([ -n "$message" ] && json_escape "$message" || echo null), "startedAt": $([ -n "$started" ] && json_escape "$started" || echo null), "finishedAt": $([ -n "$finished" ] && json_escape "$finished" || echo null)}
EOF
  chown "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$STATUS_FILE"
  chmod 644 "$STATUS_FILE"
}

write_version() {
  local commit ref deployed_at
  commit="$(git -C "$SPECTRA_SRC_DIR" rev-parse HEAD)"
  ref="$SPECTRA_GIT_REF"
  deployed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  cat > "$VERSION_FILE" <<EOF
{"commit": $(json_escape "$commit"), "ref": $(json_escape "$ref"), "deployedAt": $(json_escape "$deployed_at")}
EOF
  chown "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$VERSION_FILE"
  chmod 644 "$VERSION_FILE"
}

STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# A failed build must never leave update-status.json stuck on "running" —
# and since the restart (below) only happens after a successful build, a
# failed build never takes down the currently-running, working instance.
on_error() {
  local exit_code=$?
  write_status "failed" "Update failed (exit $exit_code) — see journalctl -u spectra-updater for details." "$STARTED_AT" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  exit "$exit_code"
}
trap on_error ERR

mkdir -p "$SPECTRA_DATA_DIR"
write_status "running" "" "$STARTED_AT" ""

# First action, before any long-running work: spectra-updater.path re-triggers
# for as long as this file exists, so it has to go before anything that could
# fail and leave it lying around.
rm -f "$REQUEST_FLAG"

log "Pulling latest code (ref: $SPECTRA_GIT_REF)..."
git -C "$SPECTRA_SRC_DIR" fetch --quiet origin "$SPECTRA_GIT_REF"
git -C "$SPECTRA_SRC_DIR" checkout --quiet "$SPECTRA_GIT_REF"
git -C "$SPECTRA_SRC_DIR" reset --quiet --hard "origin/$SPECTRA_GIT_REF"

log "Publishing the backend..."
# Publish into a staging directory rather than straight into
# $SPECTRA_INSTALL_DIR/app — spectra.service is still running at this point
# with the old Spectra.Api.dll/.pdb open, and overwriting those files in
# place while they're in use can fail partway (seen as MSB3021/"could not
# copy... being used by another process"), leaving the live directory in a
# broken half-published state that only surfaces on the next restart. The
# swap below only touches the live directory once the build has fully
# succeeded.
rm -rf "$SPECTRA_INSTALL_DIR/app.new"
dotnet publish "$SPECTRA_SRC_DIR/backend/Spectra.Api.csproj" -c Release -o "$SPECTRA_INSTALL_DIR/app.new" --nologo
chown -R "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_INSTALL_DIR/app.new"

# None of these are build output — they're runtime state the running app
# writes into its own ContentRootPath (== $SPECTRA_INSTALL_DIR/app, see
# Program.cs/AzureAdBootstrapStore.cs/ActiveDatabaseProvider.cs) and must
# survive every publish: the Data Protection key ring (encrypts the Azure AD
# client secret and any external DB password stored in the database — losing
# it makes that data permanently undecryptable, not just briefly broken),
# the Azure AD JWT bootstrap marker (inbound token audience validation reads
# this once at startup — losing it breaks every signed-in request until
# Azure AD is reconfigured), and the active-database marker. Carried forward
# into app.new before the swap rather than left behind for `rm -rf` to eat.
for item in keys azuread-bootstrap.json database-provider.json; do
  if [ -e "$SPECTRA_INSTALL_DIR/app/$item" ]; then
    cp -a "$SPECTRA_INSTALL_DIR/app/$item" "$SPECTRA_INSTALL_DIR/app.new/$item"
  fi
done

rm -rf "$SPECTRA_INSTALL_DIR/app"
mv "$SPECTRA_INSTALL_DIR/app.new" "$SPECTRA_INSTALL_DIR/app"

log "Building the frontend..."
# VITE_API_BASE_URL must be "" so API calls stay same-origin through the nginx
# proxy — Vite bakes it in at build time (see frontend/.env.example), and an
# unset value becomes the literal string "undefined" in the built bundle.
( cd "$SPECTRA_SRC_DIR/frontend" && npm ci --silent && VITE_API_BASE_URL="" npm run build --silent )
rm -rf "${SPECTRA_WEB_ROOT:?}"/*
cp -r "$SPECTRA_SRC_DIR/frontend/dist/." "$SPECTRA_WEB_ROOT/"
ensure_world_traversable "$SPECTRA_WEB_ROOT"
chmod -R o+rX "$SPECTRA_WEB_ROOT"

write_version

log "Restarting spectra.service..."
systemctl restart spectra

write_status "succeeded" "" "$STARTED_AT" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
log "Update complete."
