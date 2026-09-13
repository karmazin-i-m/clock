# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Hardware project: a desk clock built on an Arduino Nano (ATmega328P @ 16 MHz) that drives a
24x8 LED matrix through chained shift registers, reads time from a DS3231 RTC and
environment data from a Bosch BME280/BMP280 over I2C, and is controlled by a single button.

The repo is not only firmware — it also holds the mechanical and electrical design:

| Directory | Contents |
|---|---|
| `Clock_Arduino/` | Firmware as Arduino sketches. **This is the canonical source.** |
| `Clock/Arduino Nano 3/` | Proteus (`.pdsprj`) simulation of the circuit |
| `Gerber files/` | Fabrication zips for three boards: Control, Display, ESP01 template |
| `Autocad_Model/` | AutoCAD drawings of the enclosure and the GNM-23881 display module |
| `GNM-23881AEG/` | Datasheet for the LED matrix module |

The outer directory is **not** a git repo; `Clock_Arduino/` is a nested repo pointing at
Azure DevOps (`dev.azure.com/karmazin-i-m/Clock`). Changes outside `Clock_Arduino/` are untracked.

## Building and flashing

`arduino-cli` is installed at `~/.local/bin/arduino-cli` (not on the default PATH), with the
`arduino:avr` core and the libraries below already pulled in.

**This board needs the old-bootloader FQBN.** Plain `arduino:avr:nano` uploads at 115200 and
fails with `not in sync: resp=0x00`; `cpu=atmega328old` drops it to 57600 and works:

```bash
export PATH="$HOME/.local/bin:$PATH"
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old Clock_Arduino/Clock_Arduino.ino
arduino-cli upload  --fqbn arduino:avr:nano:cpu=atmega328old -p /dev/ttyUSB0 \
  Clock_Arduino/Clock_Arduino.ino
```

**Never `upload` a build you did not just produce.** `arduino-cli upload` does not compile —
it flashes whatever `.hex` is sitting in the cache directory for this sketch, and that path is
keyed on sketch and FQBN only, *not* on `--build-property`. Verifying the `HAS_HUMIDITY=0`
variant therefore overwrites the cached image, and a following `upload` silently flashes the
wrong firmware. Always use one atomic command:

```bash
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old --clean --upload \
  -p /dev/ttyUSB0 Clock_Arduino/Clock_Arduino.ino
```

Compile variants with `--output-dir` so they never share a cache. To check what is actually on
the chip, read it back and compare — `Initialized` on the serial line proves only that *some*
firmware boots:

```bash
avrdude -C <conf> -c arduino -p m328p -P /dev/ttyUSB0 -b 57600 -U flash:r:chip.bin:r
```

**Serial port permissions.** The user is not in the `dialout` group, so `/dev/ttyUSB0` is not
writable by default and both upload and monitor fail with `Permission denied`. `sudo chmod
a+rw /dev/ttyUSB0` fixes it until the board is unplugged; `sudo usermod -aG dialout $USER`
plus a re-login fixes it for good. Check `ls -l /dev/ttyUSB0` before blaming the firmware.

**Reading the serial banner.** The sketch prints `Initialized` once, in `setup()`. Neither
`arduino-cli monitor` nor `cat /dev/ttyUSB0` reliably produces a DTR edge, so the board never
resets and you see nothing — this looks exactly like a dead board but is not. Pulse DTR/RTS
low then high over the open fd (`TIOCMSET`) and read for a few seconds to get the banner.

Libraries: `Wire`, `Adafruit_Sensor`, `Adafruit_BME280`, `Adafruit_BMP280`, and Jarzebski's
`DS3231` — the one exposing `RTCDateTime`, which is not in the library index and is installed
from the vendored `Clock_Arduino/libs/DS3231.zip` via
`arduino-cli lib install --zip-path` (needs `library.enable_unsafe_install true`).

There are no tests and no lint config; verification is done on hardware or in Proteus. The
display packing is pure integer arithmetic, so it can also be replayed on the host — see the
approach used for the marquee window in `marqueeRender()`.

## One sketch, two board variants

`Clock_Arduino/Clock_Arduino.ino` is the only copy of the firmware. The BMP280 board — which
measures temperature and pressure but has no humidity sensor — is selected by one switch at
the top of the file:

```c
#define HAS_HUMIDITY 1   // 0 for a BMP280 board
```

It is wrapped in `#ifndef`, so a build can override it without editing the file:

```bash
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328old \
  --build-property "compiler.cpp.extra_flags=-DHAS_HUMIDITY=0" Clock_Arduino/Clock_Arduino.ino
```

