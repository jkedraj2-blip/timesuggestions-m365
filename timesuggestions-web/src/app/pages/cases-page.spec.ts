import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { CasesPage, parseKeywords } from './cases-page';
import { ApiService } from '../services/api.service';
import { ToastService } from '../services/toast.service';

describe('parseKeywords', () => {
  it('dzieli po przecinku', () => {
    expect(parseKeywords('Alfa, Beta, Gamma')).toEqual(['Alfa', 'Beta', 'Gamma']);
  });

  it('dzieli po średniku, bo tak backend przechowuje słowa kluczowe', () => {
    expect(parseKeywords('Alfa; Beta')).toEqual(['Alfa', 'Beta']);
  });

  it('dopuszcza oba separatory naraz', () => {
    expect(parseKeywords('Alfa; Beta, Gamma')).toEqual(['Alfa', 'Beta', 'Gamma']);
  });

  it('przycina spacje i pomija puste fragmenty', () => {
    expect(parseKeywords('  Alfa ;; , Beta  ')).toEqual(['Alfa', 'Beta']);
  });

  it('zachowuje spacje wewnątrz słowa kluczowego', () => {
    expect(parseKeywords('raport final, Alfa Holding')).toEqual(['raport final', 'Alfa Holding']);
  });

  it('tekst pusty lub złożony z samych separatorów daje pustą listę', () => {
    expect(parseKeywords('')).toEqual([]);
    expect(parseKeywords('   ')).toEqual([]);
    expect(parseKeywords(' ; , ; ')).toEqual([]);
  });
});

describe('CasesPage – otwarcie formularza', () => {
  it('po „Dodaj sprawę" fokus ląduje na polu nazwy sprawy', async () => {
    // Formularz jest w bloku @if — test pilnuje, że fokus czeka na render elementu.
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: { getCases: vi.fn().mockResolvedValue([]) } },
        { provide: ToastService, useValue: { show: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(CasesPage);
    await fixture.whenStable();
    fixture.detectChanges();

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.btn-primary')!
      .click();
    await fixture.whenStable();
    fixture.detectChanges();

    const nameInput = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLInputElement>('.form-card input');
    expect(nameInput).not.toBeNull();
    expect(document.activeElement).toBe(nameInput);
  });
});
