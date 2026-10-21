#!/usr/bin/env bash
# Builds the API and the migrations bundle here, then installs them on the API host over SSH.
#
#   Server/deploy/deploy.sh [host]     host defaults to $KCLOCK_HOST, then 192.168.23.51
#
# Both are self-contained linux-x64 builds, so the host needs neither the SDK nor a .NET
# runtime - only libicu, which .NET will not start without. The one-time host setup is in
# Server/README.md, "Deploying to a plain LXC".
#
# Order matters: the migrations run before the new API files land, so a migration that
# fails leaves the old API running, on the old files, against the old schema.
set -euo pipefail

host="root@${1:-${KCLOCK_HOST:-192.168.23.51}}"
server="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$server/build"
cd "$server"

dotnet tool restore >/dev/null
dotnet restore src/KClock.Data/KClock.Data.csproj -r linux-x64 >/dev/null
dotnet ef migrations bundle \
  --project src/KClock.Data/KClock.Data.csproj \
  --startup-project src/KClock.Data/KClock.Data.csproj \
  --context ClockDbContext \
  --self-contained -r linux-x64 --force \
  -o "$out/migrate"
dotnet publish src/KClock.Api/KClock.Api.csproj -c Release -r linux-x64 --self-contained -o "$out/api"

# rsync writes to a temporary file and renames it, so neither a dropped connection nor a
# running binary can leave a half-written file behind.
rsync -a "$out/migrate" "$host:/opt/kclock/migrate"
ssh "$host" '/opt/kclock/migrate --connection "$(cat /etc/kclock/migrator.conn)"'

rsync -a --delete "$out/api/" "$host:/opt/kclock/api/"
rsync -a deploy/kclock-api.service "$host:/etc/systemd/system/kclock-api.service"
ssh "$host" 'systemctl daemon-reload && systemctl enable kclock-api >/dev/null 2>&1 && systemctl restart kclock-api'

# /health/ready answers only once the API is up and can reach the database.
for _ in $(seq 20); do
  if ssh "$host" 'curl -fsS http://127.0.0.1:8080/health/ready' 2>/dev/null; then
    echo
    echo "OK: deployed to $host"
    exit 0
  fi
  sleep 1
done
echo "FAIL: /health/ready did not answer within 20 s; see: ssh $host journalctl -u kclock-api -n 50" >&2
exit 1