With `HAS_HUMIDITY 0` the sensor class becomes `Adafruit_BMP280`, and the humidity screen is
dropped rather than rendered as a literal `00%`: `MARQUEE_PAGE_COUNT` falls to 4 and `case
humidity` disappears, so the button cycle runs clock → temperature → pressure → clock through
the existing `default` reset. Verify both configurations compile after touching display code;
as of the consolidation they are 16286 and 15540 bytes of flash.

One piece of history worth keeping: the deleted Atmel Studio copy of this firmware used a
**different pin map** — `LatchPin`/`ClockPin` 3/4 rather than 4/3, and
`CatodeDataPin`/`CatodeClockPin` 6/7 rather than 7/6. If a board ever turns up whose display
is scrambled, that mapping is the thing to try.

The `.ino` file is CRLF; keep it that way when editing.

## Firmware architecture

**Pins (canonical `.ino`)**: button on D2 (INT0, `INPUT_PULLUP`), row/anode shift register on
D5 data + D3 clock, column/cathode shift register chain on D7 data + D6 clock, shared latch on D4.

**Rendering.** `out[8][3]` is the frame buffer: 8 rows × 3 bytes = 24 columns. `visual()` runs
once per `loop()` and multiplexes the panel — for each row it clocks three cathode bytes out
LSB-first, then one anode byte (`catode[]`, a rotating one-hot row select) MSB-first, then
pulses the latch.

All five display pins are on PORTD, so the bit banging goes straight to the port (`sbi`/`cbi`)
rather than through `shiftOut`/`digitalWrite`. Read-modify-write is confined to those five
bits — never assign `PORTD` wholesale, or you will drop the serial pins and the button
pull-up on PD2.

Each row gets an identical `ROW_PERIOD_US` slot and is **blanked** at the end of it
(`latchRow(0,0,0,0)`). That is load-bearing: previously a row stayed lit until the next one
was latched, so the last row of a frame also burned through everything `loop()` did
afterwards — the bottom line was brighter, by an amount that changed with the screen being
shown. Brightness is now `rowOnMicros / ROW_PERIOD_US`, which is also the whole dimming
mechanism: `rowOnMicros` drops to `ROW_ON_DIM_US` between `DIM_FROM_HOUR` and
`DIM_UNTIL_HOUR`. Dimming does not change the frame rate.

**Font.** `numeric[][8]` holds 8 rows per glyph: digits 0–9, then `colon`(10), `degree`(11),
`celsius`(12), `pressureSymbol`(13), `nullNumber`(14, blank), `percent`(15), `trendUp`(16),
`trendDown`(17), `trendSteady`(18). Glyphs are 4 px wide (`0x0F` mask) except `percent`, which
is 7 px, and the trend arrows, which are 3.

The table and `catode[]` live in `PROGMEM`, so read them through `glyph(index, row)` /
`pgm_read_byte`, never by plain subscript — a plain subscript compiles and returns garbage.

**Layout.** Each `*ArrayFiling()` function composes one screen by shifting glyphs into the
24-bit row and splitting them across byte boundaries (`<<6` / `>>2` pairs). The bit offsets are
hardcoded per screen, so moving a glyph means recomputing the shifts in that function.

**State machine.** The `state` global selects both the screen and the settings step:

- Display modes: `times`(0), `temperature`(1), `pressure`(2), `humidity`(3), `altitude`(4, unused)
- `marquee`(5) is entered only from the timer at the top of `loop()`, when the RTC minute is a
  multiple of `MARQUEE_PERIOD_MINUTES` and differs from `lastMarqueeMinute`. `startMarquee()`
  composes all five screens into `marqueeStrip[8][15]` (5 pages x 3 bytes = 120 columns, page 0
  at the high/left end), and `marqueeRender()` copies a 24-column window out of it, advancing
  one column every `MARQUEE_STEP_MS`. A step that lands on a page boundary
  (`marqueeStep % MARQUEE_PAGE_COLUMNS == 0`) waits `MARQUEE_HOLD_MS` instead, so each screen
  slides in and then dwells. It hands back to `times` on reaching the final page rather than
  dwelling on a clock captured ~20 s earlier. Its id stays below `settings` so the existing
  button logic still aborts it (short press -> 6 -> `default` -> 0).
- Settings modes: `settings`(100) latches the current time into the digit variables, then
  `minuteMinorSettings`(101) → `minuteMajorSettings`(102) → `hourMinorSettings`(103) →
  `hourMajorSettings`(104) each blink the field being edited on a 1 s cycle (visible 0–500 ms),
  and `endSettings`(105) writes the digits back with `clk.setDateTime(...)` and falls through
  to `default`, which resets `state` to 0.

