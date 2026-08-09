import { describe, expect, it } from 'vitest';
import {
  allVisibleRange,
  confirmSettleLabel,
  currentMonthRange,
  formatRangeLabel,
  lastWeekRange,
  settledToastMessage,
  toIsoDate,
} from './time-entries-page';

describe('toIsoDate', () => {
  it('formatuje datę lokalną jako yyyy-MM-dd z zerami wiodącymi', () => {
    expect(toIsoDate(new Date(2026, 7, 9))).toBe('2026-08-09');
    expect(toIsoDate(new Date(2026, 0, 1))).toBe('2026-01-01');
  });
});

describe('lastWeekRange', () => {
  it('obejmuje 7 ostatnich dni z dzisiaj włącznie', () => {
    expect(lastWeekRange(new Date(2026, 7, 9))).toEqual({ from: '2026-08-03', to: '2026-08-09' });
  });

  it('przechodzi przez granicę miesiąca i roku', () => {
    expect(lastWeekRange(new Date(2026, 7, 2))).toEqual({ from: '2026-07-27', to: '2026-08-02' });
    expect(lastWeekRange(new Date(2026, 0, 3))).toEqual({ from: '2025-12-28', to: '2026-01-03' });
  });
});

describe('currentMonthRange', () => {
  it('zaczyna od pierwszego dnia bieżącego miesiąca', () => {
    expect(currentMonthRange(new Date(2026, 7, 9))).toEqual({ from: '2026-08-01', to: '2026-08-09' });
  });

  it('pierwszego dnia miesiąca zakres to jeden dzień', () => {
    expect(currentMonthRange(new Date(2026, 7, 1))).toEqual({ from: '2026-08-01', to: '2026-08-01' });
  });
});

describe('allVisibleRange', () => {
  it('rozciąga się od najstarszego widocznego dnia do dziś', () => {
    const range = allVisibleRange(['2026-08-05', '2026-07-20', '2026-08-01'], new Date(2026, 7, 9));

    expect(range).toEqual({ from: '2026-07-20', to: '2026-08-09' });
  });

  it('pusta lista → null (nie ma czego rozliczać)', () => {
    expect(allVisibleRange([], new Date(2026, 7, 9))).toBeNull();
  });
});

describe('settledToastMessage', () => {
  it('odmienia wpisy przez polishPlural i formatuje czas', () => {
    expect(settledToastMessage(1, 45)).toBe('Rozliczono 1 wpis (45 min).');
    expect(settledToastMessage(3, 120)).toBe('Rozliczono 3 wpisy (2 godz.).');
    expect(settledToastMessage(5, 570)).toBe('Rozliczono 5 wpisów (9 godz. 30 min).');
    expect(settledToastMessage(12, 570)).toBe('Rozliczono 12 wpisów (9 godz. 30 min).');
  });

  it('zero wpisów to normalny komunikat, nie błąd', () => {
    expect(settledToastMessage(0, 0)).toBe('Rozliczono 0 wpisów (0 min).');
  });
});

describe('formatRangeLabel', () => {
  it('w obrębie jednego roku pomija rok', () => {
    expect(formatRangeLabel({ from: '2026-08-03', to: '2026-08-09' })).toBe('03.08–09.08');
    expect(formatRangeLabel({ from: '2026-01-01', to: '2026-08-09' })).toBe('01.01–09.08');
  });

  it('przy przełomie roku dopisuje lata, żeby zakres nie wyglądał na kilka tygodni', () => {
    expect(formatRangeLabel({ from: '2025-01-01', to: '2026-08-09' }))
      .toBe('01.01.2025–09.08.2026');
  });
});

describe('confirmSettleLabel', () => {
  it('pyta o potwierdzenie z liczbą wpisów i sumą czasu', () => {
    expect(confirmSettleLabel(3, 130)).toBe('Na pewno? Rozliczysz 3 wpisy (2 godz. 10 min)');
    expect(confirmSettleLabel(1, 60)).toBe('Na pewno? Rozliczysz 1 wpis (1 godz.)');
  });

  it('dla akcji hurtowej dopisuje zakres dat', () => {
    expect(confirmSettleLabel(3, 130, { from: '2026-08-03', to: '2026-08-09' }))
      .toBe('Na pewno? Rozliczysz 3 wpisy (2 godz. 10 min) z 03.08–09.08');
  });

  it('wieloletnie „wszystko" ujawnia skalę zakresu', () => {
    expect(confirmSettleLabel(143, 12600, { from: '2025-01-01', to: '2026-08-09' }))
      .toBe('Na pewno? Rozliczysz 143 wpisy (210 godz.) z 01.01.2025–09.08.2026');
  });

  it('przy „Rozlicz dzień" zakres jest pomijany, bo wynika z nagłówka dnia', () => {
    expect(confirmSettleLabel(2, 90, null)).toBe('Na pewno? Rozliczysz 2 wpisy (1 godz. 30 min)');
  });
});
