import { describe, expect, it } from 'vitest';
import { formatDuration } from './duration.pipe';

describe('formatDuration', () => {
  it.each([
    [0, '0 min'],
    [45, '45 min'],
    [60, '1 godz.'],
    [90, '1 godz. 30 min'],
  ])('formatuje %i minut jako "%s"', (minutes, expected) => {
    expect(formatDuration(minutes)).toBe(expected);
  });
});
