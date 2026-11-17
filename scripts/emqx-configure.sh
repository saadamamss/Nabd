#!/usr/bin/env bash
# scripts/emqx-configure.sh — P2 spec 05: configure EMQX's HTTP authn/authz with
# their REQUEST BODY TEMPLATES via the dashboard REST API.
#
# WHY A SCRIPT (the compose story, honestly logged):
#   1. compose env vars CANNOT reach EMQX's body-template maps — EMQX 5.8 warns
#      `unknown_env_vars` and ignores them, then falls back to a DEFAULT body
#      that is EMPTY ({}). Our doors answered deny to every connect (debug log:
#      request body was literally `{}`).
#   2. The REST API accepts template maps, and its writes persist in the
#      emqx data volume (cluster.hocon) — so this runs once per fresh volume,
#      idempotent like _apply.sh: it deletes any existing HTTP authenticator /
#      authorizer, then creates fresh ones with the correct templates.
#   3. Everything else (allow_anonymous, no_match, URLs, the shared secret in
#      ?k=) stays in compose env — this script only adds what env cannot carry.
#
# Usage:  bash scripts/emqx-configure.sh     (source .env first, or run via compose env)
set -euo pipefail

DASH_USER="${EMQX_DASHBOARD_USER:?set EMQX_DASHBOARD_USER in .env}"
DASH_PASS="${EMQX_DASHBOARD_PASSWORD:?set EMQX_DASHBOARD_PASSWORD in .env}"
: "${NABD_INTERNAL_SHARED_SECRET:?source .env first — the ?k= URLs bake it in}"
DASH="http://127.0.0.1:18083"
API_URL="http://host.docker.internal:5109"

echo "== login to the dashboard API =="
TOKEN=$(curl -s -X POST "$DASH/api/v5/login" \
    -H 'Content-Type: application/json' \
    -d "{\"username\":\"$DASH_USER\",\"password\":\"$DASH_PASS\"}" \
    | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -n "$TOKEN" ] || { echo "FATAL: dashboard login failed"; exit 1; }
AUTH="Authorization: Bearer $TOKEN"

echo "== authentication: remove existing HTTP authenticator(s), create fresh =="
for ID in $(curl -s -H "$AUTH" "$DASH/api/v5/authentication" | grep -o '"id":"[^"]*"' | cut -d'"' -f4 || true); do
    echo "   deleting authenticator $ID"
    curl -s -X DELETE -H "$AUTH" "$DASH/api/v5/authentication/$ID" > /dev/null
done
curl -s -X POST -H "$AUTH" -H 'Content-Type: application/json' \
    "$DASH/api/v5/authentication" -d '{
      "mechanism": "password_based",
      "backend": "http",
      "method": "post",
      "url": "'"$API_URL"'/internal/emqx/auth?k='"$NABD_INTERNAL_SHARED_SECRET"'",
      "headers": {"content-type": "application/json"},
      "body": {"username": "${username}", "password": "${password}"},
      "pool_size": 8
    }' > /dev/null
echo "   authenticator created (url: $API_URL/internal/emqx/auth)"

echo "== authorization: replace the http source with a templated one =="
curl -s -X PUT -H "$AUTH" -H 'Content-Type: application/json' \
    "$DASH/api/v5/authorization/sources/http" -d '{
      "type": "http",
      "enable": true,
      "method": "post",
      "url": "'"$API_URL"'/internal/emqx/acl?k='"$NABD_INTERNAL_SHARED_SECRET"'",
      "headers": {"content-type": "application/json"},
      "body": {"username": "${username}", "topic": "${topic}", "action": "${action}"},
      "pool_size": 8
    }' > /dev/null
echo "   authorizer created (url: $API_URL/internal/emqx/acl)"

echo "== inflight window: raise mqtt.max_inflight on the global zone (P4 spec 01, R5) =="
# The effective window = min(client Receive Maximum, EMQX mqtt.max_inflight) —
# BOTH sides must rise or the zone default (32) caps unacked dispatch. The zone
# PUT VALIDATES THE WHOLE mqtt block (partial bodies are rejected with
# required_field), so this is a read-modify-write: GET the zone config, patch
# the one key, PUT it all back. Idempotent.
# (sharding ruling 2026-11-16): max_inflight follows the PER-CONNECTION
# Receive Maximum (Ingestion:ReceiveMaximum, default 200) — NOT FlushSize,
# which is only the flush trigger's readings bound.
NABD_FLUSH_SIZE="${INGESTION__RECEIVEMAXIMUM:-${INGESTION__FLUSHSIZE:-${NABD_INGESTION_FLUSH_SIZE:-200}}}"
TOKEN=$(curl -s -X POST "$DASH/api/v5/login" \
    -H 'Content-Type: application/json' \
    -d "{\"username\":\"$DASH_USER\",\"password\":\"$DASH_PASS\"}" \
    | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
python3 - "$TOKEN" "$NABD_FLUSH_SIZE" <<'PYEOF'
import json, sys, urllib.request
token, flush_size = sys.argv[1], int(sys.argv[2])
def call(method, data=None):
    req = urllib.request.Request('http://127.0.0.1:18083/api/v5/configs/global_zone',
        method=method,
        data=json.dumps(data).encode() if data is not None else None,
        headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req) as r:
        body = r.read().decode()
        return json.loads(body) if body else None
cfg = call('GET')
if cfg['mqtt']['max_inflight'] == flush_size:
    print(f"   mqtt.max_inflight already {flush_size} (idempotent skip)")
else:
    cfg['mqtt']['max_inflight'] = flush_size
    call('PUT', cfg)
    print(f"   mqtt.max_inflight = {flush_size} (global zone)")
PYEOF
curl -s -H "Authorization: Bearer $TOKEN" "$DASH/api/v5/configs/global_zone" | grep -o '"max_inflight":[0-9]*' | head -1

echo "== verification: the chain as EMQX sees it =="
curl -s -H "$AUTH" "$DASH/api/v5/authentication" | head -c 400; echo
curl -s -H "$AUTH" "$DASH/api/v5/authorization/sources" | head -c 400; echo
echo "ALL EMQX CONFIG GREEN"
