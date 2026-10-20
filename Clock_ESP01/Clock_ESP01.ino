// The clock's WiFi side, on an ESP-01.
//
// It does four things: raises a captive portal so the network can be chosen
// from a phone, keeps the DS3231 on the Nano honest against NTP, serves a
// status page, and lets the clock's own settings be edited from a browser.
//
// What makes the portal page open by itself is that the module answers every
// DNS query with its own address and redirects the plain HTTP probe each phone
// fires after joining a network - Android asks connectivitycheck.gstatic.com
// for a 204, iOS asks captive.apple.com for the word Success. Neither gets what
// it expects, both conclude they are behind a portal and show the page
// unprompted. WiFiManager implements both halves.
//
// The link to the Nano is the ESP's only UART, so there is no console to print
// to once the clock is wired up. Diagnostics live on the status page instead,
// with DEBUG_ON_SERIAL1 as an escape hatch - see below.

#include <ESP8266WiFi.h>
#include <WiFiClientSecure.h>
#include <ESP8266WebServer.h>
#include <ESP8266mDNS.h>
#include <ESP8266LLMNR.h>
#include <ESP8266NetBIOS.h>
#include <ArduinoOTA.h>
#include <WiFiManager.h>
#include <EEPROM.h>
#include <time.h>

// Flash once with this set to 1 to drop the stored network and get the portal
// back, then set it to 0 again. The portal has an Erase button that does the
// same, but only once the portal is reachable.
#define FORGET_WIFI 0

// GPIO2 is the ESP-01's blue LED and also the only pin Serial1 can transmit on,
// so the two are mutually exclusive. Normally the LED wins, because the status
// page carries the diagnostics. Set this to 1 to get a 115200 console on GPIO2
// instead when something needs watching live.
#define DEBUG_ON_SERIAL1 0

#if DEBUG_ON_SERIAL1
#define DBG(x) Serial1.println(x)
#else
#define DBG(x) do {} while (0)
#endif

static const char ApName[] = "K-Clock";

// The name the module answers to once it is on the home network, in three
// places at once: as the DHCP name the router lists it under, as k-clock.local
// over mDNS, and as a bare k-clock over LLMNR and NetBIOS. Keep it lowercase
// and free of anything but letters, digits and hyphens - all three protocols
// want a plain host label.
static const char HostName[] = "k-clock";

// The Nano talks at 9600 and nothing about this link is in a hurry: one status
// exchange every few seconds and a time frame once an hour.
static const unsigned long LinkBaud = 9600;

static const uint8_t StatusLedPin = LED_BUILTIN;

// Fast even blinking while the portal waits for somebody, and dark once the
// module is on a network. It used to flash every couple of seconds online too,
// and on a desk at night the blue LED is far brighter than the clock's own
// dimmed panel; the status page already says whether the module is online.
static const unsigned long PortalBlinkMs = 150;

// After a power cut the module is up long before the router is, finds nothing
// to join, and raises the portal - and WiFiManager switches the station side
// off while the portal runs, so it would never look for the network again.
// With a network saved, the portal therefore gives up after this long, unless
// a phone is connected to it, and loop() tries the saved network again.
static const unsigned long PortalRetrySeconds = 180;

// Bounds one attempt at the saved network. It blocks loop() for that long, the
// link to the Nano included, so it is kept short; a router that is up answers
// within a few seconds.
static const unsigned long ConnectTimeoutSeconds = 20;

static const unsigned long StatusPollMs = 3000;
static const unsigned long ConfigPollMs = 30000;
static const unsigned long TimePushMs = 3600000UL;
static const unsigned long TimeRetryMs = 60000;
static const unsigned long LinkStaleMs = 15000;

// Long enough to start arduino-cli and let avrdude finish - an upload at 57600
// takes some fifteen seconds - and short enough that forgetting about it costs
// one missed status poll rather than an evening of wondering why the page is
// stale.
static const unsigned long QuietDefaultSeconds = 30;
static const unsigned long QuietMaxSeconds = 600;

// How far the clock may be out before it is worth correcting. Wide enough that
// the second the frame spends in flight is never mistaken for drift.
static const long TimeToleranceSeconds = 5;

// Ukraine has been on UTC+2 with the European summer time rule, but the rule is
// exactly the kind of thing that gets legislated away, so it is a setting
// rather than a constant. Drop the EEST half and leave "EET-2" if the clocks
// stop changing.
static const char DefaultTz[] = "EET-2EEST,M3.5.0/3,M10.5.0/4";

// The password OTA starts with. It is in the repository, so be clear about what
// it is worth: it stops a neighbour who stumbles onto the module, and nothing
// else. Anyone holding this source can write to a clock on the same network.
// The alternative was shipping without one, and that turned out worse in
// practice - a module that arrives with OTA already working can be recovered
// over the air on the day it is set up, whereas one waiting to be told a
// password first has to be opened and cabled if anything goes wrong before
// somebody gets to the form. Set your own on the settings page and this stops
// applying to your module; the stored one always wins.
static const char DefaultOtaPassword[] = "k-clock";

// Bumped when the struct grows, so a module carrying the older layout falls
// back to the defaults instead of reading the new field out of stale bytes.
// KCL3 only appended the token, so a KCL2 module keeps its time zone and OTA
// password - they sit at the same offsets - and starts out unbound.
static const uint32_t SettingsMagic = 0x4B434C33;    // "KCL3"
static const uint32_t SettingsMagicKcl2 = 0x4B434C32;  // "KCL2"

struct EspSettings {
  uint32_t magic;
  char tz[48];
  char otaPassword[33];
  // The device's own credential from enroll: kcd1_ and 43 more characters.
  // Empty until the module is bound, and replaced only by a successful
  // enrollment - never cleared because the server refused it, or one bad
  // deploy answering 401 would unbind every clock for good (DESIGN.md §5.3).
  char token[64];
};

EspSettings settings;

WiFiManager wm;
ESP8266WebServer server(80);

unsigned long blinkTime;
unsigned long statusPollTime;
unsigned long configPollTime;
unsigned long timePushTime;
bool ledLit;
bool serverStarted;
bool timeSynced;
bool timePushed;
bool otaReady;

