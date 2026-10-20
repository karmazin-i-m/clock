#!/usr/bin/env bash
# Builds the ESP-01 firmware with the options it cannot do without, and refuses
# an image that a later over-the-air update could not replace.
#
#   Clock_ESP01/build.sh                 build into build/esp01 and check the size
#   Clock_ESP01/build.sh upload [host]   the same, then OTA it (host defaults to
#                                        k-clock.local; password from $OTA_PASSWORD
#                                        or asked for)
#
# Why the ceiling is 512000 and not "half the chip": an update is written into
# the free space above the running image, and the Updater wants
#   roundup4K(running) + roundup4K(incoming) <= 0xFB000
# where 0xFB000 is where the 1M layout's EEPROM begins. That is 251 sectors, so
# two images of the same size can each have 125 of them - 512000 bytes. Past
# that the build still succeeds and the upload fails on the module with nothing
# but "space" to explain it.
#
# The options, each worth its bytes against that ceiling (measured on 3.1.2):
#   eesz=1M          no filesystem reservation; this firmware never mounts one
#   xtal=160         halves the TLS handshake, 8 s -> 4 s, all of it ECDSA maths
#   build.float=     drops the -u _printf_float/_scanf_float the core forces;
#                    nothing here formats a float through printf (String(float)
#                    goes through dtostrf, which does not need it). -17 KB
#   -DWM_NODEBUG     WiFiManager's log strings; its output is already switched
#                    off with setDebugOutput(false). -11 KB
# In reserve if the cloud client outgrows the margin: ip=lm2n, another -10 KB,
# at the cost of two of the three NTP servers and the hostname in DHCP DISCOVER.
set -euo pipefail

FQBN="esp8266:esp8266:generic:eesz=1M,xtal=160"
MAX_IMAGE=512000

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sketch="$repo/Clock_ESP01/Clock_ESP01.ino"
out="$repo/build/esp01"
export PATH="$HOME/.local/bin:$PATH"

arduino-cli compile --fqbn "$FQBN" \
  --build-property "build.float=" \
  --build-property "compiler.cpp.extra_flags=-DWM_NODEBUG" \
  --build-path "$out/cache" --output-dir "$out" "$sketch"

size=$(stat -c %s "$out/Clock_ESP01.ino.bin")
if (( size > MAX_IMAGE )); then
  echo "FAIL: image is $size bytes, $((size - MAX_IMAGE)) over the $MAX_IMAGE OTA ceiling" >&2
  exit 1
fi
echo "OK: image is $size bytes, $((MAX_IMAGE - size)) under the $MAX_IMAGE OTA ceiling"

[[ "${1:-}" == "upload" ]] || exit 0

host="${2:-k-clock.local}"
# arduino-cli matches the port against what its mDNS discovery has seen, and
# takes an IP address more reliably than a .local name.
if [[ "$host" == *.local ]]; then
  host=$(getent hosts "$host" | awk '{print $1; exit}')
  [[ -n "$host" ]] || { echo "FAIL: ${2:-k-clock.local} does not resolve" >&2; exit 1; }
fi

password="${OTA_PASSWORD:-}"
if [[ -z "$password" ]]; then
  read -rsp "OTA password for $host: " password
  echo
fi

# Straight after a restart the module answers HTTP before its _arduino._tcp
# advertisement has reached arduino-cli, which then reports "port not found"
# without trying. A few seconds always cures it.
for attempt in 1 2 3; do
  if arduino-cli upload --fqbn "$FQBN" -p "$host" --protocol network \
       -F "password=$password" --input-dir "$out" "$sketch"; then
    exit 0
  fi
  echo "upload attempt $attempt failed, retrying in 10 s" >&2
  sleep 10
done
exit 1
