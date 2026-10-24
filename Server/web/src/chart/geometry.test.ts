import { describe, expect, it } from 'vitest';
import { layout } from './geometry';

describe('layout', () => {
  it('breaks the line at a null and shades exactly the missing stretch', () => {
    const g = layout(
      [
        [0, 10],
        [100, 12],
        [150, null],
        [400, 11],
        [600, 13],
      ],
      0,
      600,
      100,
    )!;

    expect(g.line.match(/M/g)).toHaveLength(2);
    expect(g.line).not.toMatch(/100\.0 [\d.]+ L400\.0/);
    expect(g.gaps).toHaveLength(1);
    expect(g.gaps[0]!.left).toBeCloseTo((100 / 600) * 100);
    expect(g.gaps[0]!.width).toBeCloseTo((300 / 600) * 100);
    expect(g.last?.x).toBe(100);
  });

  it('draws lone points as dots rather than dropping them', () => {
    const g = layout(
      [
        [0, 1],
        [10, null],
        [300, 2],
        [310, null],
        [500, 3],
        [600, 4],
      ],
      0,
      600,
      100,
    )!;
    expect(g.dots.match(/M/g)).toHaveLength(2);
    expect(g.line.match(/M/g)).toHaveLength(1);
    expect(g.gaps).toHaveLength(2);
  });

  it('places the data inside the requested window, not stretched across it', () => {
    const g = layout([[300, 5], [600, 6]], 0, 600, 100)!;
    expect(g.line.startsWith('M300.0')).toBe(true);
  });

  it('has nothing to draw without values', () => {
    expect(layout([], 0, 600, 100)).toBeNull();
    expect(layout([[0, null]], 0, 600, 100)).toBeNull();
  });
});