// Rollover safe as a pair, like the clock's own: always a subtraction of two
// millis() values rather than a stored deadline that wraps.
unsigned long quietStart;
unsigned long quietMs;
bool otaActive;

// Everything last heard from the clock. The web page renders this cache rather
// than asking the Nano per request, so a browser refresh cannot flood the link.
struct ClockStatus {
  bool valid;
  unsigned long heardAt;
  char stamp[24];
  int temperatureTenths;
  int pressureMmHg;
  int humidityPercent;  // negative on a BMP280 board, which has no humidity
};

ClockStatus clockStatus;

struct ClockConfig {
  bool valid;
  uint8_t dimFrom;
  uint8_t dimUntil;
  uint8_t marquee;
};

ClockConfig clockConfig;

unsigned long linkFramesGood;
unsigned long linkFramesDropped;
unsigned long linkFramesRejected;
char linkLine[80];
uint8_t linkLength;
bool linkOverflow;

// True while the quiet period has the UART shut down and GPIO1 released.
bool linkOffline;

// ---------------------------------------------------------------- settings --

void loadSettings() {
  EEPROM.begin(sizeof(EspSettings));
  EEPROM.get(0, settings);

  if (settings.magic == SettingsMagicKcl2) {
    // The bytes behind the old layout are whatever the sector held before.
    settings.magic = SettingsMagic;
    memset(settings.token, 0, sizeof(settings.token));
  } else if (settings.magic != SettingsMagic) {
    settings.magic = SettingsMagic;
    strncpy(settings.tz, DefaultTz, sizeof(settings.tz) - 1);

    strncpy(settings.otaPassword, DefaultOtaPassword,
            sizeof(settings.otaPassword) - 1);
    memset(settings.token, 0, sizeof(settings.token));
  }

  // Outside the branch on purpose. A half written commit can leave the magic
  // word intact and a string without its terminator, and everything that
  // touches these afterwards reads them as C strings.
  settings.tz[sizeof(settings.tz) - 1] = '\0';
  settings.otaPassword[sizeof(settings.otaPassword) - 1] = '\0';
  settings.token[sizeof(settings.token) - 1] = '\0';

  // A module carrying the layout from before there was a default has a valid
  // magic word and an empty password, so the branch above never runs for it.
  // Without this it would keep OTA switched off for ever.
  if (settings.otaPassword[0] == '\0') {
    strncpy(settings.otaPassword, DefaultOtaPassword,
            sizeof(settings.otaPassword) - 1);
  }
}

void saveSettings() {
  EEPROM.put(0, settings);
  EEPROM.commit();
}

// Worth showing on the status page: it is the difference between a clock only
// this repository can write to and one only its owner can.
bool otaPasswordIsDefault() {
  return strcmp(settings.otaPassword, DefaultOtaPassword) == 0;
}

// -------------------------------------------------------------------- link --

static uint8_t linkChecksum(const char *payload) {
  uint8_t sum = 0;
  for (const char *p = payload; *p; p++) {
    sum ^= (uint8_t)*p;
  }
  return sum;
}

static int hexDigit(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  return -1;
}

// The whole point of the quiet period: while it lasts the ESP's transmitter
// stays off D0, so avrdude has the clock's bootloader to itself. The gate is
// here rather than at the call sites, so nothing can be added later that talks
// through it by accident.
bool linkQuiet() {
  return quietMs > 0 && millis() - quietStart < quietMs;
}

unsigned long quietSecondsLeft() {
  if (!linkQuiet()) {
    return 0;
  }
  return (quietMs - (millis() - quietStart) + 999) / 1000;
}

void linkSend(const char *payload) {
  if (linkQuiet()) {
    return;
  }

  char tail[4];
  snprintf(tail, sizeof(tail), "*%02X", linkChecksum(payload));
  Serial.print(payload);
  Serial.println(tail);
}

// Strips the checksum and says whether it matched. Frames that fail are dropped
// without a word: the Nano's bootloader and our own reset both put noise on
// this wire, and none of it deserves a reply.
static bool linkVerify(char *line) {
  char *star = strrchr(line, '*');
  if (star == NULL || strlen(star) != 3) {
    return false;
  }

  int high = hexDigit(star[1]);
  int low = hexDigit(star[2]);
  if (high < 0 || low < 0) {
    return false;
  }

  *star = '\0';
  return linkChecksum(line) == (uint8_t)(high * 16 + low);
}

void handleStatusFrame(char *args) {
  // "<S 2026-09-21 14:03:22 234 745 57" - temperature in tenths of a degree so
  // the Nano never has to format a float.
  char date[12];
  char clockTime[10];
  int tenths, mmhg, humidity;

  if (sscanf(args, "%11s %9s %d %d %d", date, clockTime, &tenths, &mmhg, &humidity) != 5) {
    linkFramesDropped++;
    return;
  }

  snprintf(clockStatus.stamp, sizeof(clockStatus.stamp), "%s %s", date, clockTime);
  clockStatus.temperatureTenths = tenths;
  clockStatus.pressureMmHg = mmhg;
  clockStatus.humidityPercent = humidity;
  clockStatus.heardAt = millis();
  clockStatus.valid = true;
}

void handleConfigFrame(char *args) {
  int from, until, marquee;
  if (sscanf(args, " %d %d %d", &from, &until, &marquee) != 3) {
    linkFramesDropped++;
    return;
  }

  clockConfig.dimFrom = from;
  clockConfig.dimUntil = until;
  clockConfig.marquee = marquee;
  clockConfig.valid = true;
}

