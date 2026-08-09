import { describe, expect, it } from 'vitest';
import { polishPlural } from './polish-plural';

describe('polishPlural', () => {
  it.each([
    [1, 'spotkanie'],
    [2, 'spotkania'],
    [4, 'spotkania'],
    [5, 'spotkań'],
    [12, 'spotkań'],
    [13, 'spotkań'],
    [14, 'spotkań'],
    [22, 'spotkania'],
    [23, 'spotkania'],
    [25, 'spotkań'],
    [0, 'spotkań'],
  ])('%i → "%s"', (count, expected) => {
    expect(polishPlural(count, 'spotkanie', 'spotkania', 'spotkań')).toBe(expected);
  });
});
