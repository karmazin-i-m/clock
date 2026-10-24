import type { Trend } from '../api/types';

/**
 * What the clock's own screens read, as text for <Led>. Each mirrors the firmware rather than
 * prettifying it: the temperature screen shows whole degrees, cut rather than rounded
 * (int(sensorTemperature)), and the pressure arrow is the firmware's three way trend.
 */
export function timeScreen(now: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
    .formatToParts(now);
  const hour = parts.find((p) => p.type === 'hour')?.value ?? '--';
  const minute = parts.find((p) => p.type === 'minute')?.value ?? '--';
  return `${hour}:${minute}`;
}

export function temperatureScreen(tempC: number | null | undefined): string {
  return tempC == null ? '--°c' : `${Math.trunc(tempC)}°c`;
}

const ARROWS: Record<Trend, string> = { up: '↑', down: '↓', flat: '→' };

export function pressureScreen(mmhg: number | null | undefined, trend: Trend | null | undefined): string {
  return mmhg == null ? '---p' : `${mmhg}p${trend ? ARROWS[trend] : ''}`;
}

export function humidityScreen(pct: number | null | undefined): string {
  return pct == null ? '--%' : `${pct}%`;
}

export function trendArrow(trend: Trend | null | undefined): string {
  return trend ? ARROWS[trend] : '';
}