void handleLinkFrame(char *line) {
  if (line[0] != '<') {
    linkFramesDropped++;
    return;
  }

  if (!linkVerify(line)) {
    linkFramesDropped++;
    return;
  }

  linkFramesGood++;

  switch (line[1]) {
    case 'S':
      handleStatusFrame(line + 2);
      break;
    case 'C':
      handleConfigFrame(line + 2);
      break;
    case 'B':
      // The clock restarted, so anything cached about it is stale and the time
      // is worth pushing again rather than waiting out the hour.
      DBG(F("Nano booted"));
      clockConfig.valid = false;
      timePushed = false;
      break;
    case 'R':
      // Ten seconds on the clock's button. The stored network goes, and with it
      // the only way back in if the router it named no longer exists. Restart
      // rather than calling autoConnect again from here: this runs inside the
      // link parser, with a half read buffer and a web request possibly in
      // flight, and the portal wants a clean start.
      DBG(F("Network reset from the clock"));
      wm.resetSettings();
      delay(100);
      ESP.restart();
      break;
    case 'K':
      break;
    case 'E':
      // Nothing to do about it here - a refused time frame is noticed by
      // clockDisagrees() a minute later, and a refused setting by the config
      // poll putting the old value back on the page. Counted so the status
      // page can show that the two sides are arguing.
      linkFramesRejected++;
      break;
    default:
      linkFramesDropped++;
      break;
  }
}

void updateLink() {
  // Going quiet is not a matter of saying nothing. GPIO1 is a push-pull output
  // that idles high, and while it is driving D0 the USB bridge cannot pull that
  // line low cleanly however silent we are - which is exactly what made the
  // first version of this useless as a substitute for the jumper. Serial.end()
  // is what actually steps off the wire: uart_uninit() puts the pin back to
  // pinMode(1, INPUT), a genuine high impedance.
  bool quiet = linkQuiet();

  if (quiet && !linkOffline) {
    Serial.flush();
    Serial.end();
    linkOffline = true;
    linkLength = 0;
    linkOverflow = false;
    return;
  }

  if (!quiet && linkOffline) {
    Serial.begin(LinkBaud);
    linkOffline = false;
    linkLength = 0;
    linkOverflow = false;
  }

  if (linkOffline) {
    return;
  }

  while (Serial.available()) {
    char c = Serial.read();

    if (c == '\r') {
      continue;
    }

    if (c == '\n') {
      if (!linkOverflow && linkLength > 0) {
        linkLine[linkLength] = '\0';
        handleLinkFrame(linkLine);
      }
      linkLength = 0;
      linkOverflow = false;
      continue;
    }

    if (linkLength >= sizeof(linkLine) - 1) {
      linkOverflow = true;
      continue;
    }

    linkLine[linkLength++] = c;
  }
}

bool linkAlive() {
  return clockStatus.valid && millis() - clockStatus.heardAt < LinkStaleMs;
}

// -------------------------------------------------------------------- time --

void startTimeSync() {
  configTzTime(settings.tz, "pool.ntp.org", "time.google.com", "time.cloudflare.com");
  timeSynced = false;
  timePushed = false;
}

// Anything before 2021 means the SDK has not been given a real time yet, which
// is how configTzTime reports "no answer from NTP so far".
bool haveRealTime() {
  return time(nullptr) > 1609459200;
}

// The clock is never asked whether it accepted a time frame, it is simply
// watched: whatever it reports on the next status poll is compared with ours,
// and a disagreement means push again. That covers the frame it refused because
// somebody was setting the time by hand, and it is also what carries the clock
// across a daylight saving boundary without waiting out the hourly push.
bool clockDisagrees() {
  if (!clockStatus.valid || !haveRealTime()) {
    return false;
  }

  int year, month, day, hour, minute, second;
  if (sscanf(clockStatus.stamp, "%d-%d-%d %d:%d:%d",
             &year, &month, &day, &hour, &minute, &second) != 6) {
    return false;
  }

  struct tm reported;
  memset(&reported, 0, sizeof(reported));
  reported.tm_year = year - 1900;
  reported.tm_mon = month - 1;
  reported.tm_mday = day;
  reported.tm_hour = hour;
  reported.tm_min = minute;
  reported.tm_sec = second;
  reported.tm_isdst = -1;

  time_t theirs = mktime(&reported);
  if (theirs == (time_t)-1) {
    return false;
  }

  long difference = (long)(time(nullptr) - theirs);
  return difference > TimeToleranceSeconds || difference < -TimeToleranceSeconds;
}

void pushTime() {
  time_t now = time(nullptr);
  struct tm local;
  localtime_r(&now, &local);

  // Local time rather than an epoch: the Nano already has exactly this call in
  // its own settings screen, and it keeps the timezone question on this side of
  // the wire where the rules actually live.
  char frame[40];
  snprintf(frame, sizeof(frame), ">T %04d-%02d-%02d %02d:%02d:%02d",
           local.tm_year + 1900, local.tm_mon + 1, local.tm_mday,
           local.tm_hour, local.tm_min, local.tm_sec);
  linkSend(frame);

  timePushed = true;
  timePushTime = millis();
}

// ------------------------------------------------------------------- cloud --

// The device half of Server/DESIGN.md §5, so far only binding: a code typed on
// the settings page is exchanged over TLS for this module's own token. The
// code is short-lived because that page is plain HTTP that anyone on the home
// network can read; the token never appears on it.
static const char CloudHost[] = "clock.karmazin.com.ua";
static const uint16_t CloudPort = 443;
static const unsigned long CloudReadMs = 5000;

// What the server builds this device's policy from, and the version it records
// on enrollment - the only moment it learns one, so bump it with a release.
static const char DeviceProfile[] = "esp8266-1m";
static const char FwVersion[] = "2026.10.21";

