#!/bin/sh
# One-shot service of docker-compose.yml (dev only): creates the dev ticket secret and access-token secret once, 32
# random bytes each in base64, in the dev-secrets volume. Existing files are kept, so tickets stay valid across restarts;
# `docker compose down -v` deletes them. A non-empty MMORPG_TICKET_SECRET / MMORPG_AUTH_TOKEN_SECRET in .env wins
# (docker/dev-entrypoint.sh). Nothing is printed.
set -eu
dir=/run/dev-secrets
for name in ticket_secret auth_token_secret; do
  if [ ! -s "$dir/$name" ]; then
    head -c 32 /dev/urandom | base64 > "$dir/$name.tmp"
    mv "$dir/$name.tmp" "$dir/$name"
    echo "dev-secrets: created $name"
  fi
done