**Cached readings.** Nothing reads a sensor or the RTC inside a screen case. `readSensors()`
refreshes `sensorTemperature` / `sensorPressure` / `sensorHumidity` every `SENSOR_POLL_MS`, and
the RTC is polled every `CLOCK_POLL_MS` at the top of `loop()`. Reading the BME280 on every
pass was what made the sensor screens visibly dimmer than the clock. Pressure is converted
from Pa to mmHg by `/133.0F`. Digits are carried as separate major/minor ints and recombined
by `concatenateInt()`.

**Pressure trend.** `pressureHistory[]` is a ring of `PRESSURE_HISTORY` samples taken every
`PRESSURE_SAMPLE_MS`, i.e. a three hour window. `pressureTrendGlyph()` compares the current
reading against the oldest sample held, so an arrow appears after the first sampling interval
and widens to the full window as the ring fills; below two samples it returns `nullNumber` and
nothing is drawn. The arrow occupies the four leftmost columns of the pressure screen, which
that layout leaves empty — `out[i][2]` carries the hundreds digit in its low nibble and the
arrow in its high nibble.

**Blinking colon.** `timeArrayFilling()` takes a `separator` argument defaulting to `colon`.
Only `case times` passes something else, keyed off `dt.second & 1`. The settings blink and the
marquee's captured clock pages deliberately keep a steady colon.

**Input.** One button on `ButtonPin` (D2), polled from `updateButton()` at the top of `loop()`.

**The button is active HIGH** (`BUTTON_PRESSED_LEVEL`). This is the single most misleading thing
in the sketch, because `setup()` calls `pinMode(ButtonPin, INPUT_PULLUP)` — which reads like
active LOW and is not. Measured with a probe sketch: D2 idles LOW in 100% of samples, with zero
edges, while nothing is pressed and the internal pull-up is on. An external pull-down holds the
line and pressing lifts it. Read it as active LOW and the firmware believes the button is held
from the moment it boots, so it drops into time programming three seconds after power-up
without anybody touching it. There is no interrupt: `loop()` runs on a fixed ~4 ms
cadence, which is ample for a button and keeps press timing out of an ISR.

- **Click** steps through the screens (`DISPLAY_MODE_COUNT`, which follows `HAS_HUMIDITY`) and
  is also how a running marquee is aborted.
- **Hold** does something only on the clock screen, where `BUTTON_HOLD_MS` (3 s) opens time
  programming, and inside programming, where `BUTTON_FIELD_HOLD_MS` (1 s) moves to the next
  field. On every other screen a hold does nothing. The split is deliberate and confirmed on
  hardware: entering takes the full three seconds so it cannot happen by accident, while a
  second is enough once inside and keeps setting a time bearable.
- A hold that lands on a screen which ignores it still sets `buttonHoldFired`, so holding never
  quietly turns into a page step on release.
- **Idle return.** A *sensor* screen falls back to the clock after `IDLE_RETURN_MS` (30 s)
  without a debounced button transition. Programming is exempt (`state < settings` in the
  guard): it is a deliberate mode the user is standing in front of, and dropping out mid-edit
  would be worse than waiting. The marquee is exempt too — it runs on its own clock and already
  ends on the clock screen, so timing it out would only truncate it.

The state machine was verified on the host against a synthetic bouncing signal: clicks, a hold
on the clock screen, a hold on a screen that ignores it, a full `07:25` programming walk,
aborting the marquee, and the idle return — including that a click restarts its countdown and
that programming survives a minute of idling untouched.

The predecessor ran from an INT0 `CHANGE` interrupt. Its timing was actually right — the
rising edge (press) stored `millis()`, the falling edge (release) turned it into the press
duration, and both branches fired on that falling edge — but it had no debounce, so the chatter
on release produced several falling edges a few milliseconds apart, each taken for another
short press. That is why the screens jumped in bursts.

## Known rough edges in the current code

Do not "fix" these silently as drive-by cleanups — they change hardware behavior:

- `bme.begin(0x76)` — the address is hardcoded and the returned `status` is discarded.
- `clk.setDateTime(__DATE__, __TIME__)` in `setup()` is commented out on purpose; uncommenting
  it reseeds the RTC from build time on every boot.
- `RENDER_LINE_COUNT` is defined but unused — the loops use a literal `8`.
- `hourMajorSettings` wraps the tens of hours at 3 and `hourMinorSettings` at 10, so the
  programming screens will happily accept 25:xx or 29:xx and write it to the RTC.