// Roots rather than the leaf or YE1, which Let's Encrypt reissues - a pin on
// either would blind the clock within ninety days. Two of them, because Caddy
// currently ends the chain on Root YE cross-signed by X2, and BearSSL stops at
// the first certificate whose issuer is an anchor, so either one carries it
// whichever end Caddy settles on. The probe that measured all this is gone;
// the figures are in CLAUDE.md.
static const char CloudAnchors[] PROGMEM = R"EOF(
-----BEGIN CERTIFICATE-----
MIIB2TCCAWCgAwIBAgIRAKQCa6LvbHwg1AR+XmWmk4AwCgYIKoZIzj0EAwMwLjEL
MAkGA1UEBhMCVVMxDTALBgNVBAoTBElTUkcxEDAOBgNVBAMTB1Jvb3QgWUUwHhcN
MjUwOTAzMDAwMDAwWhcNNDUwOTAyMjM1OTU5WjAuMQswCQYDVQQGEwJVUzENMAsG
A1UEChMESVNSRzEQMA4GA1UEAxMHUm9vdCBZRTB2MBAGByqGSM49AgEGBSuBBAAi
A2IABDwS/6vhrcVqcbBo+wgdI3fwn9x7DNJJOY/lTOti0vkwuRN87RhEhTH17E7X
yFjWsPYhIPt/wzOqxTd2b+4ZJNy9ID04YywF9U5zasDVyGSNErVNtz8uSGh5izW8
7j77GaNCMEAwDgYDVR0PAQH/BAQDAgEGMA8GA1UdEwEB/wQFMAMBAf8wHQYDVR0O
BBYEFKPIJlqOoUzQNWP8myPIOq5W809WMAoGCCqGSM49BAMDA2cAMGQCMHhMr8N9
LdL1VQKs9BdV81r76eXRB6mtjuNjzk6/lBsPNToWLTDzGYgtQKO1jl63uAIwGV7m
onyF377c+MM1oqVNs17sgu7F9YKZwgLmVbeOMDbKAXHtKMDLbiGllCcs8f47
-----END CERTIFICATE-----
-----BEGIN CERTIFICATE-----
MIICGzCCAaGgAwIBAgIQQdKd0XLq7qeAwSxs6S+HUjAKBggqhkjOPQQDAzBPMQsw
CQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2gg
R3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMjAeFw0yMDA5MDQwMDAwMDBaFw00
MDA5MTcxNjAwMDBaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5ldCBT
ZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgyMHYw
EAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0HttwW
+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7AlF9
ItgKbppbd9/w+kHsOdx1ymgHDB/qo0IwQDAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0T
AQH/BAUwAwEB/zAdBgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwCgYIKoZI
zj0EAwMDaAAwZQIwe3lORlCEwkSHRhtFcP9Ymd70/aTSVaYgLXTWNLxBo1BfASdW
tL4ndQavEi51mI38AjEAi/V3bNTIZargCyzuFJ0nN6T5U6VR5CmD1/iQMVtCnwr1
/q4AaOeMSQ+2b1tbFfLn
-----END CERTIFICATE-----
)EOF";

// A code is held here between the form and loop(), which runs the exchange
// once the module can: online, with a real time to check the certificate
// against, and with the clock answering, since the server takes the sensor
// model from the first enrollment and the Nano is the only one that knows it.
char enrollCode[17];
bool enrollPending;

// The last attempt's outcome for the status page; NULL until there is one.
const __FlashStringHelper *enrollResult;
int enrollHttpStatus;

static String heapSummary() {
  return String(ESP.getFreeHeap()) + F(" / ") + ESP.getMaxFreeBlockSize() +
         F(" Б, фрагм. ") + ESP.getHeapFragmentation() + F(" %");
}

// Server/DESIGN.md §5 rule 5: every key is unique as a substring of the whole
// contract and never occurs inside a value, so the first "key":" is the key.
static bool jsonString(const char *json, const char *key, char *out, size_t size) {
  char pattern[24];
  snprintf(pattern, sizeof(pattern), "\"%s\":\"", key);
  const char *start = strstr(json, pattern);
  if (start == NULL) {
    return false;
  }
  start += strlen(pattern);

  const char *end = strchr(start, '"');
  if (end == NULL || (size_t)(end - start) >= size) {
    return false;
  }

  memcpy(out, start, end - start);
  out[end - start] = '\0';
  return true;
}

// One POST on one connection, closed afterwards: Caddy resumes sessions only
// from tickets and BearSSL only from session IDs, so a kept socket would buy
// nothing by the next request. Blocks loop() for the handshake, ~4 s at
// 160 MHz. Returns the HTTP status, or 0 when nothing usable came back.
static int cloudPost(const char *path, const char *json, const char *token,
                     char *reply, size_t replySize, char **body) {
  BearSSL::X509List anchors(CloudAnchors);
  BearSSL::WiFiClientSecure client;
  client.setTrustAnchors(&anchors);
  client.setX509Time(time(nullptr));

  if (!client.connect(CloudHost, CloudPort)) {
    return 0;
  }

  client.print(F("POST "));
  client.print(path);
  client.print(F(" HTTP/1.1\r\nHost: "));
  client.print(CloudHost);
  if (token != NULL) {
    client.print(F("\r\nAuthorization: Bearer "));
    client.print(token);
  }
  client.print(F("\r\nContent-Type: application/json\r\nContent-Length: "));
  client.print(strlen(json));
  client.print(F("\r\nConnection: close\r\n\r\n"));
  client.print(json);

  size_t length = 0;
  unsigned long start = millis();
  while (length < replySize - 1 && millis() - start < CloudReadMs &&
         (client.connected() || client.available())) {
    int got = client.read((uint8_t *)reply + length, replySize - 1 - length);
    if (got > 0) {
      length += got;
    } else {
      delay(10);
    }
  }
  reply[length] = '\0';
  client.stop();

  // Every device response carries a Content-Length and no chunking (rule 4),
  // and is small enough to arrive whole, so the headers need no parsing.
  char *separator = strstr(reply, "\r\n\r\n");
  if (strncmp(reply, "HTTP/1.", 7) != 0 || separator == NULL) {
    return 0;
  }
  *body = separator + 4;
  return atoi(reply + 9);
}

// Anything else is refused at the form, which also keeps the code safe to
// drop into the JSON body as it is.
static bool validEnrollCode(const String &code) {
  if (code.length() == 0 || code.length() >= sizeof(enrollCode)) {
    return false;
  }
  for (unsigned int i = 0; i < code.length(); i++) {
    if (!isAlphaNumeric(code[i]) && code[i] != '-') {
      return false;
    }
  }
  return true;
}

static const __FlashStringHelper *describeEnrollError(int status, const char *err) {
  if (status == 0)                       return F("немає з'єднання з сервером");
  if (strcmp(err, "code_invalid") == 0)  return F("код невірний");
  if (strcmp(err, "code_expired") == 0)  return F("код прострочений — створіть новий");
  if (strcmp(err, "code_used") == 0)     return F("код уже використано — створіть новий");
  if (strcmp(err, "slow_down") == 0)     return F("забагато спроб — зачекайте хвилину");
  return F("сервер відмовив");
}

