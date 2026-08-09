import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CONFIRM_TIMEOUT_MS, TwoStepConfirm } from './confirm-state';

describe('TwoStepConfirm', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('pierwsze kliknięcie uzbraja, drugie potwierdza i rozbraja', () => {
    const confirm = new TwoStepConfirm();

    expect(confirm.confirm('week')).toBe(false);
    expect(confirm.isArmed('week')).toBe(true);

    expect(confirm.confirm('week')).toBe(true);
    expect(confirm.isArmed('week')).toBe(false);
  });

  it('kliknięcie innego przycisku rozbraja poprzedni zamiast potwierdzać', () => {
    const confirm = new TwoStepConfirm();
    confirm.confirm('week');

    expect(confirm.confirm('month')).toBe(false);

    expect(confirm.isArmed('week')).toBe(false);
    expect(confirm.isArmed('month')).toBe(true);
  });

  it('po ~5 s bezczynności wraca do stanu wyjściowego', () => {
    const confirm = new TwoStepConfirm();
    confirm.confirm('week');

    vi.advanceTimersByTime(CONFIRM_TIMEOUT_MS);

    expect(confirm.isArmed('week')).toBe(false);
    // Kolejne kliknięcie znów tylko uzbraja — timeout nie zostawia "połkniętego" potwierdzenia.
    expect(confirm.confirm('week')).toBe(false);
  });

  it('reset() rozbraja natychmiast (klik gdziekolwiek indziej)', () => {
    const confirm = new TwoStepConfirm();
    confirm.confirm('week');

    confirm.reset();

    expect(confirm.isArmed('week')).toBe(false);
  });
});
