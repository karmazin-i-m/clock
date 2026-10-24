import type { Point } from '../api/types';

export const VIEW_WIDTH = 600;

export type Geometry = {
  /** Path of the line: one subpath per unbroken run of two or more points. */
  line: string;
  /** The same runs closed down to the baseline, for the gradient fill. */
  area: string;
  /** Runs of a single point, which have no line to draw: a dot each. */
  dots: string;
  /** Stretches with no data between two runs, as percentages of the plot width. */
  gaps: { left: number; width: number }[];
  min: number;
  max: number;
  last: { x: number; y: number } | null;
  y: (value: number) => number;
};

/**
 * Lays a series out in a VIEW_WIDTH × height box, time on x across the whole requested window
 * (not just the data), so a clock that started reporting at noon draws its first point at
 * noon. A null splits the line and becomes a shaded gap from the last point before it to the
 * first after it — the line is never drawn across missing data (HANDOFF §4.4, §8 rule 9).
 */
export function layout(points: readonly Point[], from: number, to: number, height: number): Geometry | null {
  const values = points.flatMap(([, v]) => (v == null ? [] : [v]));
  if (values.length === 0) return null;

  const min = Math.min(...values);
  const max = Math.max(...values);
  const pad = (max - min) * 0.18 || 1;
  const lo = min - pad;
  const hi = max + pad;
  const span = to - from || 1;
  const x = (ts: number) => ((ts - from) / span) * VIEW_WIDTH;
  const y = (v: number) => height - ((v - lo) / (hi - lo)) * height;
  const xy = ([ts, v]: readonly [number, number]) => `${x(ts).toFixed(1)} ${y(v).toFixed(1)}`;

  let line = '';
  let area = '';
  let dots = '';
  const gaps: Geometry['gaps'] = [];
  let run: [number, number][] = [];
  let lastBeforeGap: number | null = null;

  const flush = () => {
    if (run.length === 1) dots += `M${xy(run[0]!)} h0.01`;
    if (run.length >= 2) {
      line += `M${run.map(xy).join(' L')}`;
      area += `M${x(run[0]![0]).toFixed(1)} ${height} L${run.map(xy).join(' L')} L${x(run[run.length - 1]![0]).toFixed(1)} ${height} Z`;
    }
    run = [];
  };

  for (const [ts, v] of points) {
    if (v == null) {
      if (run.length > 0) lastBeforeGap = run[run.length - 1]![0];
      flush();
      continue;
    }
    if (lastBeforeGap !== null) {
      const left = (x(lastBeforeGap) / VIEW_WIDTH) * 100;
      gaps.push({ left, width: (x(ts) / VIEW_WIDTH) * 100 - left });
      lastBeforeGap = null;
    }
    run.push([ts, v]);
  }
  flush();

  const final = [...points].reverse().find(([, v]) => v != null);
  return {
    line,
    area,
    dots,
    gaps,
    min,
    max,
    last: final ? { x: (x(final[0]) / VIEW_WIDTH) * 100, y: y(final[1]!) } : null,
    y,
  };
}
