#!/usr/bin/env bash
# scripts/db-roles.sh — apply the DATABASE ROLE PASSWORDS from .env.
#
# WHY THIS EXISTS (P2 gate G1 finding, 2026-11-13): migrations 0005/0011 create
# `nabd_app` LOGIN and `nabd_admin` LOGIN BYPASSRLS but deliberately keep their
# PASSWORDS OUT of migrations (NFR-08: secrets never committed). The apply step
# lived in nobody's hands — a `down -v` rebuild left the roles password-less and
# the API host died at the first query with 28P01. This script IS the ops path
# 0005's comment points at; it is idempotent like _apply.sh.
#
# Usage:  set -a && source .env && set +a && bash scripts/db-roles.sh
set -euo pipefail

: "${NABD_APP_DB_USER:?source .env first (NABD_APP_DB_USER)}"
: "${NABD_APP_DB_PASSWORD:?source .env first (NABD_APP_DB_PASSWORD)}"
: "${NABD_ADMIN_DB_USER:?source .env first (NABD_ADMIN_DB_USER)}"
: "${NABD_ADMIN_DB_PASSWORD:?source .env first (NABD_ADMIN_DB_PASSWORD)}"

apply() {
    local role="$1" password="$2"
    # single-quoted SQL literal: double any embedded single quote, the rest is
    # safe (dev passwords are hex/alphanumeric; the doubling is the honest guard)
    local literal="'${password//\'/\'\'}'"
    docker exec -i nabd-postgres psql -U nabd -d nabd -q \
        -c "ALTER ROLE ${role} LOGIN PASSWORD ${literal};"
    echo "   ${role}: password applied (idempotent)"
}

echo "== role passwords from .env (never committed) =="
apply "$NABD_APP_DB_USER" "$NABD_APP_DB_PASSWORD"
apply "$NABD_ADMIN_DB_USER" "$NABD_ADMIN_DB_PASSWORD"
echo "DB ROLES GREEN"
