# Clock

A desk clock built around an Arduino Nano, a 24×8 LED matrix and a DS3231 real-time clock.
Besides the time it shows temperature, barometric pressure and humidity, and every five
minutes it scrolls through all of them as a marquee.

This repository holds the whole project, not only the firmware: the enclosure drawings, the
PCB fabrication files, the circuit simulation and the display datasheet live here too.

## What it does

| Screen | Shows |
|---|---|
| Clock | `12:34`, colon blinking once a second |
| Temperature | `23°c` from the BME280 |
| Pressure | `745p` in mmHg, with a trend arrow |
| Humidity | `57%` (BME280 boards only) |

- **Marquee.** On every fifth minute of the hour the screens are composed into one 120-column
  strip and slid past the display, each one dwelling five seconds once fully in view.
- **Pressure trend.** A ring of twelve samples a quarter of an hour apart gives the three-hour
  window pressure trends are conventionally read over. The arrow appears after the first
  sampling interval and widens to the full window as the ring fills.
- **Night dimming.** Between 22:00 and 07:00 by default, the lit fraction of each row's slot
  drops, without changing the refresh rate. Both hours are settable over WiFi, as boundaries on
  a 0–24 line with the end excluded: `22`–`7` dims from 22:00 to 06:59, `0`–`24` dims around
  the clock, and any two equal values never dim.
- **Idle return.** A sensor screen falls back to the clock after 30 s untouched. Time
  programming does not — it waits as long as you need.

## Hardware

- **Arduino Nano** (ATmega328P, 16 MHz, **old bootloader** — see below)
- **ESP-01** (ESP8266, 1 MB flash) on the hardware UART, for NTP and the settings page
- **GNM-23881AEG** 24×8 LED matrix, datasheet in `GNM-23881AEG/`
- **DS3231** real-time clock, I²C
- **BME280** (temperature, pressure, humidity) or **BMP280** (no humidity), I²C at `0x76`
- Four shift registers: three daisy-chained for the 24 columns, one for the 8 row selects
- One button

All five display pins sit on PORTD (D3–D7), which is why the firmware clocks the registers
with single-cycle port writes rather than `shiftOut`. The button is on D2.

**The button is wired active HIGH** — an external pull-down holds the line and pressing lifts
it. `setup()` calls `pinMode(ButtonPin, INPUT_PULLUP)`, which reads like the opposite and is
the single most misleading line in the sketch.

| Action | Clock screen | Sensor screens | Time programming |
|---|---|---|---|
| Click | next screen | next screen | +1 to the digit |
| Hold 3 s | enter programming | nothing | — |
| Hold 1 s | — | — | next field, then save |
| 30 s idle | — | back to the clock | no effect |

A click also aborts a running marquee.

## Layout

| Directory | Contents |
|---|---|
| `Clock_Arduino/` | The clock firmware. One sketch, both sensor variants. |
| `Clock_ESP01/` | The WiFi firmware for the ESP-01 |
| `Clock/Arduino Nano 3/` | Proteus simulation of the circuit |
| `Gerber files/` | Fabrication files for the Control, Display and ESP-01 boards |
| `Autocad_Model/` | Enclosure drawings — facade, middle part, back cover, button |
| `GNM-23881AEG/` | LED matrix datasheet |

`Gerber_ESP01_Template_PCB.zip` is a provisioned footprint for an ESP-01, which `Clock_ESP01/`
now has firmware for — see **WiFi** below.

## WiFi

An ESP-01 on the Nano's hardware UART keeps the DS3231 honest against NTP and puts the
clock's settings in a browser. It is a peripheral: it drives nothing on the panel, and the
clock works exactly as before with the module unplugged.

On first power-up — or whenever it cannot reach the stored network — it raises an open access
point called **K-Clock**. Joining it from a phone opens the configuration page by itself, the
way a hotel portal does: the module answers every DNS query with its own address, so the plain
HTTP probe each OS fires after joining (`connectivitycheck.gstatic.com` for Android,
`captive.apple.com` for iOS) gets a redirect instead of the reply it expects, and the phone
concludes it is behind a portal. Pick the network, type the password, done. Afterwards the
page lives at `http://k-clock/` on the home network and offers the time zone, the dimming
hours and the marquee period.

The two boards talk in one-line ASCII frames ending in a XOR checksum. The checksum is not
there for noisy wires — it is there because the ESP's boot ROM dumps a burst of 74880 baud
chatter onto this same line every time it starts, and none of it may be mistaken for a
command. Frames that fail are dropped without a reply.

| Direction | Frame | Meaning |
|---|---|---|
| ESP → Nano | `>T 2026-09-21 14:03:22` | set the RTC (local time, refused during programming) |
| ESP → Nano | `>Q` | ask for the readings |
| ESP → Nano | `>G` | ask for the settings |
| ESP → Nano | `>C dimfrom 22` | change one setting |
| Nano → ESP | `<S 2026-09-21 14:03:22 234 745 57` | date, time, tenths of a degree, mmHg, percent |
| Nano → ESP | `<C 22 7 5` | dim from, dim until, marquee period |
| Nano → ESP | `<K` / `<E` / `<B` | accepted / rejected / the clock just booted |

Settings changed over the link are kept in the Nano's EEPROM, which the firmware had not used
at all before. A blank chip falls back to the compiled defaults.

