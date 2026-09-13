// Board variant. Set to 0 for a board fitted with a BMP280, which measures
// temperature and pressure but has no humidity sensor. Guarded so a build can
// override it, e.g. arduino-cli --build-property
// compiler.cpp.extra_flags=-DHAS_HUMIDITY=0
#ifndef HAS_HUMIDITY
#define HAS_HUMIDITY 1
#endif

#include <DS3231.h>
#include <Adafruit_Sensor.h>
#if HAS_HUMIDITY
#include <Adafruit_BME280.h>
#else
#include <Adafruit_BMP280.h>
#endif
#include <Wire.h>
#include <avr/pgmspace.h>

#define ButtonPin 2
#define LatchPin 4
#define ClockPin 3
#define AnodeDataPin 5
#define CatodeDataPin 7
#define CatodeClockPin 6

#define RENDER_LINE_COUNT 8

#if HAS_HUMIDITY
#define MARQUEE_PAGE_COUNT 5
#else
#define MARQUEE_PAGE_COUNT 4
#endif
#define MARQUEE_PAGE_BYTES 3
#define MARQUEE_STRIP_BYTES (MARQUEE_PAGE_COUNT * MARQUEE_PAGE_BYTES)
#define MARQUEE_PAGE_COLUMNS 24
#define MARQUEE_TOTAL_STEPS ((MARQUEE_PAGE_COUNT - 1) * MARQUEE_PAGE_COLUMNS)
#define MARQUEE_STEP_MS 80
#define MARQUEE_HOLD_MS 5000
#define MARQUEE_PERIOD_MINUTES 5

// Every row gets the same slot and is blanked at the end of it, so brightness
// stops depending on how long loop() happened to take. Dimming is simply a
// shorter lit fraction of that same slot, which leaves the frame rate alone.
#define ROW_PERIOD_US 500
#define ROW_ON_BRIGHT_US 450
#define ROW_ON_DIM_US 60
#define DIM_FROM_HOUR 22
#define DIM_UNTIL_HOUR 7

#define CLOCK_POLL_MS 100
#define SENSOR_POLL_MS 1000

// Twelve samples a quarter of an hour apart give the three hour window that
// pressure trends are conventionally read over.
#define PRESSURE_HISTORY 12
#define PRESSURE_SAMPLE_MS 900000UL
#define PRESSURE_TREND_MMHG 1

// One click steps through the screens. Holding does something only on the clock
// screen, where it opens time programming, and inside programming, where it moves
// to the next field. The same three seconds either way.
// The button is wired active HIGH. D2 was measured idling LOW in 100% of samples
// with the internal pull-up enabled and nothing pressed, and with zero edges, so
// an external pull-down holds the line and pressing lifts it. Reading it the other
// way round makes the firmware believe the button is held from the moment it boots.
#define BUTTON_PRESSED_LEVEL HIGH

#define BUTTON_DEBOUNCE_MS 25
#define BUTTON_HOLD_MS 3000

// A sensor screen returns to the clock once the button has been left alone this
// long. Programming is exempt: it is a deliberate mode the user is standing in
// front of, and dropping out of it mid-edit would be worse than waiting.
#define IDLE_RETURN_MS 30000

#if HAS_HUMIDITY
#define DISPLAY_MODE_COUNT 4
#else
#define DISPLAY_MODE_COUNT 3
#endif

const int times = 0;
const int temperature = 1;
const int pressure = 2;
#if HAS_HUMIDITY
const int humidity = 3;
#endif
const int altitude = 4;
const int marquee = 5;
const int settings = 100;
const int minuteMinorSettings = 101;
const int minuteMajorSettings = 102;
const int hourMinorSettings = 103;
const int hourMajorSettings = 104;
const int endSettings = 105;

const int colon = 10;
const int degree = 11;
const int celsius = 12;
const int pressureSymbol= 13;
const int nullNumber = 14;
const int percent = 15;
const int trendUp = 16;
const int trendDown = 17;
const int trendSteady = 18;