// No retry on failure: the code is single use and lives fifteen minutes, so a
// refused one is spent and a fresh one has to come from the account anyway.
void cloudEnroll() {
  enrollPending = false;

  uint8_t raw[6];
  WiFi.macAddress(raw);
  char mac[13];
  snprintf(mac, sizeof(mac), "%02X%02X%02X%02X%02X%02X",
           raw[0], raw[1], raw[2], raw[3], raw[4], raw[5]);

  // The Nano reports -1 for humidity on a BMP280 board.
  char json[160];
  snprintf(json, sizeof(json),
           "{\"code\":\"%s\",\"hw\":\"%s\",\"fw\":\"%s\",\"mac\":\"%s\","
           "\"model\":\"%s\",\"chip\":\"%08x\"}",
           enrollCode, DeviceProfile, FwVersion, mac,
           clockStatus.humidityPercent >= 0 ? "bme280" : "bmp280", ESP.getChipId());
  memset(enrollCode, 0, sizeof(enrollCode));

  char reply[1024];
  char *body = NULL;
  enrollHttpStatus = cloudPost("/d/v1/enroll", json, NULL, reply, sizeof(reply), &body);

  char token[sizeof(settings.token)];
  if (enrollHttpStatus == 200 && jsonString(body, "token", token, sizeof(token)) &&
      strncmp(token, "kcd1_", 5) == 0) {
    strcpy(settings.token, token);
    saveSettings();
    enrollResult = F("прив'язано");
    return;
  }

  char err[24] = "";
  if (body != NULL) {
    jsonString(body, "err", err, sizeof(err));
  }
  enrollResult = describeEnrollError(enrollHttpStatus, err);
}

// Why a pending code has not gone out yet, for the status page.
static const __FlashStringHelper *enrollWaitingFor() {
  if (WiFi.status() != WL_CONNECTED) return F("чекає на мережу");
  if (!haveRealTime())                return F("чекає на час з NTP");
  if (!linkAlive())                   return F("чекає на годинник");
  return F("виконується");
}

// -------------------------------------------------------------------- pages --

static const char PageStyle[] PROGMEM =
  "<meta name=viewport content='width=device-width,initial-scale=1'>"
  "<style>"
  "body{font:16px system-ui,sans-serif;margin:0;padding:24px 16px;"
  "background:#14161a;color:#e8eaed}"
  "main{max-width:32rem;margin:0 auto}"
  "h1{font-size:1.4rem;margin:0 0 1.5rem}"
  "h2{font-size:1rem;margin:2rem 0 .75rem;color:#9aa0a6;font-weight:600}"
  ".row{display:flex;justify-content:space-between;padding:.6rem 0;"
  "border-bottom:1px solid #2a2e35}"
  ".row b{font-weight:600}"
  "label{display:block;margin:1rem 0 .3rem;color:#9aa0a6;font-size:.9rem}"
  "input{width:100%;box-sizing:border-box;padding:.6rem;border-radius:8px;"
  "border:1px solid #3c4149;background:#1d2025;color:#e8eaed;font-size:1rem}"
  "button{margin-top:1.5rem;width:100%;padding:.75rem;border:0;border-radius:8px;"
  "background:#4c8bf5;color:#fff;font-size:1rem;font-weight:600}"
  "a{color:#8ab4f8}"
  ".warn{background:#3b2c1a;border:1px solid #6b4f24;padding:.75rem;"
  "border-radius:8px;margin-bottom:1rem}"
  "</style>";

// The time zone is the one stored string that comes back out into an HTML
// attribute, and a lone apostrophe in it would end the attribute early.
static String escapeAttribute(const char *text) {
  String out;
  for (const char *p = text; *p; p++) {
    switch (*p) {
      case '&':  out += F("&amp;");  break;
      case '<':  out += F("&lt;");   break;
      case '"':  out += F("&quot;"); break;
      case '\'': out += F("&#39;");  break;
      default:   out += *p;          break;
    }
  }
  return out;
}

static void appendRow(String &page, const __FlashStringHelper *label, const String &value) {
  page += F("<div class=row><span>");
  page += label;
  page += F("</span><b>");
  page += value;
  page += F("</b></div>");
}

