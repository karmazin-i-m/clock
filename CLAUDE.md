# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Hardware project: a desk clock built on an Arduino Nano (ATmega328P @ 16 MHz) that drives a
24x8 LED matrix through chained shift registers, reads time from a DS3231 RTC and
environment data from a Bosch BME280/BMP280 over I2C, and is controlled by a single button.

The repo is not only firmware — it also holds the mechanical and electrical design:

| Directory | Contents |
|---|---|
| `Clock_Arduino/` | Clock firmware as an Arduino sketch. **This is the canonical source.** |
| `Clock_ESP01/` | ESP-01 firmware: captive portal, NTP, settings page |
| `Clock/Arduino Nano 3/` | Proteus (`.pdsprj`) simulation of the circuit |
| `Gerber files/` | Fabrication zips for three boards: Control, Display, ESP01 template |
| `Autocad_Model/` | AutoCAD drawings of the enclosure and the GNM-23881 display module |
| `GNM-23881AEG/` | Datasheet for the LED matrix module |

The whole project is one git repo, on GitHub at `karmazin-i-m/clock`. `Clock_Arduino/` used to
be a nested repo of its own pointing at Azure DevOps; it is a plain directory now, and there
are no submodules.

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

**Reading the serial line.** The UART now belongs to the ESP-01 (see below), so `setup()` no
longer prints `Initialized` — it sends a `<B` frame instead. The old advice still applies to
watching that line: neither `arduino-cli monitor` nor `cat /dev/ttyUSB0` reliably produces a
DTR edge, so the board never resets and you see nothing, which looks exactly like a dead board
and is not. Pulse DTR/RTS low then high over the open fd (`TIOCMSET`) and read for a few
seconds. Failing that, poke it: send `>Q*` with a valid checksum and a `<S` frame comes back.

**Unplug the ESP before flashing the Nano.** `arduino-cli upload` drives D0 from the USB
bridge; an ESP transmitting into the same pin corrupts the upload. This is what the jumper in
the ESP TX line is for - or press the status page's first quiet button and start the upload
straight away, which does the same thing without opening anything. See the link section.

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
as of the ESP link they are 20740 and 20014 bytes of flash. The variant also reaches the wire:
`linkSendStatus()` sends `-1` for humidity on a BMP280 board, and the ESP's page renders that
as "no sensor" rather than as a reading.

One piece of history worth keeping: the deleted Atmel Studio copy of this firmware used a
**different pin map** — `LatchPin`/`ClockPin` 3/4 rather than 4/3, and
`CatodeDataPin`/`CatodeClockPin` 6/7 rather than 7/6. If a board ever turns up whose display
is scrambled, that mapping is the thing to try.

`core.autocrlf` is `input` and the working copy is LF; keep it that way when editing.

## Firmware architecture

**Pins (canonical `.ino`)**: button on D2 (INT0, `INPUT_PULLUP`), row/anode shift register on
D5 data + D3 clock, column/cathode shift register chain on D7 data + D6 clock, shared latch on
D4, ESP-01 on D0/D1, sensors on A4/A5. That leaves D8–D13 and A0–A3 free, and A6/A7 free but
analog-input only — they cannot be digital pins.

**Rendering.** `out[8][3]` is the buffer `loop()` composes into; `frame[8][3]` is the one the
renderer latches, and `commitFrame()` publishes the first into the second with a 24 byte
`memcpy` under `noInterrupts()`, so a row is never latched from a half rebuilt screen.
`latchRow()` clocks three cathode bytes out LSB-first, then one anode byte (`catode[]`, a
rotating one-hot row select) MSB-first, then pulses the latch.

**The panel is multiplexed from Timer1, not from `loop()`.** `startRendering()`, called at the
end of `setup()` after `pinMode()`, puts Timer1 in CTC mode on the /8 prescaler (one tick =
0.5 µs). `OCR1A` is `ROW_PERIOD_TICKS - 1`, so `TIMER1_COMPA_vect` opens every row slot and
latches that row lit; `OCR1B` is `rowOnTicks`, so `TIMER1_COMPB_vect` lands inside the slot,
blanks the row and advances `renderRow`. Each ISR spends ~38 µs bit banging, i.e. ~15% of the
CPU, and `loop()` is free to block for as long as it likes without the display noticing.

