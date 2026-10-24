import type { Lang } from '../i18n/plural';

// The zones offered in the picker. The server accepts any IANA zone and turns it into the
// POSIX rule the ESP's configTzTime() wants, so this list is only about what is quick to
// pick; a clock already set to something else keeps it and shows it as is.
const ZONES: { id: string; uk: string; en: string }[] = [
  { id: 'Europe/Kyiv', uk: 'Київ', en: 'Kyiv' },
  { id: 'Europe/Warsaw', uk: 'Варшава', en: 'Warsaw' },
  { id: 'Europe/Berlin', uk: 'Берлін', en: 'Berlin' },
  { id: 'Europe/Prague', uk: 'Прага', en: 'Prague' },
  { id: 'Europe/Vienna', uk: 'Відень', en: 'Vienna' },
  { id: 'Europe/Paris', uk: 'Париж', en: 'Paris' },
  { id: 'Europe/Madrid', uk: 'Мадрид', en: 'Madrid' },
  { id: 'Europe/Rome', uk: 'Рим', en: 'Rome' },
  { id: 'Europe/Vilnius', uk: 'Вільнюс', en: 'Vilnius' },
  { id: 'Europe/Riga', uk: 'Рига', en: 'Riga' },
  { id: 'Europe/Tallinn', uk: 'Таллінн', en: 'Tallinn' },
  { id: 'Europe/Bucharest', uk: 'Бухарест', en: 'Bucharest' },
  { id: 'Europe/Chisinau', uk: 'Кишинів', en: 'Chisinau' },
  { id: 'Europe/London', uk: 'Лондон', en: 'London' },
  { id: 'Europe/Lisbon', uk: 'Лісабон', en: 'Lisbon' },
  { id: 'Europe/Istanbul', uk: 'Стамбул', en: 'Istanbul' },
  { id: 'Asia/Tbilisi', uk: 'Тбілісі', en: 'Tbilisi' },
  { id: 'America/New_York', uk: 'Нью-Йорк', en: 'New York' },
  { id: 'America/Toronto', uk: 'Торонто', en: 'Toronto' },
  { id: 'America/Chicago', uk: 'Чикаго', en: 'Chicago' },
  { id: 'America/Los_Angeles', uk: 'Лос-Анджелес', en: 'Los Angeles' },
  { id: 'UTC', uk: 'UTC', en: 'UTC' },
];

/** "UTC+2" now, or "UTC+2 / UTC+3" for a zone that changes, as the design labels them. */
function offsets(zone: string): string {
  const year = new Date().getUTCFullYear();
  const at = (month: number) =>
    new Intl.DateTimeFormat('en-GB', { timeZone: zone, timeZoneName: 'shortOffset' })
      .formatToParts(Date.UTC(year, month, 15))
      .find((p) => p.type === 'timeZoneName')
      ?.value.replace('GMT', 'UTC') ?? '';
  const [winter, summer] = [at(0), at(6)].map((o) => (o === 'UTC' ? 'UTC+0' : o));
  return winter === summer ? winter! : `${winter} / ${summer}`;
}

export function timezoneOptions(lang: Lang, current: string): { id: string; label: string }[] {
  const options = ZONES.map((z) => ({ id: z.id, label: `${z[lang]} (${offsets(z.id)})` }));
  if (!ZONES.some((z) => z.id === current)) options.unshift({ id: current, label: `${current} (${offsets(current)})` });
  return options;
}
