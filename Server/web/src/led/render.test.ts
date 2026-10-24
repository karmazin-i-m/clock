import { describe, expect, it } from 'vitest';
import { marqueeStrip, renderFrame, toRows } from './render';

// Each screen the clock itself shows, printed, checked by eye once, then pinned. A change to
// a glyph or to the centring shows up here as a picture, not as a column index.
describe('renderFrame', () => {
  it('draws the clock screen', () => {
    expect(toRows(renderFrame('12:34', 24))).toEqual([
      '...#...##.....##..#..#..',
      '..##..#..#...#..#.#..#..',
      '.#.#.....#.#....#.#..#..',
      '...#.....#....##..#..#..',
      '...#....#.......#.####..',
      '...#...#...#....#....#..',
      '...#..#......#..#....#..',
      '..###.####....##.....#..',
    ]);
  });

  it('draws the temperature screen', () => {
    expect(toRows(renderFrame('23°c', 24))).toEqual([
      '....##...##...#.........',
      '...#..#.#..#.#.#........',
      '......#....#..#.........',
      '......#..##.......##....',
      '.....#.....#.....#......',
      '....#......#.....#......',
      '...#....#..#.....#......',
      '...####..##.......##....',
    ]);
  });

  it('draws the pressure screen with its trend arrow', () => {
    expect(toRows(renderFrame('745p↑', 24))).toEqual([
      '.####.#..#.####.........',
      '....#.#..#.#.........#..',
      '....#.#..#.#....##..###.',
      '...#..#..#.###..#.#..#..',
      '...#..####....#.#.#..#..',
      '..#......#....#.##...#..',
      '..#......#.#..#.#....#..',
      '..#......#..##..#.......',
    ]);
  });

  it('draws the humidity screen', () => {
    expect(toRows(renderFrame('57%', 24))).toEqual([
      '....####.####.##..#.....',
      '....#.......#.##..#.....',
      '....#.......#....#......',
      '....###....#....#.......',
      '.......#...#....#.......',
      '.......#..#....#........',
      '....#..#..#...#..##.....',
      '.....##...#...#..##.....',
    ]);
  });

  it('draws the empty clock', () => {
    expect(toRows(renderFrame('--:--', 24))).toEqual([
      '........................',
      '........................',
      '...........#............',
      '........................',
      '...###.###...###.###....',
      '...........#............',
      '........................',
      '........................',
    ]);
  });

  it('blinks the colon off without moving the digits', () => {
    const on = renderFrame('12:34', 24);
    const off = renderFrame('12:34', 24, false);
    const changed = on.flatMap((col, x) => (col.some((lit, y) => lit !== off[x]![y]) ? [x] : []));
    expect(changed).toEqual([11]);
  });
});

describe('marqueeStrip', () => {
  it('ends on the first frame again', () => {
    const strip = marqueeStrip(['12:34', '23°c'], 24);
    expect(strip).toHaveLength(72);
    expect(strip.slice(48)).toEqual(strip.slice(0, 24));
  });
});
