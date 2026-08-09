import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ComponentFixture } from '@angular/core/testing';
import { SuggestionCard } from './suggestion-card';
import { ApiService } from '../services/api.service';
import { Suggestion } from '../models/api.models';

function createSuggestion(overrides: Partial<Suggestion> = {}): Suggestion {
  return {
    id: 1,
    source: 'calendar',
    title: 'Spotkanie z Kowalski',
    startedAt: '2026-08-06T10:00:00',
    durationMinutes: 60,
    caseId: 1,
    caseName: 'Kowalski sp. z o.o.',
    isAmbiguous: false,
    matchCandidates: [],
    proposedDescription: 'Spotkanie z Kowalski',
    status: 'pending',
    ...overrides,
  };
}

describe('SuggestionCard', () => {
  let fixture: ComponentFixture<SuggestionCard>;

  const approveMock = vi.fn();

  beforeEach(async () => {
    approveMock.mockReset();
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: { approve: approveMock, reject: vi.fn(), restore: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(SuggestionCard);
    fixture.componentRef.setInput('suggestion', createSuggestion());
    fixture.componentRef.setInput('cases', []);
    await fixture.whenStable();
    fixture.detectChanges();
  });

  function card(): SuggestionCard & {
    durationDraft: { (): number; set(value: number): void };
    descriptionDraft: { (): string; set(value: string): void };
    sourceConflict: () => boolean;
    editing: { (): boolean; set(value: boolean): void };
    approve: () => Promise<void>;
  } {
    // Dostęp do pól protected na potrzeby testu zachowania draftów.
    return fixture.componentInstance as never;
  }

  async function setSuggestion(suggestion: Suggestion): Promise<void> {
    fixture.componentRef.setInput('suggestion', suggestion);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('inicjalizuje drafty z wartości sugestii', () => {
    expect(card().durationDraft()).toBe(60);
    expect(card().descriptionDraft()).toBe('Spotkanie z Kowalski');
  });

  it('resetuje drafty po zmianie inputa, gdy użytkownik nie edytuje', async () => {
    await setSuggestion(createSuggestion({ durationMinutes: 90, proposedDescription: 'Nowy opis' }));

    expect(card().durationDraft()).toBe(90);
    expect(card().descriptionDraft()).toBe('Nowy opis');
    expect(card().sourceConflict()).toBe(false);
  });

  it('nie nadpisuje pracy użytkownika i blokuje Zatwierdź przy konflikcie edycji', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    fixture.detectChanges();

    // Synchronizacja w tle zmieniła źródło, podczas gdy użytkownik edytuje.
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));

    expect(card().sourceConflict()).toBe(true);
    expect(card().durationDraft()).toBe(120); // praca użytkownika nietknięta

    const approveButton = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.btn-primary');
    expect(approveButton?.disabled).toBe(true);

    // Zatwierdzenie zablokowane także programowo — API nie może zostać wywołane.
    await card().approve();
    expect(approveMock).not.toHaveBeenCalled();
  });

  it('"Odśwież wartości" przejmuje nowe wartości źródła i odblokowuje Zatwierdź', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));
    expect(card().sourceConflict()).toBe(true);

    (fixture.nativeElement as HTMLElement)
      .querySelectorAll<HTMLButtonElement>('.conflict-box button')[0]
      .click();
    fixture.detectChanges();

    expect(card().sourceConflict()).toBe(false);
    expect(card().durationDraft()).toBe(90);
  });

  it('"Zachowaj moje" utrzymuje wartości użytkownika i odblokowuje Zatwierdź', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));

    (fixture.nativeElement as HTMLElement)
      .querySelectorAll<HTMLButtonElement>('.conflict-box button')[1]
      .click();
    fixture.detectChanges();

    expect(card().sourceConflict()).toBe(false);
    expect(card().durationDraft()).toBe(120);
  });
});