void handleRoot() {
  String page;
  page.reserve(2560);
  page = F("<!doctype html><title>K-Clock</title>");
  page += FPSTR(PageStyle);
  page += F("<main><h1>K-Clock</h1>");

  if (!linkAlive()) {
    page += F("<div class=warn>Годинник не відповідає. Перевірте лінію "
              "TX/RX і живлення Nano.</div>");
  }

  page += F("<h2>Годинник</h2>");

  if (clockStatus.valid) {
    appendRow(page, F("Час на RTC"), String(clockStatus.stamp));

    String t(clockStatus.temperatureTenths / 10.0, 1);
    appendRow(page, F("Температура"), t + F(" °C"));
    appendRow(page, F("Тиск"), String(clockStatus.pressureMmHg) + F(" мм рт. ст."));

    if (clockStatus.humidityPercent >= 0) {
      appendRow(page, F("Вологість"), String(clockStatus.humidityPercent) + F(" %"));
    } else {
      appendRow(page, F("Вологість"), F("датчика немає"));
    }
  } else {
    appendRow(page, F("Стан"), F("даних ще немає"));
  }

  page += F("<h2>Мережа</h2>");
  appendRow(page, F("SSID"), WiFi.SSID());
  appendRow(page, F("IP"), WiFi.localIP().toString());
  appendRow(page, F("Ім'я"), String(F("http://")) + HostName + F(".local/"));
  appendRow(page, F("Сигнал"), String(WiFi.RSSI()) + F(" dBm"));
  appendRow(page, F("Час з NTP"), haveRealTime() ? F("отримано") : F("ще ні"));
  appendRow(page, F("Часовий пояс"), String(settings.tz));
  appendRow(page, F("Оновлення по WiFi"),
            !otaReady          ? F("вимкнено")
            : otaPasswordIsDefault() ? F("увімкнено, пароль типовий")
                                     : F("увімкнено, пароль власний"));

  page += F("<h2>Хмара</h2>");
  appendRow(page, F("Сервер"), String(CloudHost));
  appendRow(page, F("Прив'язка"), settings.token[0] ? F("є") : F("немає"));

  if (enrollPending) {
    appendRow(page, F("Остання спроба"), enrollWaitingFor());
  } else if (enrollResult != NULL) {
    String result(enrollResult);
    if (enrollHttpStatus != 0 && enrollHttpStatus != 200) {
      result += String(F(" (HTTP ")) + enrollHttpStatus + F(")");
    }
    appendRow(page, F("Остання спроба"), result);
  }

  appendRow(page, F("Пам'ять (вільно / блок)"), heapSummary());

  page += F("<h2>Лінія до Nano</h2>");
  appendRow(page, F("Прийнято кадрів"), String(linkFramesGood));
  appendRow(page, F("Відкинуто"), String(linkFramesDropped));
  appendRow(page, F("Відхилено годинником"), String(linkFramesRejected));
  appendRow(page, F("Аптайм ESP"), String(millis() / 1000) + F(" с"));

  page += F("<h2>Тиша на лінії</h2>");

  if (linkQuiet()) {
    appendRow(page, F("Стан"),
              String(F("мовчимо ще ")) + quietSecondsLeft() + F(" с"));
  } else {
    appendRow(page, F("Стан"), F("лінія працює"));
  }

  page += F("<form method=post action='/quiet'>"
            "<p style='color:#9aa0a6;font-size:.85rem'>Натисніть перед тим, як "
            "запускати заливку, і одразу запускайте її.</p>"
            "<button name=target value=esp>Шию Nano — ESP мовчить 30 с</button> "
            "<button name=target value=nano>Шию ESP кабелем — годинник мовчить "
            "300 с</button></form>");

  page += F("<p><a href='/settings'>Налаштування</a></p></main>");

  server.send(200, "text/html; charset=utf-8", page);
}

void handleSettings() {
  String page;
  page.reserve(2048);
  page = F("<!doctype html><title>K-Clock — налаштування</title>");
  page += FPSTR(PageStyle);
  page += F("<main><h1>Налаштування</h1><form method=post action='/save'>");

  page += F("<h2>Час</h2>");
  page += F("<label>Часовий пояс (формат TZ)</label>"
            "<input name=tz value='");
  page += escapeAttribute(settings.tz);
  page += F("'>");

  page += F("<label>Виставити час вручну (рівно РРРР-ММ-ДД ГГ:ХХ:СС, "
            "порожнє — не чіпати)</label><input name=manual placeholder='");
  page += escapeAttribute(clockStatus.valid ? clockStatus.stamp : "2026-09-21 14:03:22");
  page += F("'>");

  page += F("<h2>Оновлення по WiFi</h2>");
  page += F("<label>Пароль OTA (порожнє — не змінювати");
  page += otaPasswordIsDefault() ? F("; зараз типовий — його видно у прошивці)</label>")
                                 : F("; зараз власний)</label>");
  page += F("<input name=ota type=password autocomplete=new-password>");
  page += F("<p style='color:#9aa0a6;font-size:.85rem'>Зміна пароля перезавантажує "
            "модуль — бібліотека приймає його лише під час запуску.</p>");

  // The OTA password's convention exactly: a password box, never filled back
  // in, blank meaning leave alone.
  page += F("<h2>Хмара</h2>");
  page += F("<label>Код прив'язки з акаунта (порожнє — не змінювати; ");
  page += settings.token[0] ? F("зараз прив'язано)</label>") : F("зараз не прив'язано)</label>");
  page += F("<input name=code type=password autocomplete=off>");
  page += F("<p style='color:#9aa0a6;font-size:.85rem'>Код живе 15 хвилин. Нова "
            "прив'язка забирає годинник у попереднього власника.</p>");

  page += F("<h2>Годинник</h2>");

  if (!clockConfig.valid) {
    page += F("<div class=warn>Поточні значення з годинника ще не отримані — "
              "збереження перезапише їх тим, що тут введено.</div>");
  }

  page += F("<label>Притлумлювати з години (0…24)</label><input name=dimfrom value='");
  page += String(clockConfig.valid ? clockConfig.dimFrom : 22);
  page += F("'>");

  page += F("<label>Притлумлювати до години (0…24)</label><input name=dimuntil value='");
  page += String(clockConfig.valid ? clockConfig.dimUntil : 7);
  page += F("'>");

  page += F("<p style='color:#9aa0a6;font-size:.85rem'>Кінець не включається: "
            "22…7 гасить з 22:00 до 06:59. <b>0…24</b> — цілодобово, "
            "однакові значення — ніколи.</p>");

  page += F("<label>Період біжучого рядка, хвилин (0 — вимкнути)</label>"
            "<input name=marquee value='");
  page += String(clockConfig.valid ? clockConfig.marquee : 5);
  page += F("'>");

  page += F("<button type=submit>Зберегти</button></form>"
            "<p><a href='/'>Назад</a></p></main>");

  server.send(200, "text/html; charset=utf-8", page);
}

// Everything here is checked before it goes on the wire rather than left to the
// Nano, because the Nano's only way to complain is a bare <E that nothing on
// this side can attribute to a particular field.
static void sendConfig(const char *key, long low, long high) {
  if (!server.hasArg(key)) {
    return;
  }

  String value = server.arg(key);
  value.trim();

  // toInt() answers zero for anything it cannot read, and zero is a legal hour,
  // so it is the digits that have to be checked and not the result.
  if (value.length() == 0) {
    return;
  }

  for (unsigned int i = 0; i < value.length(); i++) {
    if (!isDigit(value[i])) {
      return;
    }
  }

  long number = value.toInt();
  if (number < low || number > high) {
    return;
  }

  char frame[32];
  snprintf(frame, sizeof(frame), ">C %s %ld", key, number);
  linkSend(frame);
  delay(50);
}

