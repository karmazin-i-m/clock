import { describe, expect, it } from 'vitest';
import { humidityScreen, pressureScreen, temperatureScreen, timeScreen } from './screens';

describe('screens', () => {
  it('reads the time in the clock’s zone, not the browser’s', () => {
    const instant = new Date('2026-10-24T21:05:00Z');
    expect(timeScreen(instant, 'Europe/Kyiv')).toBe('00:05');
    expect(timeScreen(instant, 'Europe/London')).toBe('22:05');
  });

  it('cuts the temperature like the firmware does', () => {
    expect(temperatureScreen(22.8)).toBe('22°c');
    expect(temperatureScreen(-3.7)).toBe('-3°c');
    expect(temperatureScreen(null)).toBe('--°c');
  });

  it('draws the trend after the pressure', () => {
    expect(pressureScreen(745, 'up')).toBe('745p↑');
    expect(pressureScreen(744, 'flat')).toBe('744p→');
    expect(pressureScreen(744, null)).toBe('744p');
  });

  it('never shows a humidity it does not have', () => {
    expect(humidityScreen(57)).toBe('57%');
    expect(humidityScreen(null)).toBe('--%');
  });
});
