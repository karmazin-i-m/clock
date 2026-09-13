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
- **Night dimming.** Between 22:00 and 07:00 the lit fraction of each row's slot drops, without
  changing the refresh rate.
- **Idle return.** A sensor screen falls back to the clock after 30 s untouched. Time
  programming does not — it waits as long as you need.

## Hardware

- **Arduino Nano** (ATmega328P, 16 MHz, **old bootloader** — see below)
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
| `Clock_Arduino/` | The firmware. One sketch, both sensor variants. |
| `Clock/Arduino Nano 3/` | Proteus simulation of the circuit |
| `Gerber files/` | Fabrication files for the Control, Display and ESP-01 boards |
| `Autocad_Model/` | Enclosure drawings — facade, middle part, back cover, button |
| `GNM-23881AEG/` | LED matrix datasheet |

`Gerber_ESP01_Template_PCB.zip` is a provisioned footprint for an ESP-01. Nothing in the
firmware uses it yet; NTP time sync is the obvious thing to put there, bearing in mind that the
Nano's only hardware UART is taken by the serial console.

## Building and flashing

```bash
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old --clean --upload \
  -p /dev/ttyUSB0 Clock_Arduino/Clock_Arduino.ino
```

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
| BME280 | 16402 B (53%) | 743 B (36%) |
| BMP280 | 15786 B (51%) | 735 B (35%) |

## Notes

There are no automated tests — verification is on hardware, or in the Proteus simulation. The
display packing is pure integer arithmetic, so it can also be replayed on a host, which is how
the marquee window and the trend arrow placement were checked before flashing.

`CLAUDE.md` documents the internals: the frame buffer layout, the glyph table, the state
machine and the traps worth knowing about.
