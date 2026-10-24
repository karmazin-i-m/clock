import { describe, expect, it } from 'vitest';
import { plural } from './plural';

const clocks = ['годинник', 'годинники', 'годинників'] as const;

describe('plural', () => {
  it.each([
    [1, 'годинник'],
    [2, 'годинники'],
    [4, 'годинники'],
    [5, 'годинників'],
    [11, 'годинників'],
    [12, 'годинників'],
    [21, 'годинник'],
    [22, 'годинники'],
    [0, 'годинників'],
  ])('uk %i → %s', (n, word) => expect(plural('uk', n, clocks)).toBe(word));

  it('falls back to one/other in English', () => {
    const forms = ['clock', 'clocks', 'clocks'] as const;
    expect(plural('en', 1, forms)).toBe('clock');
    expect(plural('en', 2, forms)).toBe('clocks');
    expect(plural('en', 21, forms)).toBe('clocks');
  });
});
