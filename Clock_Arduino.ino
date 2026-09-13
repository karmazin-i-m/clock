#include <DS3231.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BME280.h>
#include <Wire.h>

#define InterruptPin 2
#define LatchPin 4
#define ClockPin 3
#define AnodeDataPin 5
#define CatodeDataPin 7
#define CatodeClockPin 6

#define RENDER_LINE_COUNT 8

#define MARQUEE_PAGE_COUNT 5
#define MARQUEE_PAGE_BYTES 3
#define MARQUEE_STRIP_BYTES (MARQUEE_PAGE_COUNT * MARQUEE_PAGE_BYTES)
#define MARQUEE_PAGE_COLUMNS 24
#define MARQUEE_TOTAL_STEPS ((MARQUEE_PAGE_COUNT - 1) * MARQUEE_PAGE_COLUMNS)
#define MARQUEE_STEP_MS 80
#define MARQUEE_HOLD_MS 5000
#define MARQUEE_PERIOD_MINUTES 5

const int times = 0;
const int temperature = 1;
const int pressure = 2;
const int humidity = 3;
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

void visual();
void timeArrayFilling(int hour1 = nullNumber, int hour2 = nullNumber, int minute1 = nullNumber, int minute2 = nullNumber);
void pressureArrayFiling(int pressure1, int pressure2, int pressure3);
void temperatureArrayFiling(int temperature1, int temperature2);
int concatenateInt(int major, int minor);
void changeState();
void lowInterrupt();
void humidityArrayFiling(int humidity1, int humidity2);
void startMarquee();
void marqueeCapturePage(int page);
void marqueeRender();

DS3231 clk;
Adafruit_BME280 bme;
RTCDateTime dt;

void setup() {
  pinMode(LatchPin, OUTPUT);
  pinMode(ClockPin, OUTPUT);
  pinMode(AnodeDataPin, OUTPUT);
  pinMode(CatodeClockPin, OUTPUT);
  pinMode(CatodeDataPin, OUTPUT);
  pinMode(InterruptPin, INPUT_PULLUP);
  
  clk.begin();
  //clk.setDateTime(__DATE__, __TIME__);

  unsigned status;
  status = bme.begin(0x76);
  
  attachInterrupt(0, changeState, CHANGE);
  
  Serial.begin(9600);
  Serial.println("Initialized");
}

int catode[] = {2, 4, 8, 16, 32, 64, 128, 1};

int out[8][3] = {};

int numeric[][8] = {
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
};

volatile long debounceInterrupt;
long blinkTimeSettings;
volatile bool lockInterrupt = true;
volatile int  state = 0;

int minute_minore;
int minute_major;

int hour_minore;
int hour_major;

uint8_t marqueeStrip[8][MARQUEE_STRIP_BYTES];
int marqueeStep;
unsigned long marqueeStepTime;
int lastMarqueeMinute = -1;