**Wiring.** ESP TX goes straight to Nano D0; Nano D1 reaches ESP RX through a 1k/2k divider,
because the ESP's input is not 5 V tolerant. The ESP needs its own 3.3 V regulator off the 5 V
rail with a bulk capacitor beside it — it peaks near 300 mA in access point mode, which is far
more than the Nano's onboard 3.3 V pin can give, and starving it looks exactly like a faulty
module. `CH_PD` and `GPIO2` want 10k pull-ups, `GPIO0` a 10k pull-up and a button to ground.

**Put a jumper in the ESP TX line.** `arduino-cli upload` drives D0 from the USB bridge, and an
ESP transmitting at the same moment corrupts the flash. The boot banner is gone for the same
reason: `setup()` now sends a `<B` frame instead of printing `Initialized`.

## Building and flashing

```bash
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old --clean --upload \
  -p /dev/ttyUSB0 Clock_Arduino/Clock_Arduino.ino
```

The ESP-01 takes the esp8266 core and `WiFiManager`, and **the `eesz=1M` flash layout is part
of the FQBN** — leave it out and the image is linked for a 64 KB filesystem that nothing uses,
costing the same 64 KB of the space an update needs:

```bash
arduino-cli core install esp8266:esp8266
arduino-cli lib install "WiFiManager@2.0.17"
arduino-cli compile --fqbn esp8266:esp8266:generic:eesz=1M --clean --upload \
  -p /dev/ttyUSB0 Clock_ESP01/Clock_ESP01.ino
```

A bare ESP-01 has no auto-reset circuit, so it has to be walked into the bootloader by hand:
ground `GPIO0`, cycle the power while it is still grounded, and start the upload immediately.
Expect `Timed out waiting for packet header` on a mistimed attempt and simply repeat it. Use
`arduino-cli upload` on its own for the retry — a full `compile --upload` spends ten seconds
building before esptool starts, and the module can fall out of the window.

### Updating the ESP over WiFi

Once a module is running this firmware, the cable is only needed again if an update bricks it.
Set an OTA password on the settings page — there is no default, and the service stays down
until there is one, because a password in the repository would protect nothing. Then:

```bash
arduino-cli compile --fqbn esp8266:esp8266:generic:eesz=1M --clean --upload \
  -p 192.168.1.102 --upload-field password=<the one you set> Clock_ESP01/Clock_ESP01.ino
```

Changing the password reboots the module. That is not tidiness: `ArduinoOTA::setPassword`
returns without storing anything once `begin()` has run, so a new one can only be applied by
starting over, and calling `begin()` again would look like it worked while the old password
stayed in force.

**Mind the headroom.** Both the running image and the incoming one have to be in flash at
once, which puts the ceiling at half the chip — about 502 KB on this layout, against an image
of 393 KB. Grow the sketch past that and OTA stops working with no warning beyond a failed
upload. An image that merely fails to join the network is still recoverable without the cable:
`autoConnect` raises `K-Clock` and waits. Only one that crashes before reaching that line
needs opening the case.

Three things that will otherwise cost you an evening:

- **Use `cpu=atmega328old`.** Plain `arduino:avr:nano` uploads at 115200 and fails with
  `not in sync: resp=0x00`.
- **Compile and upload in one command.** `arduino-cli upload` on its own flashes whatever is
  cached for this sketch and FQBN, and that cache key ignores `--build-property` — so verifying
  the other sensor variant will silently leave the wrong image ready to flash.
- **`/dev/ttyUSB0` needs write access.** `sudo usermod -aG dialout $USER` and a re-login, or
  `sudo chmod a+rw /dev/ttyUSB0` until the board is unplugged.

Libraries: `Wire`, `Adafruit_Sensor`, `Adafruit_BME280` or `Adafruit_BMP280`, and Jarzebski's
`DS3231` — the one exposing `RTCDateTime`. That last one is not in the library index and is
vendored here:

```bash
arduino-cli config set library.enable_unsafe_install true
arduino-cli lib install --zip-path Clock_Arduino/libs/DS3231.zip
```

### Board variants

One sketch covers both sensors. `HAS_HUMIDITY` defaults to 1 (BME280) and is wrapped in
`#ifndef`, so a BMP280 build needs no edit:

```bash
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old \
  --build-property "compiler.cpp.extra_flags=-DHAS_HUMIDITY=0" Clock_Arduino/Clock_Arduino.ino
```

With `HAS_HUMIDITY 0` the humidity screen is dropped rather than shown as a literal `00%`, and
the marquee runs four pages instead of five.

|  | Flash | SRAM |
|---|---|---|
| BME280 | 20236 B (65%) | 900 B (43%) |
| BMP280 | 19500 B (63%) | 894 B (43%) |

The ESP-01 build is 339 KB of its 1 MB, and needs no filesystem: the SDK keeps the WiFi
credentials in its own flash area and the only other stored setting, the time zone, fits in
the emulated EEPROM.

## Notes

There are no automated tests — verification is on hardware, or in the Proteus simulation. The
display packing is pure integer arithmetic, so it can also be replayed on a host, which is how
the marquee window and the trend arrow placement were checked before flashing.

`CLAUDE.md` documents the internals: the frame buffer layout, the glyph table, the state
machine and the traps worth knowing about.

## License

MIT — see `LICENSE`.
