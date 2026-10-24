import { GLYPHS, ROWS } from './font';

/** One column of the panel, top row first. */
export type Column = readonly boolean[];

const EMPTY: Column = Array<boolean>(ROWS).fill(false);

/**
 * Text to columns, centred in `width`: glyphs one blank column apart, anything the font does
 * not know drawn as a space, anything wider than the panel cut on the right. With `colonOn`
 * false every ':' is drawn unlit, which is the blink.
 */
export function renderFrame(text: string, width: number, colonOn = true): Column[] {
  const cols: Column[] = [];
  Array.from(text).forEach((ch, i) => {
    const glyph = ch === ':' && !colonOn ? GLYPHS[';']! : (GLYPHS[ch] ?? GLYPHS[' ']!);
    if (i > 0) cols.push(EMPTY);
    for (let x = 0; x < glyph[0]!.length; x++) cols.push(glyph.map((row) => row[x] === '#'));
  });

  const shown = cols.slice(0, width);
  const left = Math.floor((width - shown.length) / 2);
  const out: Column[] = [];
  for (let i = 0; i < left; i++) out.push(EMPTY);
  out.push(...shown);
  while (out.length < width) out.push(EMPTY);
  return out;
}

/**
 * The marquee as one strip: every frame centred in its own `width`, then the first frame
 * again, so that a window sliding off the end lands exactly where it started.
 */
export function marqueeStrip(frames: readonly string[], width: number): Column[] {
  const strip: Column[] = [];
  for (const frame of [...frames, frames[0] ?? '']) strip.push(...renderFrame(frame, width));
  return strip;
}

/** Columns as eight strings of '#' and '.', top row first — for tests and for reading. */
export function toRows(cols: readonly Column[]): string[] {
  const rows: string[] = [];
  for (let y = 0; y < ROWS; y++) rows.push(cols.map((c) => (c[y] ? '#' : '.')).join(''));
  return rows;
}
