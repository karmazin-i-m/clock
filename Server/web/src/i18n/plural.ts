export type Lang = 'uk' | 'en';

/** Ukrainian word forms: [one, few, many] — 1 годинник, 2 годинники, 5 годинників. */
export type Forms = readonly [string, string, string];

/**
 * CLDR's cardinal rule for Ukrainian, which English reduces to one/other: 21 takes "one",
 * 11–14 take "many" however they end.
 */
export function plural(lang: Lang, n: number, forms: Forms): string {
  const [one, few, many] = forms;
  const abs = Math.abs(n);
  if (lang === 'en') return abs === 1 ? one : many;
  const mod10 = abs % 10;
  const mod100 = abs % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}