This replaced a software multiplex that ran once per `loop()`. That version fixed the lit time
per row but not the *period* of a frame, so every blocking I2C read stretched a frame while
the panel was dark: the sensor poll cost 3 ms on a 4.75 ms loop and read as a once-per-second
flicker, the RTC poll cost 1.1 ms ten times a second and read as a shimmer. Both are gone.

Two constraints the ISRs impose. `ROW_ON_DIM_US` must stay above the ~38 µs `latchRow()` needs,
or `COMPB` would fire while `COMPA` is still shifting; and `rowOnTicks` must stay below `OCR1A`,
or `COMPB` never fires at all and one row sticks on. Both hold with room to spare at 60/450 µs
out of 500. `rowOnTicks` is 16 bit and written by `loop()`, so it is written with interrupts
off — the ISR would otherwise be able to catch half of a new value.

All five display pins are on PORTD, so the bit banging goes straight to the port (`sbi`/`cbi`)
rather than through `shiftOut`/`digitalWrite`. Read-modify-write is confined to those five
bits — never assign `PORTD` wholesale, or you will drop the serial pins and the button
pull-up on PD2.

Each row gets an identical `ROW_PERIOD_US` slot and is **blanked** at the end of it
(`latchRow(0,0,0,0)`). That is load-bearing: previously a row stayed lit until the next one
was latched, so the last row of a frame also burned through everything `loop()` did
afterwards — the bottom line was brighter, by an amount that changed with the screen being
shown. Brightness is exactly `rowOnTicks / ROW_PERIOD_TICKS`, which is also the whole dimming
mechanism: `rowOnTicks` drops to `ROW_ON_DIM_US` worth of ticks whenever `dimmedAt(dt.hour)`
says so. Dimming does not change the frame rate.

`dimmedAt()` replaced the inline `hour >= DIM_FROM_HOUR || hour < DIM_UNTIL_HOUR`. That test
was right only because the window wraps midnight; now that the hours come off the wire it also
has to handle `from < until` (a daytime window) and `from == until` (never dim), so it
branches on the order. The defines survive as the values a blank EEPROM starts from.

**Both ends are boundaries on a 0–24 line, not hours of the day**, and the end is half open.
That is why the range checks accept 24 and not just 23 — in three places, and they have to
agree: the form filter in `handleSave`, the `>C` handler in `linkSetConfig`, and the sanity
check in `loadConfig`. Without 24 there is no way to say "dim around the clock" at all: `0`–`23`
leaves the last hour bright and `0`–`0` lands in the equal case and never dims. 24 is legal at
the start too, where it reads as the far end and therefore behaves like zero.

**Loop cadence.** With rendering in the ISR, `loop()` itself runs in microseconds. The button
poll, the idle return and the sensor/RTC timers run on every pass and only gain resolution from
it, but composing a screen that often would be waste, so `loop()` returns early unless
`FRAME_PERIOD_MS` has passed — the switch that fills `out[][]` and `commitFrame()` keep the
~4 ms cadence the renderer actually consumes.

**I2C runs at 400 kHz.** `Wire.setClock(400000)` in `setup()` must stay *after* both
`clk.begin()` and `bme.begin()`, each of which calls `Wire.begin()` and would reset the bus to
100 kHz. It no longer affects brightness, but it still quarters the time every poll blocks.

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
without anybody touching it. The button has no interrupt of its own: `loop()` runs at well under 1 ms
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

## The ESP-01 link

The ESP-01 is a peripheral on the hardware UART at 9600 baud. It owns nothing on the panel;
pull it out and the clock behaves exactly as it did before. `Clock_ESP01/Clock_ESP01.ino` runs
WiFiManager's captive portal, an NTP client, and a small web server for the status and
settings pages. Credentials need no filesystem — the SDK keeps them itself — and the only
thing the ESP stores of its own is the time zone string, in emulated EEPROM.

**The transport had to be the hardware UART.** `SoftwareSerial` on one of the free pins
(D8–D13, A0–A3) is not an option: the Timer1 ISR spends ~38 µs twice every 500 µs, which is a
third of a bit period at 9600 baud, and `SoftwareSerial` in turn masks interrupts for a whole
byte, which would stick a display row lit. A second I2C master on the DS3231 bus is not an
option either — the ESP's I2C is bit-banged with no arbitration, and `loop()` polls the RTC ten
times a second.

**Frames.** One ASCII line, `\n` terminated, ending in `*` and two hex digits of XOR over
everything before the `*`. `handleLinkFrame()` drops anything that fails without replying, and
that is the point of the checksum: the ESP's boot ROM dumps 74880 baud chatter onto this wire
every time it starts, and none of it may be read as a command.