void visual();
void timeArrayFilling(int hour1 = nullNumber, int hour2 = nullNumber, int minute1 = nullNumber, int minute2 = nullNumber, int separator = colon);
void pressureArrayFiling(int pressure1, int pressure2, int pressure3, int trend = nullNumber);
void temperatureArrayFiling(int temperature1, int temperature2);
int concatenateInt(int major, int minor);
void updateButton(unsigned long now);
void buttonClick();
void buttonHold();
void readSensors();
void recordPressureSample();
int pressureTrendGlyph();
#if HAS_HUMIDITY
void humidityArrayFiling(int humidity1, int humidity2);
#endif
void startMarquee();
void marqueeCapturePage(int page);
void marqueeRender();

DS3231 clk;
#if HAS_HUMIDITY
Adafruit_BME280 bme;
#else
Adafruit_BMP280 bme;
#endif
RTCDateTime dt;

uint16_t rowOnMicros = ROW_ON_BRIGHT_US;

float sensorTemperature;
float sensorPressure;
#if HAS_HUMIDITY
float sensorHumidity;
#endif
unsigned long sensorReadTime;
unsigned long clockReadTime;

int pressureHistory[PRESSURE_HISTORY];
uint8_t pressureHistoryCount;
uint8_t pressureHistoryHead;
unsigned long pressureSampleTime;

void setup() {
  pinMode(LatchPin, OUTPUT);
  pinMode(ClockPin, OUTPUT);
  pinMode(AnodeDataPin, OUTPUT);
  pinMode(CatodeClockPin, OUTPUT);
  pinMode(CatodeDataPin, OUTPUT);
  pinMode(ButtonPin, INPUT_PULLUP);
  
  clk.begin();
  //clk.setDateTime(__DATE__, __TIME__);

  unsigned status;
  status = bme.begin(0x76);
  
  Serial.begin(9600);
  Serial.println("Initialized");
  
  readSensors();
  recordPressureSample();
  sensorReadTime = millis();
  pressureSampleTime = millis();
}

const uint8_t catode[8] PROGMEM = {2, 4, 8, 16, 32, 64, 128, 1};

uint8_t out[8][3] = {};

const uint8_t numeric[][8] PROGMEM = {
  {0x0F,0x09,0x09,0x09,0x09,0x09,0x0F,0x00},                  //0
  {0x02,0x06,0x0A,0x02,0x02,0x02,0x0F,0x00},                  //1
  {0x0F,0x01,0x01,0x0F,0x08,0x08,0x0F,0x00},                  //2
  {0x0F,0x01,0x01,0x0F,0x01,0x01,0x0F,0x00},                  //3
  {0x09,0x09,0x09,0x0F,0x01,0x01,0x01,0x00},                  //4
  {0x0F,0x08,0x08,0x0F,0x01,0x01,0x0F,0x00},                  //5
  {0x0F,0x08,0x08,0x0F,0x09,0x09,0x0F,0x00},                  //6
  {0x0F,0x01,0x02,0x04,0x04,0x04,0x04,0x00},                  //7
  {0x0F,0x09,0x09,0x0F,0x09,0x09,0x0F,0x00},                  //8
  {0x0F,0x09,0x09,0x0F,0x01,0x01,0x0F,0x00},                  //9
  {0x00,0x00,0x03,0x00,0x03,0x00,0x00,0x00},                  //=
  {0x07,0x05,0x07,0x00,0x00,0x00,0x00,0x00},                  //o
  {0x00,0x07,0x04,0x04,0x04,0x04,0x07,0x00},                  //c
  {0x0F,0x09,0x09,0x0F,0x08,0x08,0x08,0x00},                  //p
  {0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00},                  //null
  {0x71,0x52,0x74,0x08,0x17,0x25,0x47,0x00},                  //%
  {0x02,0x07,0x02,0x02,0x02,0x02,0x02,0x00},                  //trend up
  {0x02,0x02,0x02,0x02,0x02,0x07,0x02,0x00},                  //trend down
  {0x00,0x00,0x00,0x07,0x00,0x00,0x00,0x00},                  //trend steady
};

