#!/usr/bin/env bash
# Build, run unit tests, start the API, run a curl smoke suite, then stop the API.
# Usage: ./verify.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/morrow-notifications/morrow-notifications.csproj"
BASE="http://localhost:5252"
APP_PID=""

cleanup() {
  if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

pass=0
fail=0
check() {
  local label="$1" actual="$2" expected="$3"
  if [[ "$actual" == "$expected" ]]; then
    echo "  PASS  $label"
    pass=$((pass + 1))
  else
    echo "  FAIL  $label (expected=$expected actual=$actual)"
    fail=$((fail + 1))
  fi
}

# req METHOD PATH [JSON_BODY] -> sets $HTTP_STATUS and $HTTP_BODY
req() {
  local method="$1" path="$2" body="${3:-}"
  local tmp
  tmp=$(mktemp)
  if [[ -n "$body" ]]; then
    HTTP_STATUS=$(curl -sS -o "$tmp" -w "%{http_code}" -X "$method" "$BASE$path" \
      -H "Content-Type: application/json" -d "$body")
  else
    HTTP_STATUS=$(curl -sS -o "$tmp" -w "%{http_code}" -X "$method" "$BASE$path")
  fi
  HTTP_BODY=$(cat "$tmp")
  rm -f "$tmp"
}

echo
echo "=== morrow-notifications verify ==="

echo
echo "[1/4] Build..."
dotnet build "$PROJECT" --verbosity quiet

echo
echo "[2/4] Unit tests..."
dotnet test "$ROOT/MN.Tests/MN.Tests.csproj" --verbosity minimal --no-build

echo
echo "[3/4] Start API..."
dotnet run --project "$PROJECT" --no-build --urls http://localhost:5252 &
APP_PID=$!

ready=false
for _ in $(seq 1 30); do
  if curl -sf "$BASE/api/tenants" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  echo "API did not become ready at $BASE within 30s."
  exit 1
fi
echo "     API ready at $BASE"

echo
echo "[4/4] API smoke tests (curl)..."
name="Verify-$(date +%H%M%S)"

req POST "/api/tenants" "{\"name\":\"$name\",\"rateLimitPerMinute\":100}"
check "POST /api/tenants -> 201" "$HTTP_STATUS" "201"
tenant_id=$(echo "$HTTP_BODY" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
if [[ -z "$tenant_id" ]]; then
  echo "FATAL: could not parse tenant id from: $HTTP_BODY"
  exit 1
fi

req POST "/api/tenants/$tenant_id/rules" '{"eventType":"order.created","channelType":"slack"}'
check "POST rule -> 201" "$HTTP_STATUS" "201"

req POST "/api/events" "{\"tenantId\":\"$tenant_id\",\"eventType\":\"order.created\",\"payload\":\"{\\\"orderId\\\":\\\"1\\\"}\"}"
check "POST event -> 202" "$HTTP_STATUS" "202"

req POST "/api/events" "{\"tenantId\":\"$tenant_id\",\"eventType\":\"unrouted.event\",\"payload\":\"{}\"}"
check "POST unrouted event -> 202" "$HTTP_STATUS" "202"

sleep 3

req GET "/api/dead-letters/tenant/$tenant_id"
check "GET dead letters -> 200" "$HTTP_STATUS" "200"
check "  tenant has dead letter" "$(echo "$HTTP_BODY" | grep -c unrouted.event || true)" "1"

req DELETE "/api/tenants/$tenant_id"
check "DELETE tenant -> 204" "$HTTP_STATUS" "204"

echo
if [[ "$fail" -eq 0 ]]; then
  echo "=== ALL CHECKS PASSED ($pass checks) ==="
else
  echo "=== VERIFY FAILED ($pass passed, $fail failed) ==="
  exit 1
fi
