import { describe, expect, it } from 'vitest';
import { formatCaseMeta } from './case-label';

describe('formatCaseMeta', () => {
  it('skleja numer i klienta separatorem „·"', () => {
    expect(formatCaseMeta('K-2026-001', 'Kowalski')).toBe('K-2026-001 · Kowalski');
  });

  it('pokazuje samotną część bez osieroconego separatora', () => {
    expect(formatCaseMeta('K-2026-001', null)).toBe('K-2026-001');
    expect(formatCaseMeta(null, 'Kowalski')).toBe('Kowalski');
  });

  it('zwraca null, gdy nie ma czego pokazać', () => {
    expect(formatCaseMeta(null, null)).toBeNull();
    expect(formatCaseMeta('  ', '')).toBeNull();
  });
});