static inline uint8_t glyph(int index, int row){
  return pgm_read_byte(&numeric[index][row]);
}

long blinkTimeSettings;
int state = 0;

bool buttonRaw;
bool buttonStable;
bool buttonHoldFired;
unsigned long buttonEdgeTime;
unsigned long buttonPressTime;
unsigned long buttonActivityTime;

int minute_minore;
int minute_major;

int hour_minore;
int hour_major;

uint8_t marqueeStrip[8][MARQUEE_STRIP_BYTES];
int marqueeStep;
unsigned long marqueeStepTime;
int lastMarqueeMinute = -1;

void loop() {
  unsigned long now = millis();
  
  updateButton(now);
  
  // The marquee is left alone as well: it runs on its own clock and already ends
  // on the clock screen, so timing it out would only truncate it.
  if(state > times && state < settings && state != marquee && now - buttonActivityTime >= IDLE_RETURN_MS){
    state = times;
  }
  
  // The sensor moves far slower than the display refreshes, and reading it in
  // every pass was the main reason the sensor screens looked dimmer than the
  // clock. Once a second is more than the readings are worth.
  if(now - sensorReadTime >= SENSOR_POLL_MS){
    sensorReadTime = now;
    readSensors();
  }
  
  if(now - pressureSampleTime >= PRESSURE_SAMPLE_MS){
    pressureSampleTime = now;
    recordPressureSample();
  }
  
  if(state < settings && state != marquee && now - clockReadTime >= CLOCK_POLL_MS){
    clockReadTime = now;
    dt = clk.getDateTime();
    rowOnMicros = (dt.hour >= DIM_FROM_HOUR || dt.hour < DIM_UNTIL_HOUR) ? ROW_ON_DIM_US : ROW_ON_BRIGHT_US;
    
    if(dt.minute % MARQUEE_PERIOD_MINUTES == 0 && dt.minute != lastMarqueeMinute){
      lastMarqueeMinute = dt.minute;
      startMarquee();
    }
  }
  
  switch(state){
    case times:
    {
      minute_minore = dt.minute % 10;
      minute_major = dt.minute / 10;
      
      hour_minore = dt.hour % 10;
      hour_major = dt.hour / 10;
      
      timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore,
                       (dt.second & 1) ? colon : nullNumber);
    }
    break;
    case temperature:
    {
      int temperature = int(sensorTemperature);
      
      int temperature_minore = temperature % 10;
      int temperature_major = temperature / 10;
      
      temperatureArrayFiling(temperature_major,temperature_minore);
    }
    break;
    case pressure:
    {
      int pressure = int(sensorPressure);
      
      int pressure1 = pressure / 100;
      int pressure2 = pressure % 100 / 10;
      int pressure3 = pressure % 10;
      
      pressureArrayFiling(pressure1, pressure2, pressure3, pressureTrendGlyph());
    }
    break;
#if HAS_HUMIDITY
    case humidity:
    {
      int humidity = int(sensorHumidity);
      
      int humidity_minore = humidity % 10;
      int humidity_major = humidity / 10;
      
      humidityArrayFiling(humidity_major, humidity_minore);
    }
    break;
#endif
    case marquee:
    {
      // The last page is the clock again, so hand straight back to `times` on arrival
      // instead of dwelling on a copy of the time captured half a minute ago.
      if(marqueeStep >= MARQUEE_TOTAL_STEPS){
        state = times;
        break;
      }
      
      // A step that has just brought a whole screen into view dwells before moving on.
      unsigned long interval = MARQUEE_STEP_MS;
      
      if(marqueeStep > 0 && marqueeStep % MARQUEE_PAGE_COLUMNS == 0){
        interval = MARQUEE_HOLD_MS;
      }
      
      if(millis() - marqueeStepTime >= interval){
        marqueeStepTime = millis();
        marqueeStep++;
      }
      
      marqueeRender();
    }
    break;
    case settings:
    {
      dt = clk.getDateTime();
      
      minute_minore = dt.minute % 10;
      minute_major = dt.minute / 10;
      
      hour_minore = dt.hour % 10;
      hour_major = dt.hour / 10;
      
      state++;
    }
    break;
    case minuteMinorSettings:
    {
      long difference = millis() - blinkTimeSettings;
      
      if(difference > 1000){
        blinkTimeSettings = millis();
        break;
      }
      if(difference > 500 ){
        timeArrayFilling(hour_major,hour_minore,minute_major,nullNumber);
        break;
      }
      if(difference > 0){
        timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore);
        break;;
      }
    }break;
    case minuteMajorSettings:
    {
      long difference = millis() - blinkTimeSettings;
      
      if(difference > 1000){
        blinkTimeSettings = millis();
        break;
      }
      if(difference > 500 ){
        timeArrayFilling(hour_major,hour_minore,nullNumber,minute_minore);
        break;
      }
      if(difference > 0){
        timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore);
        break;
      }
    }break;
    case hourMinorSettings:
    {
      long difference = millis() - blinkTimeSettings;
      
      if(difference > 1000){
        blinkTimeSettings = millis();
        break;
      }
      if(difference > 500 ){
        timeArrayFilling(hour_major,nullNumber,minute_major,minute_minore);
        break;
      }
      if(difference > 0){
        timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore);
        break;;
      }
    }break;
    case hourMajorSettings:
    {
      long difference = millis() - blinkTimeSettings;
      
      if(difference > 1000){
        blinkTimeSettings = millis();
        break;
      }
      if(difference > 500 ){
        timeArrayFilling(nullNumber,hour_minore,minute_major,minute_minore);
        break;
      }
      if(difference > 0){
        timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore);
        break;;
      }
    }break;
    case endSettings:
    {
      clk.setDateTime(dt.year, dt.month, dt.day, concatenateInt(hour_major, hour_minore), concatenateInt(minute_major,minute_minore), 0);
      state ++;
    }
    default:
    state = 0;
    break;
  }
  
  visual();
}

