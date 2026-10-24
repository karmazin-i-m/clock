/**
 * Night dimming as the firmware stores it: two boundaries on a 0–24 line, the end half open
 * (Clock_Arduino dimmedAt()). Equal boundaries never dim, 0–24 always does, and a start later
 * than the end wraps midnight. The cabinet edits exactly these two numbers; "never" and
 * "always" are only names for particular pairs, so nothing here can mean something the clock
 * would read differently.
 */
export type Night = { from: number; until: number };

export type NightMode = 'never' | 'always' | 'range';

export const NEVER: Night = { from: 0, until: 0 };
export const ALWAYS: Night = { from: 0, until: 24 };
export const DEFAULT_RANGE: Night = { from: 22, until: 7 };

export const PRESETS: readonly Night[] = [NEVER, DEFAULT_RANGE, { from: 23, until: 6 }, ALWAYS];

/** The same rule as dimmedAt(), for one hour of the day. */
export function dimmedAt({ from, until }: Night, hour: number): boolean {
  if (from === until) return false;
  if (from > until) return hour >= from || hour < until;
  return hour >= from && hour < until;
}

export function hoursDimmed(night: Night): number {
  let n = 0;
  for (let h = 0; h < 24; h++) if (dimmedAt(night, h)) n++;
  return n;
}

// Read off the hours rather than off the numbers, so every pair the clock could hold — 24–0,
// 5–5, 0–24 — is named by what the clock actually does with it.
export function modeOf(night: Night): NightMode {
  const hours = hoursDimmed(night);
  return hours === 0 ? 'never' : hours === 24 ? 'always' : 'range';
}

/** Filled stretches of the 0–24 track, as [start, end] in hours. */
export function segments(night: Night): [number, number][] {
  const mode = modeOf(night);
  if (mode === 'never') return [];
  if (mode === 'always') return [[0, 24]];
  const from = night.from % 24;
  const until = night.until === 24 ? 24 : night.until % 24;
  if (from < until) return [[from, until]];
  const pieces: [number, number][] = [[from, 24], [0, until]];
  return pieces.filter(([a, b]) => b > a);
}

/**
 * One hour earlier or later on one end, wrapping round the day. A range is never stepped
 * onto equal ends: to the clock that means "never", which is not what pressing + on a range
 * asks for, so the step carries on one more hour.
 */
export function step(night: Night, end: 'from' | 'until', delta: 1 | -1): Night {
  const base = modeOf(night) === 'range' ? night : DEFAULT_RANGE;
  const other = end === 'from' ? base.until % 24 : base.from % 24;
  let value = ((base[end] % 24) + delta + 24) % 24;
  if (value === other) value = (value + delta + 24) % 24;
  return { ...base, [end]: value };
}

export const hh = (h: number) => `${String(h % 24).padStart(2, '0')}:00`;