// Exactly "2026-09-21 14:03:22" and nothing else. Strict because the Nano's
// parser reads fixed offsets and would take "2026-9-21" apart wrongly, and
// because a value carrying a newline would otherwise split one frame into two
// on the wire.
static bool validTimestamp(const String &value) {
  if (value.length() != 19) {
    return false;
  }

  const char *p = value.c_str();
  if (p[4] != '-' || p[7] != '-' || p[10] != ' ' || p[13] != ':' || p[16] != ':') {
    return false;
  }

  for (int i = 0; i < 19; i++) {
    if (i == 4 || i == 7 || i == 10 || i == 13 || i == 16) {
      continue;
    }
    if (!isDigit(p[i])) {
      return false;
    }
  }

  return true;
}


void handleSave() {
  bool settingsChanged = false;

  String tz = server.arg("tz");
  if (tz.length() > 0 && tz.length() < sizeof(settings.tz) && tz != settings.tz) {
    strncpy(settings.tz, tz.c_str(), sizeof(settings.tz) - 1);
    settings.tz[sizeof(settings.tz) - 1] = '\0';
    settingsChanged = true;
    startTimeSync();
  }

  // An empty box leaves the stored password alone, which is what lets the form
  // be submitted for any other reason without wiping it.
  bool restartNeeded = false;
  String ota = server.arg("ota");
  if (ota.length() > 0 && ota.length() < sizeof(settings.otaPassword)) {
    strncpy(settings.otaPassword, ota.c_str(), sizeof(settings.otaPassword) - 1);
    settings.otaPassword[sizeof(settings.otaPassword) - 1] = '\0';
    settingsChanged = true;

    // ArduinoOTA::setPassword only does anything before begin() - afterwards it
    // returns without storing, so calling startOta() again would look like it
    // worked and leave the old password in force. A restart is the only honest
    // way to apply a new one.
    restartNeeded = true;
  }

  if (settingsChanged) {
    saveSettings();
  }

  // Only queued here. The exchange takes a TLS handshake, and the browser is
  // owed its redirect first; loop() runs it once the module is able to.
  String code = server.arg("code");
  code.trim();
  if (code.length() > 0) {
    if (validEnrollCode(code)) {
      strncpy(enrollCode, code.c_str(), sizeof(enrollCode) - 1);
      enrollCode[sizeof(enrollCode) - 1] = '\0';
      enrollPending = true;
    } else {
      enrollPending = false;
      enrollHttpStatus = 0;
      enrollResult = F("код має вигляд XXXX-XXXX");
    }
  }

  // Spaced out on purpose - each sendConfig ends in a short delay. Three frames
  // back to back are around sixty bytes, which is the whole of the Nano's
  // hardware receive buffer, and it only drains between passes of loop().
  // Both ends go to 24, not 23. They are boundaries on a 0..24 line rather than
  // hours of the day - dimmedAt() treats the end as half open - and 24 is what
  // makes "dim around the clock" expressible at all.
  sendConfig("dimfrom", 0, 24);
  sendConfig("dimuntil", 0, 24);
  sendConfig("marquee", 0, 60);

  String manual = server.arg("manual");
  manual.trim();
  if (validTimestamp(manual)) {
    char frame[40];
    snprintf(frame, sizeof(frame), ">T %s", manual.c_str());
    linkSend(frame);
  }

  // Ask for the config straight back rather than trusting the write.
  clockConfig.valid = false;
  configPollTime = millis() - ConfigPollMs;

  server.sendHeader("Location", "/");
  server.send(303);

  // After the answer is on its way, not before, or the browser is left waiting
  // on a socket that the reboot takes away.
  if (restartNeeded) {
    delay(200);
    ESP.restart();
  }
}

// The two sides of one problem. Flashing the Nano over the cable puts the USB
// bridge on D0, where the ESP is already transmitting, and the contention
// corrupts the image - that is what the jumper in the ESP's TX line is for, and
// this is the way to avoid reaching for it. Flashing the ESP over the cable is
// the mirror image: the clock's D1 fights the bridge for the ESP's RX, and
// there the clock is the one that has to hold its tongue, so it is told with a
// ">M <seconds>" frame while the ESP can still send one.
//
// Neither side needs cancelling. Both are timers, and both expire on their own.
void handleQuiet() {
  String target = server.arg("target");

  if (target == "nano") {
    // Lift our own silence first, or the one frame that matters would be the
    // one thing the gate below swallows.
    quietMs = 0;

    char frame[16];
    snprintf(frame, sizeof(frame), ">M %lu", QuietMaxSeconds / 2);
    linkSend(frame);
    // Out of the buffer before the browser is answered, since the next thing
    // that happens is somebody pulling power off the module.
    Serial.flush();
  } else {
    quietStart = millis();
    quietMs = QuietDefaultSeconds * 1000UL;
  }

  server.sendHeader("Location", "/");
  server.send(303);
}

// --------------------------------------------------------------- discovery --

// Finding the page on the home network used to mean opening the router and
// reading its DHCP lease table, because the module announced itself nowhere.
// It looked as though k-clock.local worked, and sometimes it did - but only by
// accident: ArduinoOTA::begin() calls MDNS.begin() for its own sake, so the
// name existed exactly as long as an OTA password did, and advertised
// _arduino._tcp rather than a web server. This makes it deliberate instead.
//
// Three protocols, because no single one covers every client:
//
//   mDNS     k-clock.local - iOS, macOS, Windows 10+, Android 12+, Linux with
//            avahi. The addService call is what also lists the clock among the
//            HTTP servers a network browser shows.
//   LLMNR    a bare k-clock from Windows, which asks for it before DNS.
//   NetBIOS  the same bare name for anything older that still speaks it.
//
// None of them reaches an Android older than 12; there the router's lease
// table, under this same name, is still the answer.
void startDiscovery() {
  // Harmless if ArduinoOTA has already brought the responder up - begin() only
  // sets the hostname and restarts it, and it does not drop services that were
  // registered beforehand. Ordering it after startOta() keeps the http service
  // on the near side of that restart either way.
  MDNS.begin(HostName);
  MDNS.addService("http", "tcp", 80);

  LLMNR.begin(HostName);
  NBNS.begin(HostName);
}