| Direction | Frame |
|---|---|
| ESP → Nano | `>T 2026-09-21 14:03:22`, `>Q`, `>G`, `>C <key> <value>`, `>M <seconds>` |
| Nano → ESP | `<S <date> <time> <tenths °C> <mmHg> <%>`, `<C <from> <until> <period>`, `<K`, `<E`, `<B`, `<R` |

Three things here are load-bearing:

- **A reply must fit in 64 bytes**, the `HardwareSerial` transmit buffer, of which 63 are
  usable. What has to fit is a *pair*: the ESP polls readings every 3 s and settings every 30 s,
  so both replies routinely leave in one pass of `updateLink()` — 40 bytes of `<S` plus 16 of
  `<C`. That is also why `linkSendStatus()` clamps its three readings: `bme.begin()`'s status is
  discarded, an absent sensor leaves them NaN, and `int(NaN)` is -32768, which would add six
  characters a field. Go past 63 and `Serial.print` blocks `loop()` until the wire drains, at
  about a millisecond a byte. `<R` is the one frame allowed to break this, and only because it
  cannot be made to collide often enough to matter: seven bytes on top of the 57 byte pair is
  64, one over, on the single pass where a ten second hold coincides with both polls. The cost
  is a millisecond of blocking on the rarest event the clock has, and the panel is on the ISR,
  so nothing is visible. Do not take it as licence to add a fifth frame.
- **`>T` is refused while `state >= settings`.** Being overruled mid-edit by a frame off the
  wire is worse than the drift that waiting costs. The ESP is not told why — it does not track
  acknowledgements at all, it compares the time the clock reports back with its own and pushes
  again when they disagree. That one mechanism covers the refusal, a lost frame, and the
  daylight saving change.
- **Temperature travels as tenths of a degree, as an integer.** Formatting a float on this chip
  costs over a kilobyte of flash, and the ESP only divides it back.

**The ten second hold resets the ESP's network.** `updateButton()` carries a second latch,
`buttonResetFired`, independent of `buttonHoldFired` — which has necessarily already fired by
then, since the longest threshold above it is three seconds — and it does not go through
`buttonHold()`, because that dispatches per screen and this gesture means the same thing
everywhere. It sends `<R` and drops `state` back to `times`: on the clock screen the three
second threshold has already opened programming on the way past, and leaving through
`endSettings` would write the half edited digits to the RTC. The ESP answers it by calling
`wm.resetSettings()` and restarting rather than re-entering the portal in place, since the
handler runs inside the link parser with a half read buffer and possibly a web request in
flight. Nothing is drawn on the panel: the clock knows only that it sent a frame, and whether
an ESP was listening is not its business.

**EEPROM.** `dimFromHour`, `dimUntilHour` and `marqueePeriodMinutes` are runtime globals now,
saved at `CONFIG_ADDRESS` behind a `'K' 'C' 1` signature. A chip without that signature keeps
the compiled defaults rather than three bytes of `0xFF`. `saveConfig()` uses `EEPROM.update()`,
so an unchanged form costs no write; a changed byte stalls for ~3.3 ms, but with interrupts on,
so the panel keeps refreshing through it.

**The module announces itself, rather than being looked up.** `startDiscovery()` brings up
mDNS, LLMNR and NetBIOS on one name, `HostName` — `k-clock.local` for everything modern,
a bare `k-clock` for Windows, and `MDNS.addService("http", "tcp", 80)` so the web server is
advertised and not only the host. It exists because the name used to be an accident:
`ArduinoOTA::begin()` calls `MDNS.begin()` for its own purposes, so `k-clock.local` resolved
exactly as long as an OTA password was set and advertised `_arduino._tcp` rather than a web
server. `loop()` calls `MDNS.update()` itself for the same reason, even though
`ArduinoOTA.handle()` also does — that coupling is what the change removes. `startDiscovery()`
runs after `startOta()` so the http service is registered on the near side of the responder
restart that `ArduinoOTA::begin()` triggers; `MDNS.begin()` twice is safe, it sets the hostname
and restarts without dropping registered services.

