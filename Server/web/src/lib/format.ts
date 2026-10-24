import type { Lang } from '../i18n/plural';
import { plural } from '../i18n/plural';
import type { Dictionary } from '../i18n/uk';

// Readings use a decimal point, as the panel does (HANDOFF §3), whatever the locale would
// choose — hence toFixed and never Intl.NumberFormat for them.
export const fixed = (value: number | null | undefined, digits = 1) => (value == null ? '—' : value.toFixed(digits));

export const signed = (value: number) => (value > 0 ? `+${value}` : value < 0 ? `−${Math.abs(value)}` : '0');

export function ago(t: Dictionary, lang: Lang, iso: string | null, now = Date.now()): string {
  if (!iso) return t.time.never;
  const minutes = Math.floor((now - Date.parse(iso)) / 60_000);
  if (minutes < 1) return t.time.justNow;
  if (minutes < 60) return t.time.minAgo(minutes);
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return t.time.hoursAgo(hours, plural(lang, hours, t.forms.hours));
  const days = Math.floor(hours / 24);
  return t.time.daysAgo(days, plural(lang, days, t.forms.days));
}

export function date(t: Dictionary, iso: string, timeZone?: string): string {
  return new Intl.DateTimeFormat(t.locale, { day: 'numeric', month: 'long', year: 'numeric', timeZone }).format(new Date(iso));
}

export function dateTime(t: Dictionary, iso: string, timeZone?: string): string {
  return new Intl.DateTimeFormat(t.locale, { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit', timeZone }).format(
    new Date(iso),
  );
}

export function clockTime(t: Dictionary, epochMs: number, timeZone?: string): string {
  return new Intl.DateTimeFormat(t.locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone }).format(epochMs);
}

export function monthYear(t: Dictionary, iso: string, timeZone?: string): string {
  return new Intl.DateTimeFormat(t.locale, { month: 'short', year: 'numeric', timeZone }).format(new Date(iso));
}

export function mmss(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}

/** "17–24 жовтня" for a span inside one month, the full dates otherwise. */
export function span(t: Dictionary, fromSec: number, toSec: number, timeZone?: string): string {
  const fmt = (options: Intl.DateTimeFormatOptions) => new Intl.DateTimeFormat(t.locale, { ...options, timeZone });
  return fmt({ day: 'numeric', month: 'long', year: 'numeric' }).formatRange(fromSec * 1000, toSec * 1000);
}
