#!/usr/bin/env bash
# Entry point of the dev service images (docker/Dockerfile, docker-compose.yml). Dev only.
# Usage: dev-entrypoint <log-name|-> <dll> [args...]
#   1. Secrets: MMORPG_TICKET_SECRET and MMORPG_AUTH_TOKEN_SECRET come from the environment (a git-ignored .env) or,
#      when empty, from the dev-secrets volume (docker/init-dev-secrets.sh). They are never printed.
#   2. Databases and Redis: MMORPG_GLOBAL_DB, MMORPG_REALM_DB and MMORPG_REDIS_PREFIX are built from POSTGRES_USER,
#      POSTGRES_PASSWORD, MMORPG_REALM_ID and MMORPG_DB_SUFFIX unless set: the databases DbMigrate --dev uses
#      (mmorpg_global, mmorpg_realm_1, plus _<suffix>), and Redis keys under mmorpg:<suffix>: with a suffix.
#   3. Logs: with a log name, stdout and stderr also go to /logs/<name>.log (the admin panel reads them); the previous
#      run's file is kept as <name>.prev.log.
set -euo pipefail

log_name="$1"
shift

secrets=/run/dev-secrets
read_secret() {
  local variable="$1" file="$secrets/$2"
  if [ -z "${!variable:-}" ] && [ -s "$file" ]; then
    export "$variable=$(cat "$file")"
  fi
}
read_secret MMORPG_TICKET_SECRET ticket_secret
read_secret MMORPG_AUTH_TOKEN_SECRET auth_token_secret

suffix="${MMORPG_DB_SUFFIX:-}"
tail=""
if [ -n "$suffix" ]; then
  if ! [[ "$suffix" =~ ^[a-z0-9_]{1,32}$ ]]; then
    echo "MMORPG_DB_SUFFIX must be 1 to 32 lowercase letters, digits or '_'." >&2
    exit 2
  fi
  tail="_$suffix"
  export MMORPG_REDIS_PREFIX="${MMORPG_REDIS_PREFIX:-mmorpg:$suffix:}"
fi
# No GSS encryption: the dev server has no Kerberos, and the image has no libgssapi (Npgsql would print an error line).
server="Host=${MMORPG_PG_HOST:-postgres};Port=5432;Username=${POSTGRES_USER:-mmorpg};Password=${POSTGRES_PASSWORD:-mmorpg_dev};Gss Encryption Mode=Disable"
export MMORPG_GLOBAL_DB="${MMORPG_GLOBAL_DB:-$server;Database=mmorpg_global$tail}"
export MMORPG_REALM_DB="${MMORPG_REALM_DB:-$server;Database=mmorpg_realm_${MMORPG_REALM_ID:-1}$tail}"

if [ "$log_name" != "-" ]; then
  log="/logs/$log_name.log"
  if [ -f "$log" ]; then
    mv -f "$log" "/logs/$log_name.prev.log"
  fi
  exec > >(tee -a "$log") 2>&1
fi

exec dotnet "$@"