**The ESP is flashed over WiFi, and its FQBN carries the flash layout.** Build it as
`esp8266:esp8266:generic:eesz=1M`, not plain `generic`: the default `1M64` reserves 64 KB for a
filesystem this firmware never mounts — credentials live in the SDK's own area and the settings
in emulated EEPROM — and that reservation comes straight out of the space an update needs. The
two layouts put `_EEPROM_start` at the same address, so the stored settings survive the switch.
An OTA upload takes two commands, because `-F/--upload-field` belongs to `upload` and
`compile --upload` refuses it with `unknown flag` - compile into the default cache, then upload
straight after.

OTA starts with the compiled-in `DefaultOtaPassword`, `k-clock`, which `loadSettings()` writes
into a fresh EEPROM and also fills in for a module whose stored password is empty — that is a
module flashed back when there was no default, whose magic word is valid so the fresh-EEPROM
branch never runs for it. A password set on the settings page overrides it, and
`otaPasswordIsDefault()` is what both pages use to say which is in force. The guard in
`startOta()` is now unreachable and stays because `ArduinoOTA` treats an empty password as no
password rather than as a refusal. Changing the password restarts the module, because `ArduinoOTA::setPassword` silently does nothing once
`begin()` has run and `begin()` itself returns early when already initialised — so calling
`startOta()` a second time would appear to work while the old password stayed in force.

The ceiling is half the chip, since the running image and the incoming one are both in flash
during an update: ~502 KB against the current 400 KB. Past that, OTA fails with nothing to
explain it. Recovery from a bad image is the portal — `autoConnect` raises `K-Clock` when it
cannot join — and only an image that crashes earlier than that needs the cable.

**Going quiet means leaving the wire, not just stopping the data.** This is the whole point and
it was got wrong once: a UART transmit pin is a push-pull output idling high, so an ESP that
has merely stopped writing is still driving D0, and `arduino-cli upload` still fails with `not
in sync`. Both sides shut the UART down instead. On the ESP `Serial.end()` reaches
`uart_uninit()`, which does `pinMode(1, INPUT)`; on the Nano it clears `TXEN0`, handing D1 back
to a `DDRD` bit that nothing in this sketch ever sets, and `pinMode(1, INPUT)` says so out
loud. `linkOffline` on each side tracks whether the UART is currently down, and `updateLink()`
reconciles it against the timer on every pass, so the port comes back by itself. Proven by
flashing the Nano over USB with the ESP still connected and the jumper still in.

**Silence is a timer on each side, never a latch.** `linkQuiet()` on the ESP and `linkMuted()`
on the Nano are the same two lines - `ms > 0 && millis() - start < ms` - which is subtraction
of two live `millis()` values rather than a stored deadline, so neither can be stranded by the
rollover. Both gates sit inside `linkSend()` rather than at the call sites, so nothing added
later talks through them by accident; on the Nano that deliberately covers `<B` and `<R` too.
The ESP additionally drains its receiver without parsing while quiet, so `linkFramesDropped`
keeps meaning "chatter that arrived when the clock should have been answering" rather than
counting avrdude.

The two directions are two halves of one problem, and the status page has a button for each.
Muting the ESP is local - the transmitter comes off D0 and avrdude has the clock's bootloader
to itself. Muting the Nano has to be sent, as `>M <seconds>`, while the ESP still can; the
handler clears the ESP's own quiet period first, or the one frame that matters would be the
one the gate swallows. `LINK_MUTE_MAX_SECONDS` is ten minutes, and `linkMute()` validates
digit by digit and refuses with `<E` rather than clamping, because a clock that has talked
itself into silence is worse than a noisy wire. The `<K` goes out and is flushed before the
gate closes.

**A marquee period of zero turns the marquee off**, and that same guard is what keeps the
`dt.minute % marqueePeriodMinutes` in `loop()` from dividing by zero. `lastMarqueeMinute` is
cleared as soon as the minute stops matching, rather than only being overwritten when one does.
That is what makes a period of sixty work: with a single candidate minute, holding the last
value would make `dt.minute != lastMarqueeMinute` false for ever after the first run.

## Known rough edges in the current code

Do not "fix" these silently as drive-by cleanups — they change hardware behavior:

- `bme.begin(0x76)` — the address is hardcoded and the returned `status` is discarded.
- `clk.setDateTime(__DATE__, __TIME__)` in `setup()` is commented out on purpose; uncommenting
  it reseeds the RTC from build time on every boot.
- `RENDER_LINE_COUNT` is defined but unused — the loops use a literal `8`.
- `hourMajorSettings` wraps the tens of hours at 3 and `hourMinorSettings` at 10, so the
  programming screens will happily accept 25:xx or 29:xx and write it to the RTC.
