import { describe, expect, it } from 'vitest';
import { ALWAYS, NEVER, dimmedAt, hoursDimmed, modeOf, segments, step } from './model';

describe('night dimming', () => {
  it('wraps midnight for a start later than the end', () => {
    const night = { from: 22, until: 7 };
    expect(segments(night)).toEqual([
      [22, 24],
      [0, 7],
    ]);
    expect(hoursDimmed(night)).toBe(9);
    expect(dimmedAt(night, 23)).toBe(true);
    expect(dimmedAt(night, 7)).toBe(false);
  });

  it('names pairs by what the clock does with them', () => {
    expect(modeOf(NEVER)).toBe('never');
    expect(modeOf({ from: 5, until: 5 })).toBe('never');
    expect(modeOf({ from: 24, until: 0 })).toBe('never');
    expect(modeOf(ALWAYS)).toBe('always');
    expect(modeOf({ from: 23, until: 6 })).toBe('range');
    expect(segments(ALWAYS)).toEqual([[0, 24]]);
    expect(segments(NEVER)).toEqual([]);
  });

  it('never steps a range onto equal ends', () => {
    expect(step({ from: 6, until: 7 }, 'from', 1)).toEqual({ from: 8, until: 7 });
    expect(step({ from: 8, until: 7 }, 'until', 1)).toEqual({ from: 8, until: 9 });
    expect(step({ from: 0, until: 7 }, 'from', -1)).toEqual({ from: 23, until: 7 });
  });

  it('starts stepping from the usual night when there is no range yet', () => {
    expect(step(NEVER, 'from', 1)).toEqual({ from: 23, until: 7 });
    expect(step(ALWAYS, 'until', -1)).toEqual({ from: 22, until: 6 });
  });
});
