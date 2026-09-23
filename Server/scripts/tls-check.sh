#!/usr/bin/env bash
# Substitutes for the TLS verification DESIGN.md §13 says cannot be done in-process. Run after
# every Caddy change, against the real deployment — none of this can be exercised in a
# Docker-less sandbox.
#
# Usage: scripts/tls-check.sh <domain>
set -euo pipefail

domain="${1:?usage: tls-check.sh <domain>}"

echo "== Certificate chain (expect: leaf + one intermediate, both ECDSA) =="
echo | openssl s_client -connect "${domain}:443" -servername "${domain}" -tlsextdebug 2>&1 \
  | grep -E "Certificate chain|s:|i:|Signature Algorithm" || true

echo
echo "== Session resumption (expect: 'Reused, TLSv1.' on the second connection) =="
openssl s_client -connect "${domain}:443" -servername "${domain}" -reconnect -no_ign_eof </dev/null 2>&1 \
  | grep -E "Reused|New," || true

echo
echo "== MFLN / max fragment length (expect: NOT honoured — Go's crypto/tls doesn't implement it) =="
openssl s_client -connect "${domain}:443" -servername "${domain}" -maxfraglen 512 </dev/null 2>&1 \
  | grep -E "error|alert|Max Fragment Length" || echo "(no explicit rejection printed — compare negotiated record size by hand)"

echo
echo "== curl pinned to the cipher BearSSL will choose (ECDHE-ECDSA-AES128-GCM-SHA256) =="
curl -sS --tls-max 1.2 --ciphers ECDHE-ECDSA-AES128-GCM-SHA256 "https://${domain}/health/live" -o /dev/null -w "HTTP %{http_code}\n"

echo
echo "== /d/* must never be Content-Encoded (DESIGN.md §5 rule 3) =="
curl -sS -D - "https://${domain}/d/v1/enroll" -X POST -H "Content-Type: application/json" -d '{}' -o /dev/null \
  | grep -i "content-encoding" && echo "FAIL: Content-Encoding present on /d/*" && exit 1
echo "OK: no Content-Encoding on /d/*"
