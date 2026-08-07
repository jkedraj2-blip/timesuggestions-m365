import { describe, expect, it } from 'vitest';
import { normalizedDocumentMinutes } from './suggestions-page';

describe('normalizedDocumentMinutes', () => {
  it.each([
    [1, 1],
    [30, 30],
    [480, 480],
  ])('przepuszcza wartość %i', (raw, expected) => {
    expect(normalizedDocumentMinutes(raw)).toBe(expected);
  });

  it.each([0, -5, 481, 30.5, Number.NaN, 'abc', '', null, undefined])(
    'wartość spoza zakresu (%s) traktuje jak brak preferencji',
    (raw) => {
      expect(normalizedDocumentMinutes(raw)).toBeUndefined();
    },
  );
});