// All five display pins sit on PORTD, so the registers can be clocked with
// single cycle sbi/cbi instead of digitalWrite. Only our own bits are touched,
// which leaves the serial pins and the button pull-up on PD2 alone.
#define LATCH_BIT   (1 << LatchPin)
#define CLOCK_BIT   (1 << ClockPin)
#define ANODE_BIT   (1 << AnodeDataPin)
#define CAT_CLK_BIT (1 << CatodeClockPin)
#define CAT_DAT_BIT (1 << CatodeDataPin)

static void shiftCatode(uint8_t value){
  for(uint8_t bit = 0; bit < 8; bit++){
    if(value & 1){ PORTD |= CAT_DAT_BIT; } else { PORTD &= ~CAT_DAT_BIT; }
    PORTD |= CAT_CLK_BIT;
    PORTD &= ~CAT_CLK_BIT;
    value >>= 1;
  }
}

static void shiftAnode(uint8_t value){
  for(uint8_t bit = 0; bit < 8; bit++){
    if(value & 0x80){ PORTD |= ANODE_BIT; } else { PORTD &= ~ANODE_BIT; }
    PORTD |= CLOCK_BIT;
    PORTD &= ~CLOCK_BIT;
    value <<= 1;
  }
}

static void latchRow(uint8_t byte0, uint8_t byte1, uint8_t byte2, uint8_t row){
  PORTD &= ~LATCH_BIT;
  shiftCatode(byte0);
  shiftCatode(byte1);
  shiftCatode(byte2);
  shiftAnode(row);
  PORTD |= LATCH_BIT;
}