// --------------------------------------------------------------------- ota --

// Over the air updates fit only while the image stays under 512000 bytes: the
// running image and the incoming one have to be in flash at once, and with
// TLS linked in this one is around 499 KB. build.sh enforces the ceiling and
// explains the options that keep it there. Past it, OTA stops working and the
// only way back in is the cable and the GPIO0 dance.
//
// The portal is the safety net for the other failure. An image that cannot
// join the network still raises K-Clock and stays reachable; only one that
// crashes before reaching that line needs opening the case.
void startOta() {
  otaReady = false;

  // Unreachable as things stand - loadSettings() never leaves this empty - and
  // kept because ArduinoOTA accepts an empty password as "no password at all"
  // rather than refusing, which would quietly open the module to the network.
  if (settings.otaPassword[0] == '\0') {
    return;
  }

  ArduinoOTA.setHostname(HostName);
  ArduinoOTA.setPassword(settings.otaPassword);

  // The Nano is not told anything. It keeps running its own firmware, its
  // polls go unanswered for the few seconds the write takes, and the link
  // picks up again by itself afterwards.
  ArduinoOTA.onStart([]() { otaActive = true; });
  ArduinoOTA.onEnd([]() { otaActive = false; });
  ArduinoOTA.onError([](ota_error_t error) { (void)error; otaActive = false; });

  ArduinoOTA.begin();
  otaReady = true;
}

// ------------------------------------------------------------------- setup --

void setStatusLed(bool lit) {
#if !DEBUG_ON_SERIAL1
  if (lit == ledLit) {
    return;
  }
  digitalWrite(StatusLedPin, lit ? LOW : HIGH);
#endif
  ledLit = lit;
}

void portalRaised(WiFiManager *manager) {
  DBG(F("Portal up"));
  (void)manager;
}

void setup() {
  Serial.begin(LinkBaud);
#if DEBUG_ON_SERIAL1
  Serial1.begin(115200);
  DBG(F("K-Clock ESP-01"));
#else
  // GPIO2 doubles as a boot mode strap and has to read high while the module
  // comes up, so it only becomes an output once we are past that.
  pinMode(StatusLedPin, OUTPUT);
  digitalWrite(StatusLedPin, HIGH);
#endif

  loadSettings();

  WiFi.mode(WIFI_STA);
  WiFi.hostname(HostName);

#if FORGET_WIFI
  wm.resetSettings();
#endif

  // WiFiManager logs to Serial, and Serial here is the wire to the Nano. The
  // clock drops those lines harmlessly - they do not begin with '>' - but there
  // is no reason to put a few hundred bytes of chatter on a shared link at
  // every boot. The library offers no way to send them somewhere else: the
  // 2.0.17 overloads only change the prefix and the level.
  wm.setDebugOutput(false);

  wm.setTitle(ApName);
  wm.setAPCallback(portalRaised);

  // With nothing saved there is nothing to retry, so the portal stays up until
  // somebody configures it. With a network saved it times out and loop() tries
  // that network again - see PortalRetrySeconds. A replaced router is still
  // recovered the same way: the portal keeps coming back between attempts, and
  // a phone that joins it holds it open.
  wm.setConfigPortalTimeout(wm.getWiFiIsSaved() ? PortalRetrySeconds : 0);
  wm.setAPClientCheck(true);
  wm.setConnectTimeout(ConnectTimeoutSeconds);

  // Blocking autoConnect() never returns while the portal is open, which would
  // freeze both the status LED and the link to the Nano.
  wm.setConfigPortalBlocking(false);

  wm.autoConnect(ApName);

  server.on("/", handleRoot);
  server.on("/settings", handleSettings);
  server.on("/save", HTTP_POST, handleSave);
  server.on("/quiet", HTTP_POST, handleQuiet);
}

void loop() {
  wm.process();

  updateLink();

  unsigned long now = millis();
  bool online = WiFi.status() == WL_CONNECTED;

  // Only until the first connection. After that the SDK's own auto-reconnect
  // carries the module through a router restart, as it always has, and going
  // through autoConnect would put the portal up for every brief drop.
  if (!serverStarted && !online && !wm.getConfigPortalActive()) {
    wm.autoConnect(ApName);
    online = WiFi.status() == WL_CONNECTED;
    now = millis();
  }

  if (online) {
    setStatusLed(false);
  } else if (now - blinkTime >= PortalBlinkMs) {
    blinkTime = now;
    setStatusLed(!ledLit);
  }

  if (online && !serverStarted) {
    server.begin();
    serverStarted = true;
    startTimeSync();
    startOta();
    startDiscovery();
    DBG(WiFi.localIP());
  }

  if (otaReady) {
    ArduinoOTA.handle();
  }

  // An image is being written to flash. Nothing else is worth doing, and the
  // link in particular must not be fed frames whose replies nobody will read.
  if (otaActive) {
    return;
  }

  if (serverStarted) {
    MDNS.update();
    server.handleClient();
  }

  if (online && !timeSynced && haveRealTime()) {
    timeSynced = true;
  }

  // After handleClient(), so the save that queued the code has already had its
  // redirect; the page it lands on waits out the handshake and shows the result.
  if (enrollPending && online && haveRealTime() && linkAlive()) {
    cloudEnroll();
  }

  // The first push happens as soon as there is a real time to push; after that
  // once an hour is far more than a DS3231 needs, it only stops the drift
  // accumulating over months. The retry is the corrective half: it catches a
  // frame the clock refused, and a change of offset the hourly push would sit
  // on until it came round.
  if (timeSynced) {
    bool due = !timePushed || now - timePushTime >= TimePushMs;
    bool drifted = now - timePushTime >= TimeRetryMs && clockDisagrees();

    if (due || drifted) {
      pushTime();
    }
  }

  if (now - statusPollTime >= StatusPollMs) {
    statusPollTime = now;
    linkSend(">Q");
  }

  if (now - configPollTime >= ConfigPollMs || (!clockConfig.valid && now - configPollTime >= StatusPollMs)) {
    configPollTime = now;
    linkSend(">G");
  }
}
