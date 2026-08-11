import { describe, expect, it } from 'vitest';
import {
  currentMonth,
  daysInMonth,
  isWeekend,
  monthDays,
  monthRangeLabel,
  shiftMonth,
  statusLabel,
  weekdayShort,
} from './timeline-panel';

describe('shiftMonth', () => {
  it('przechodzi przez granice roku w obie strony', () => {
    expect(shiftMonth({ year: 2026, month: 12 }, 1)).toEqual({ year: 2027, month: 1 });
    expect(shiftMonth({ year: 2026, month: 1 }, -1)).toEqual({ year: 2025, month: 12 });
  });

  it('delta zero nie zmienia miesiąca', () => {
    expect(shiftMonth({ year: 2026, month: 8 }, 0)).toEqual({ year: 2026, month: 8 });
  });
});

describe('daysInMonth', () => {
  it('zna długości miesięcy, w tym luty roku przestępnego', () => {
    expect(daysInMonth({ year: 2026, month: 8 })).toBe(31);
    expect(daysInMonth({ year: 2026, month: 2 })).toBe(28);
    expect(daysInMonth({ year: 2028, month: 2 })).toBe(29);
    expect(daysInMonth({ year: 2026, month: 4 })).toBe(30);
  });
});

describe('monthDays', () => {
  it('zwraca wszystkie dni miesiąca jako ISO z zerami wiodącymi', () => {
    const days = monthDays({ year: 2026, month: 8 });
    expect(days).toHaveLength(31);
    expect(days[0]).toBe('2026-08-01');
    expect(days[30]).toBe('2026-08-31');
  });
});

describe('monthRangeLabel', () => {
  it('formatuje nagłówek paska „01.08–31.08.2026"', () => {
    expect(monthRangeLabel({ year: 2026, month: 8 })).toBe('01.08–31.08.2026');
    expect(monthRangeLabel({ year: 2026, month: 2 })).toBe('01.02–28.02.2026');
  });
});

describe('weekdayShort', () => {
  it('skróty dni pochodzą z Intl, z wielką literą i bez kropki', () => {
    // 3 sierpnia 2026 = poniedziałek; 9 sierpnia = niedziela.
    expect(weekdayShort('2026-08-03').toLowerCase()).toContain('pon');
    expect(weekdayShort('2026-08-03')).not.toContain('.');
    expect(weekdayShort('2026-08-09').toLowerCase()).toContain('niedz');
  });
});

describe('isWeekend', () => {
  it('sobota i niedziela to weekend, poniedziałek nie', () => {
    expect(isWeekend('2026-08-08')).toBe(true); // sobota
    expect(isWeekend('2026-08-09')).toBe(true); // niedziela
    expect(isWeekend('2026-08-03')).toBe(false); // poniedziałek
  });
});

describe('statusLabel', () => {
  it('status zawsze ma etykietę tekstową (nigdy sam kolor)', () => {
    expect(statusLabel('pending')).toBe('sugestia');
    expect(statusLabel('active')).toBe('do rozliczenia');
    expect(statusLabel('archived')).toBe('rozliczone');
  });
});

describe('currentMonth', () => {
  it('mapuje datę na rok i miesiąc 1–12', () => {
    expect(currentMonth(new Date(2026, 7, 11))).toEqual({ year: 2026, month: 8 });
  });
});