// A row used to stay lit until the next one was latched, so the last row of a
// frame also burned through everything loop() did afterwards - the bottom line
// was brighter, and by an amount that varied with the screen being shown. Now
// every row gets an identical slot and is blanked at the end of it.
void visual(){
  for(uint8_t i = 0; i < 8; i++){
    latchRow(out[i][0], out[i][1], out[i][2], pgm_read_byte(&catode[i]));
    delayMicroseconds(rowOnMicros);
    latchRow(0, 0, 0, 0);
    delayMicroseconds(ROW_PERIOD_US - rowOnMicros);
  }
}

void timeArrayFilling(int hour1, int hour2, int minute1, int minute2, int separator){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (glyph(minute2,i)<<1) + (glyph(minute1,i)<<6);
    out[i][1] = (glyph(hour2,i)<<6) + (glyph(minute1,i)>>2) + (glyph(separator,i)<<3);
    out[i][2] = (glyph(hour1,i)<<3) + (glyph(hour2,i)>>2);
  }
}

void temperatureArrayFiling(int temperature1, int temperature2){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (glyph(celsius,i)<<1) + (glyph(degree,i)<<5);
    out[i][1] = (glyph(temperature1,i)<<6) + (glyph(temperature2,i)<<1);
    out[i][2] = (glyph(temperature1,i)>>2);
  }
}

void pressureArrayFiling(int pressure1, int pressure2, int pressure3, int trend){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (glyph(pressureSymbol,i)<<1) + (glyph(pressure3,i)<<6);
    out[i][1] = (glyph(pressure2,i)<<3) + (glyph(pressure3,i)>>2);
    out[i][2] = glyph(pressure1,i) + (glyph(trend,i)<<4);
  }
}

#if HAS_HUMIDITY
void humidityArrayFiling(int humidity1, int humidity2){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (glyph(percent,i)<<1);
    out[i][1] = (glyph(humidity1,i)<<6) + (glyph(humidity2,i)<<1);
    out[i][2] = (glyph(humidity1,i)>>2);
  }
}
#endif

void readSensors(){
  sensorTemperature = bme.readTemperature();
  sensorPressure = bme.readPressure() / 133.0F;
#if HAS_HUMIDITY
  sensorHumidity = bme.readHumidity();
#endif
}

void recordPressureSample(){
  pressureHistory[pressureHistoryHead] = int(sensorPressure);
  pressureHistoryHead = (pressureHistoryHead + 1) % PRESSURE_HISTORY;
  
  if(pressureHistoryCount < PRESSURE_HISTORY){
    pressureHistoryCount++;
  }
}

// Compares against the oldest sample held, so the arrow appears after the first
// quarter of an hour and widens to the full three hour window as the ring fills.
int pressureTrendGlyph(){
  if(pressureHistoryCount < 2){
    return nullNumber;
  }
  
  int oldest = pressureHistory[pressureHistoryCount < PRESSURE_HISTORY ? 0 : pressureHistoryHead];
  int delta = int(sensorPressure) - oldest;
  
  if(delta >= PRESSURE_TREND_MMHG){
    return trendUp;
  }
  if(delta <= -PRESSURE_TREND_MMHG){
    return trendDown;
  }
  return trendSteady;
}

void startMarquee(){
  int hour1 = dt.hour / 10;
  int hour2 = dt.hour % 10;
  int minute1 = dt.minute / 10;
  int minute2 = dt.minute % 10;
  
  int page = 0;
  
  timeArrayFilling(hour1, hour2, minute1, minute2);
  marqueeCapturePage(page++);
  
  int temperatureNow = int(sensorTemperature);
  temperatureArrayFiling(temperatureNow / 10, temperatureNow % 10);
  marqueeCapturePage(page++);
  
  int pressureNow = int(sensorPressure);
  pressureArrayFiling(pressureNow / 100, pressureNow % 100 / 10, pressureNow % 10, pressureTrendGlyph());
  marqueeCapturePage(page++);
  
#if HAS_HUMIDITY
  int humidityNow = int(sensorHumidity);
  humidityArrayFiling(humidityNow / 10, humidityNow % 10);
  marqueeCapturePage(page++);
#endif
  
  timeArrayFilling(hour1, hour2, minute1, minute2);
  marqueeCapturePage(page++);
  
  marqueeStep = 0;
  marqueeStepTime = millis();
  state = marquee;
}

