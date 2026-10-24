import type { Device } from '../api/types';
import type { LedMode } from '../led/Led';
import { humidityScreen, pressureScreen, temperatureScreen, timeScreen } from '../led/screens';
import type { Dictionary } from '../i18n/uk';

export const hasHumidity = (device: Pick<Device, 'model'>) => device.model !== 'bmp280';

export const clockName = (t: Dictionary, device: { display_name: string | null }) => device.display_name || t.common.unnamed;

/**
 * What the panel picture shows for a clock: the screens it cycles through while it reports,
 * the last temperature in grey once it stops (HANDOFF §6, "офлайн"), and an empty dimmed
 * clock if it has never reported at all (Cabinet-States, empty list).
 */
export function panelScreens(device: Device, now: number): { frames: string[]; mode: LedMode } {
  const last = device.last;
  if (!last) return { frames: ['--:--'], mode: 'night' };
  if (!device.online) return { frames: [temperatureScreen(last.temp_c)], mode: 'off' };

  const frames = [
    timeScreen(new Date(now), device.timezone),
    temperatureScreen(last.temp_c),
    pressureScreen(last.pressure_mmhg, device.pressure_trend),
  ];
  if (hasHumidity(device)) frames.push(humidityScreen(last.humidity_pct));
  return { frames, mode: 'normal' };
}
