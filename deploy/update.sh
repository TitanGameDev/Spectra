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
dotnet publish "$SPECTRA_SRC_DIR/backend/Spectra.Api.csproj" -c Release -o "$SPECTRA_INSTALL_DIR/app" --nologo
chown -R "$SPECTRA_SYSTEM_USER:$SPECTRA_SYSTEM_USER" "$SPECTRA_INSTALL_DIR/app"

log "Building the frontend..."
( cd "$SPECTRA_SRC_DIR/frontend" && npm ci --silent && npm run build --silent )
rm -rf "${SPECTRA_WEB_ROOT:?}"/*
cp -r "$SPECTRA_SRC_DIR/frontend/dist/." "$SPECTRA_WEB_ROOT/"
# See the matching comment in install.sh's build_frontend — nginx needs this
# world-readable/traversable regardless of root's umask, or it 403s.
chmod 755 "$(dirname "$SPECTRA_WEB_ROOT")" "$SPECTRA_WEB_ROOT"
chmod -R o+rX "$SPECTRA_WEB_ROOT"

write_version

log "Restarting spectra.service..."
systemctl restart spectra

write_status "succeeded" "" "$STARTED_AT" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
log "Update complete."