// Page 0 is shown first, so it sits at the high (left) end of the strip.
void marqueeCapturePage(int page){
  int base = MARQUEE_PAGE_BYTES * (MARQUEE_PAGE_COUNT - 1 - page);
  
  for (int i = 0; i<8 ; i++)
  {
    marqueeStrip[i][base + 0] = out[i][0];
    marqueeStrip[i][base + 1] = out[i][1];
    marqueeStrip[i][base + 2] = out[i][2];
  }
}

// Copy the visible 24 column window out of the strip, one column further right each step.
void marqueeRender(){
  int shift = MARQUEE_TOTAL_STEPS - marqueeStep;
  int byteIndex = shift / 8;
  int bitOffset = shift % 8;
  
  for (int i = 0; i<8 ; i++)
  {
    for (int b = 0; b<MARQUEE_PAGE_BYTES ; b++)
    {
      int value = marqueeStrip[i][byteIndex + b] >> bitOffset;
      
      if(bitOffset){
        value |= marqueeStrip[i][byteIndex + b + 1] << (8 - bitOffset);
      }
      
      out[i][b] = value & 0xFF;
    }
  }
}

int concatenateInt(int major, int minor){
  return (major*10) + minor;
}

// Polled rather than interrupt driven. loop() now runs on a fixed ~4 ms cadence,
// which is ample for a button, and it keeps press timing out of an ISR entirely.
// The old handler did measure press duration correctly, but had no debounce: the
// chatter on release produced several falling edges a few milliseconds apart, and
// each one was taken for another short press, so the screens jumped in bursts.
void updateButton(unsigned long now){
  bool pressed = (digitalRead(ButtonPin) == BUTTON_PRESSED_LEVEL);
  
  if(pressed != buttonRaw){
    buttonRaw = pressed;
    buttonEdgeTime = now;
  }
  else if(pressed != buttonStable && now - buttonEdgeTime >= BUTTON_DEBOUNCE_MS){
    buttonStable = pressed;
    buttonActivityTime = now;
    
    if(pressed){
      buttonPressTime = now;
      buttonHoldFired = false;
    }
    else if(!buttonHoldFired){
      buttonClick();
    }
  }
  
  // A hold that lands on a screen which ignores it still suppresses the click,
  // so holding never quietly turns into a page step on release.
  if(buttonStable && !buttonHoldFired){
    if(now - buttonPressTime >= BUTTON_HOLD_MS){
      buttonHoldFired = true;
      buttonHold();
    }
  }
}

void buttonClick(){
  if(state >= settings){
    switch(state){
      case minuteMinorSettings:
      {
        minute_minore ++;
        if(minute_minore == 10){
          minute_minore = 0;
        }
      }
      break;
      case minuteMajorSettings:
      {
        minute_major++;
        if(minute_major == 6){
          minute_major = 0;
        }
      }
      break;
      case hourMinorSettings:
      {
        hour_minore++;
        if(hour_minore == 10){
          hour_minore = 0;
        }
      }
      break;
      case hourMajorSettings:
      {
        hour_major++;
        if(hour_major == 3){
          hour_major = 0;
        }
      }
      break;
    }
    return;
  }
  
  // Steps the screens, and doubles as the way out of a running marquee.
  state++;
  
  if(state >= DISPLAY_MODE_COUNT){
    state = times;
  }
}

void buttonHold(){
  if(state == times){
    state = settings;
    return;
  }
  
  if(state >= settings){
    state++;
  }
}
