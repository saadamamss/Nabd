#!/usr/bin/env bash
# db/migrations/_apply.sh — applies NNNN__*.sql in order, using platform.migrations_applied
set -euo pipefail
cd "$(dirname "$0")"

# applier ensures its own bookkeeping home (idempotent, safe to rerun every time)
docker exec nabd-postgres psql -U nabd -d nabd -c \
  "CREATE SCHEMA IF NOT EXISTS platform;
   CREATE TABLE IF NOT EXISTS platform.migrations_applied (
     name       text PRIMARY KEY,
     applied_at timestamptz NOT NULL DEFAULT now());"

for f in [0-9]*.sql; do
  applied=$(docker exec nabd-postgres psql -U nabd -d nabd -tAc \
    "SELECT count(*) FROM platform.migrations_applied WHERE name='$f'")
  if [ "$applied" = "1" ]; then
    echo "skip  $f"
    continue
  fi

  echo "apply $f"
  { cat "$f";
    echo "INSERT INTO platform.migrations_applied(name) VALUES ('$f');";
  } | docker exec -i nabd-postgres psql -U nabd -d nabd -1 -v ON_ERROR_STOP=1 -f -

  echo "done  $f"
done

echo "ALL MIGRATIONS GREEN"