void loop() {
  //Serial.println(state); 
  if(state < settings && state != marquee){
    dt = clk.getDateTime();
    
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
      
      timeArrayFilling(hour_major,hour_minore,minute_major,minute_minore);
    }
    break;
    case temperature:
    {
      int temperature = int(bme.readTemperature());
      
      int temperature_minore = temperature % 10;
      int temperature_major = temperature / 10;
      
      temperatureArrayFiling(temperature_major,temperature_minore);
    }
    break;
    case pressure:
    {
      int pressure = int((bme.readPressure()/133.0F));
      
      int pressure1 = pressure / 100;
      int pressure2 = pressure % 100 / 10;
      int pressure3 = pressure % 10;
      
      pressureArrayFiling(pressure1, pressure2, pressure3);
    }
    break;
    case humidity:
    {
      int humidity = int(bme.readHumidity());
      
      int humidity_minore = humidity % 10;
      int humidity_major = humidity / 10;
      
      humidityArrayFiling(humidity_major, humidity_minore);
      //humidityArrayFiling(0, 0);
    }
    break;
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

void visual(){
  for(int i=0;i<8;i++){
    digitalWrite(LatchPin, LOW);
    shiftOut(CatodeDataPin,CatodeClockPin,LSBFIRST, out[i][0]);
    shiftOut(CatodeDataPin,CatodeClockPin,LSBFIRST, out[i][1]);
    shiftOut(CatodeDataPin,CatodeClockPin,LSBFIRST, out[i][2]);
    shiftOut(AnodeDataPin,ClockPin, MSBFIRST, catode[i]);
    digitalWrite(LatchPin, HIGH);
  }
}

void timeArrayFilling(int hour1, int hour2, int minute1, int minute2){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (numeric[minute2][i]<<1) + (numeric[minute1][i]<<6);
    out[i][1] = (numeric[hour2][i]<<6) + (numeric[minute1][i]>>2) + (numeric[colon][i]<<3);
    out[i][2] = (numeric[hour1][i]<<3) + (numeric[hour2][i]>>2);
  }
}

void temperatureArrayFiling(int temperature1, int temperature2){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (numeric[celsius][i]<<1) + (numeric[degree][i]<<5);
    out[i][1] = (numeric[temperature1][i]<<6) + (numeric[temperature2][i]<<1);
    out[i][2] = (numeric[temperature1][i]>>2);
  }
}

void pressureArrayFiling(int pressure1, int pressure2, int pressure3){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (numeric[pressureSymbol][i]<<1) + (numeric[pressure3][i]<<6);
    out[i][1] = (numeric[pressure2][i]<<3) + (numeric[pressure3][i]>>2);
    out[i][2] = numeric[pressure1][i];
  }
}

void humidityArrayFiling(int humidity1, int humidity2){
  for (int i = 0; i<8 ; i++)
  {
    out[i][0] = (numeric[percent][i]<<1); //+ (numeric[humidity1][i]<<5);
    out[i][1] = (numeric[humidity1][i]<<6) + (numeric[humidity2][i]<<1);
    out[i][2] = (numeric[humidity1][i]>>2);
  }
}

void startMarquee(){
  int hour1 = dt.hour / 10;
  int hour2 = dt.hour % 10;
  int minute1 = dt.minute / 10;
  int minute2 = dt.minute % 10;
  
  timeArrayFilling(hour1, hour2, minute1, minute2);
  marqueeCapturePage(0);
  
  int temperatureNow = int(bme.readTemperature());
  temperatureArrayFiling(temperatureNow / 10, temperatureNow % 10);
  marqueeCapturePage(1);
  
  int pressureNow = int((bme.readPressure()/133.0F));
  pressureArrayFiling(pressureNow / 100, pressureNow % 100 / 10, pressureNow % 10);
  marqueeCapturePage(2);
  
  int humidityNow = int(bme.readHumidity());
  humidityArrayFiling(humidityNow / 10, humidityNow % 10);
  marqueeCapturePage(3);
  
  timeArrayFilling(hour1, hour2, minute1, minute2);
  marqueeCapturePage(4);
  
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

void changeState(){
  bool pinState = digitalRead(2);
  Serial.println(pinState);
  
  if(pinState){
    debounceInterrupt = millis();
  }
  else{
    debounceInterrupt = millis() - debounceInterrupt;
  }
  
  if (debounceInterrupt >= 1000 && debounceInterrupt <= 3000 && !pinState && lockInterrupt) {
    
    if(state < settings)
    {
      state = settings;
    }
    else{
      state ++;
    }
    lockInterrupt = false;
  }
  
  if (debounceInterrupt >= 1 && debounceInterrupt <= 300 &&!pinState && lockInterrupt) {
    
    if(state < settings)
    {
      state++;
    }
    else{
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
    }
    
    lockInterrupt = false;
  }
  
  lockInterrupt = true;
}
