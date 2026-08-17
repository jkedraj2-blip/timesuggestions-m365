import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiService, SyncStage } from '../services/api.service';
import { AutoSyncService } from '../services/auto-sync.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { TwoStepConfirm } from '../services/confirm-state';
import { toUserMessage } from '../services/user-message';
import {
  CaseInfo,
  Suggestion,
  SuggestionSource,
  SuggestionStatus,
  SyncFetchedCounts,
  SyncFilteredOutCounts,
  SyncReport,
} from '../models/api.models';
import { FormsModule } from '@angular/forms';
import { SuggestionCard, SuggestionResolved } from '../components/suggestion-card';
import { formatDuration } from '../pipes/duration.pipe';
import { polishPlural } from '../pipes/polish-plural';
import {
  SYNC_DAYS_OPTIONS,
  SYNC_DAYS_STORAGE_KEY,
  loadSyncDays,
  normalizedSyncDays,
} from '../services/sync-preferences';

// Preferencje synchronizacji mieszkają we wspólnym module; re-eksport zostawiamy,
// bo to nadal naturalne miejsce, żeby ich szukać.
export { SYNC_DAYS_OPTIONS, normalizedSyncDays };

type SourceFilter = 'all' | SuggestionSource;
type StatusFilter = Extract<SuggestionStatus, 'pending' | 'rejected' | 'archived'>;

/**
 * Nagłówek raportu mówi o EFEKCIE syncu (nowe/zaktualizowane/usunięte),
 * nie o licznikach technicznych — "pominięto" i "pobrano" to szczegóły.
 */
export function syncReportHeadline(report: Pick<SyncReport, 'created' | 'updated' | 'removed'>): string {
  const parts: string[] = [];
  if (report.created > 0) {
    parts.push(`${report.created} ${polishPlural(report.created, 'nowa sugestia', 'nowe sugestie', 'nowych sugestii')}`);
  }
  if (report.updated > 0) {
    parts.push(`${report.updated} zaktualizowano`);
  }
  if (report.removed > 0) {
    parts.push(`${report.removed} usunięto`);
  }
  if (parts.length === 0) {
    return 'Synchronizacja zakończona bez zmian. Wszystkie sugestie są aktualne.';
  }
  return `Synchronizacja zakończona: ${parts.join(', ')}.`;
}

/**
 * "Sprawdzono" zamiast "pobrano" — każdy sync celowo pobiera pełny snapshot
 * okna (wykrywanie usuniętych/przeniesionych spotkań), więc liczba powtarza
 * się co sync i ma brzmieć jak kontrola, nie jak nowe dane. Zera pomijamy.
 * Liczba dni pochodzi z raportu (windowDays) — faktycznie użyte okno backendu.
 */
export function syncCheckedLine(fetched: SyncFetchedCounts, windowDays: number): string {
  const meetings = fetched.calendarEvents;
  const files = fetched.driveFiles;
  if (meetings === 0 && files === 0) {
    return `Brak spotkań i plików do sprawdzenia w ostatnich ${windowDays} dniach.`;
  }
  const meetingsText = `${meetings} ${polishPlural(meetings, 'spotkanie', 'spotkania', 'spotkań')}`;
  const filesText = `${files} ${polishPlural(files, 'plik', 'pliki', 'plików')}`;
  if (files === 0) {
    return `Sprawdzono ${meetingsText} z ostatnich ${windowDays} dni.`;
  }
  if (meetings === 0) {
    return `Sprawdzono ${filesText} z ostatnich ${windowDays} dni.`;
  }
  return `Sprawdzono ${meetingsText} z ostatnich ${windowDays} dni i ${filesText}.`;
}

/**
 * Sugestie, które wolno zatwierdzić HURTEM: jednoznacznie dopasowane i ze zmierzonym
 * czasem. Sesja o jednym zapisie („czas do uzupełnienia") nie ma zmierzonego czasu —
 * widoczne minuty to minimum z konfiguracji. Dopasowanie sprawy tego nie ratuje:
 * mówi, KOMU rozliczyć, a nie ILE. Hurt zapisuje czas bez otwierania choćby jednej
 * karty, więc taka pozycja trafiłaby na rachunek z wartością, na którą nikt nie
 * spojrzał — i to właśnie w trybie, w którym nikt na nią nie patrzy.
 */
export function bulkApprovable(suggestions: readonly Suggestion[]): Suggestion[] {
  return suggestions.filter(
    (suggestion) => suggestion.caseId !== null && !suggestion.isAmbiguous && !suggestion.needsTimeReview);
}

/** Toast po hurcie: co zapisano, co się nie udało i co świadomie pominięto. */
export function bulkApproveToast(approved: number, failed: number, skipped: number): string {
  const done = failed === 0
    ? `Zapisano ${approved} ${polishPlural(approved, 'wpis', 'wpisy', 'wpisów')} czasu pracy.`
    : `Zapisano ${approved}, nie udało się ${failed}. Spróbuj pojedynczo.`;
  if (skipped === 0) {
    return done;
  }

  // Pominięcie musi być powiedziane wprost: cicho przemilczane wyglądałoby jak
  // „wszystko rozliczone", a te pozycje dalej czekają na decyzję o czasie.
  const subject = polishPlural(skipped, 'sugestię', 'sugestie', 'sugestii');
  return `${done} Pominięto ${skipped} ${subject} z czasem do uzupełnienia`
    + ' — zatwierdź je pojedynczo.';
}

/** Toast po hurtowej archiwizacji: „Zarchiwizowano 3 sugestie." — bez „Cofnij", bo bez unarchive. */
export function archivedSuggestionsToast(count: number): string {
  return `Zarchiwizowano ${count} ${polishPlural(count, 'sugestię', 'sugestie', 'sugestii')}.`;
}

/** "Pominięto (już istniały)" po ludzku — powtórny sync niczego nie duplikuje. */
export function syncSkippedLine(count: number): string {
  const subject = polishPlural(count, 'pozycja była', 'pozycje były', 'pozycji było');
  return `${count} ${subject} już wcześniej na liście sugestii, więc nic nie duplikujemy.`;
}

/**
 * Powody odrzuceń jako pary (liczba, etykieta) — etykiety opisują powód
 * z perspektywy użytkownika, nie nazwy licznika. Odmieniane liczebnikiem,
 * bo określają "pozycję/pozycje/pozycji" ze wstępu ("5 odwołanych", nie "5 odwołane").
 */
function filteredOutParts(
  filtered: SyncFilteredOutCounts,
): Array<{ count: number; label: string }> {
  const parts: Array<{ count: number; label: string }> = [];
  if (filtered.private > 0) {
    parts.push({
      count: filtered.private,
      label: `${polishPlural(filtered.private, 'prywatna lub poufna', 'prywatne lub poufne', 'prywatnych lub poufnych')}`
        + ' (tytuły nie opuszczają przeglądarki)',
    });
  }
  if (filtered.cancelled > 0) {
    parts.push({
      count: filtered.cancelled,
      label: polishPlural(filtered.cancelled, 'odwołana', 'odwołane', 'odwołanych'),
    });
  }
  if (filtered.tooShort > 0) {
    parts.push({
      count: filtered.tooShort,
      label: polishPlural(filtered.tooShort, 'krótsza', 'krótsze', 'krótszych') + ' niż 5 minut',
    });
  }
  if (filtered.allDay > 0) {
    parts.push({
      count: filtered.allDay,
      label: polishPlural(filtered.allDay, 'całodniowa', 'całodniowe', 'całodniowych'),
    });
  }
  if (filtered.invalidDates > 0) {
    parts.push({ count: filtered.invalidDates, label: 'z błędnymi datami' });
  }
  if (filtered.notOfficeDocument > 0) {
    parts.push({
      count: filtered.notOfficeDocument,
      label: polishPlural(
        filtered.notOfficeDocument, 'plik inny niż Word/Excel', 'pliki inne niż Word/Excel', 'plików innych niż Word/Excel'),
    });
  }
  // Pozycji spoza rozliczanego zakresu nie ma tu wcale — ani spotkań, ani dokumentów.
  // Backend pobiera z Graph z zapasem ponad okno (przy kalendarzu ponad dwie doby, bo
  // okno liczy się od POCZĄTKU doby lokalnej), więc ten licznik meldował przy każdej
  // synchronizacji ten sam stały zapas jako „pominięte pozycje" — liczba nie mówiła
  // nic o pracy użytkownika. Pozycje spoza okna nie liczą się też jako pobrane.
  if (filtered.notModifiedByUser > 0) {
    parts.push({
      count: filtered.notModifiedByUser,
      label: polishPlural(
        filtered.notModifiedByUser, 'zmodyfikowana', 'zmodyfikowane', 'zmodyfikowanych') + ' przez kogoś innego',
    });
  }
  return parts;
}

/**
 * Pełna linia odrzuceń: niezerowe powody sklejone separatorem — join() zamiast
 * spanów z doklejonym "· " w szablonie, bo tam separator wisiał także po
 * ostatniej wyrenderowanej pozycji. Przy JEDNYM powodzie liczba pada tylko raz —
 * "Pominięto 1 pozycję: sprzed rozliczanego zakresu…" zamiast dublowania
 * "1 … 1 …", po którym nie wiadomo, ile pozycji naprawdę pominięto.
 */
export function filteredOutLine(filtered: SyncFilteredOutCounts): string {
  const subject = polishPlural(filtered.total, 'pozycję', 'pozycje', 'pozycji');
  const parts = filteredOutParts(filtered);
  const breakdown = parts.length === 1
    ? parts[0].label
    : parts.map((part) => `${part.count} ${part.label}`).join(' · ');
  return `Pominięto ${filtered.total} ${subject}: ${breakdown}.`;
}

@Component({
  selector: 'app-suggestions-page',
  imports: [SuggestionCard, FormsModule],
  // Klik poza przyciskiem rozbraja potwierdzenie (klik w przycisk robi stopPropagation).
  host: { '(document:click)': 'confirm.reset()' },
  template: `
    <div class="toolbar">
      <button class="btn btn-primary" (click)="sync()" [disabled]="syncing() || loading()">
        {{ syncing() ? 'Synchronizuję…' : 'Synchronizuj' }}
      </button>

      <label class="field sync-days" title="Z ilu ostatnich dni pobierać spotkania i dokumenty. Szerszy zakres przydaje się np. po urlopie, ale synchronizacja potrwa wtedy dłużej.">
        Zakres (dni)
        <select [(ngModel)]="syncDaysDraft" (change)="saveSyncDays()">
          @for (option of syncDaysOptions; track option) {
            <option [ngValue]="option">{{ option }}</option>
          }
        </select>
      </label>

      <!-- Wyjaśnienie NIE mieści się w atrybucie title: to nie jedno zdanie, tylko cztery
           akapity o tym, skąd w ogóle biorą się liczby na liście — a podpowiedź pod
           kursorem i tak nie istnieje na dotyku. Stąd rozwijany panel pod paskiem. -->
      <div class="auto-sync-group">
        <label class="field auto-sync" title="Aplikacja sama sprawdza kalendarz i OneDrive, dopóki ta karta jest otwarta.">
          <input type="checkbox" [checked]="autoSync.enabled()" (change)="toggleAutoSync($event)" />
          Sprawdzaj co {{ autoSync.intervalMinutes }} min
        </label>
        <!-- Nazwany odnośnik POD polem wyboru, nie obok: samo „?" nie mówiło, CZEGO
             dotyczy wyjaśnienie, a ustawione w tym samym wierszu czytało się jak druga,
             niezależna kontrolka. Pod spodem widać, że należy do tego przełącznika. -->
        <div class="auto-sync-sub">
          <button
            class="btn btn-ghost auto-sync-help"
            (click)="showAutoSyncHelp.set(!showAutoSyncHelp())"
            [attr.aria-expanded]="showAutoSyncHelp()"
          >{{ showAutoSyncHelp() ? 'Ukryj wyjaśnienie' : 'Co to daje?' }}</button>
          @if (autoSync.enabled()) {
            <span class="text-muted auto-sync-status">· {{ autoSyncStatus() }}</span>
          }
        </div>
      </div>

      <div class="filter-group">
        <span class="text-muted">Źródło:</span>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'all'" (click)="sourceFilter.set('all')">wszystkie</button>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'calendar'" (click)="sourceFilter.set('calendar')">spotkania</button>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'document'" (click)="sourceFilter.set('document')">dokumenty</button>
      </div>

      <div class="filter-group">
        <span class="text-muted">Status:</span>
        <button class="btn" [class.btn-ghost]="statusFilter() !== 'pending'" (click)="setStatusFilter('pending')">oczekujące</button>
        <button class="btn" [class.btn-ghost]="statusFilter() !== 'rejected'" (click)="setStatusFilter('rejected')">odrzucone</button>
        <button class="btn" [class.btn-ghost]="statusFilter() !== 'archived'" (click)="setStatusFilter('archived')">zarchiwizowane</button>
      </div>

      @if (autoMatchedCount() > 0) {
        <button
          class="btn"
          (click)="approveAllMatched()"
          [disabled]="bulkApproving()"
          title="Zatwierdza dopasowane sugestie ze zmierzonym czasem. Sesje z plakietką czasu do uzupełnienia zostają — ich czas trzeba potwierdzić na karcie."
        >
          {{ bulkApproving() ? 'Zatwierdzam…' : 'Zatwierdź wszystkie dopasowane (' + autoMatchedCount() + ')' }}
        </button>
      }

      @if (timeReviewCount() > 0) {
        <!-- Bez tej linijki licznik przy przycisku byłby po prostu mniejszy niż liczba
             dopasowanych kart i wyglądałby na pomyłkę. -->
        <span class="text-warn bulk-note">
          {{ timeReviewCount() }} dopasowanych czeka na czas — zatwierdź je pojedynczo.
        </span>
      }

      @if (statusFilter() === 'rejected' && suggestions().length > 0) {
        <!-- Archiwizacja jest jednokierunkowa — stąd dwustopniowe potwierdzenie zamiast window.confirm. -->
        <button class="btn" [class.btn-danger]="confirm.isArmed('archive-rejected')"
          (click)="archiveAllRejected($event)" [disabled]="bulkArchiving()">
          {{ archiveAllLabel() }}
        </button>
      }
    </div>

    <!-- Zachęta zamiast domyślnego włączenia: decyzja zostaje przy użytkowniku, ale nie
         zależy od tego, czy sam zauważy pole wyboru w pasku. Pytamy RAZ — „Nie teraz"
         zamyka temat na stałe, tak samo jak włączenie. -->
    @if (autoSync.suggestsEnabling()) {
      <div class="info-box auto-sync-nudge">
        <div class="nudge-text">
          <strong>Zacznij od włączenia sprawdzania w tle.</strong>
          <p>
            Aplikacja liczy czas z tego, kiedy Word zapisuje kolejne wersje dokumentu.
            Bywa, że zapisów jest dużo i czas wychodzi dobrze sam z siebie. Bywa też, że
            z kilku godzin pracy zostaje jeden zapis: wtedy nie ma z czego liczyć, sugestia
            dostaje minimum przewidziane w ustawieniach oraz etykietę „czas do uzupełnienia",
            a godziny musisz wpisać z pamięci. Sprawdzanie w tle dokłada własne punkty
            pomiaru, więc rzadziej trafisz na taki przypadek. Działa, dopóki karta
            z aplikacją jest otwarta.
          </p>
        </div>
        <div class="actions">
          <button class="btn btn-primary" (click)="autoSync.setEnabled(true)">
            Włącz sprawdzanie co {{ autoSync.intervalMinutes }} min
          </button>
          <button class="btn" (click)="autoSync.dismissSuggestion()">Nie teraz</button>
        </div>
      </div>
    }

    @if (showAutoSyncHelp()) {
      <!-- Tekst pisany dla kogoś, kto nie wie i nie chce wiedzieć, czym jest „wersja pliku".
           Celowo NIE obiecuje, że długa praca zawsze rozbije się na sesje: to zależy od
           tego, kiedy Word zapisze wersję, a tego aplikacja nie kontroluje. Obietnicą jest
           WIĘKSZA SZANSA i mniejsza dziura w danych — i tak trzeba to nazwać. -->
      <div class="info-box auto-sync-help-panel">
        <h3>Sprawdzanie w tle, czyli skąd biorą się godziny na liście</h3>
        <p>
          Aplikacja nie widzi, że piszesz. Widzi tylko to, kiedy Word zapisał kolejną wersję
          dokumentu. Takie zapisy powstają nieregularnie: czasem co kilka minut, a czasem
          dopiero wtedy, gdy zamkniesz plik albo odejdziesz od niego na dłużej. Z ich godzin
          aplikacja odtwarza sesję pracy: pierwszy zapis wyznacza początek, ostatni koniec.
        </p>
        <p>
          <strong>Co się dzieje, gdy zapisów jest mało.</strong> Czas sugestii liczy się
          z tego, co widać, a nie z tego, ile praca trwała naprawdę. Dwa zapisy oddalone
          o kilka minut dadzą sesję długą na te kilka minut, choćbyś pisał znacznie dłużej.
          Skrajny przypadek to jeden jedyny zapis: nie ma wtedy czego zmierzyć, więc sugestia
          dostaje minimum przewidziane w ustawieniach i etykietę „czas do uzupełnienia".
          Poprawianie takich pozycji ręcznie to dokładnie ta robota, której aplikacja ma
          oszczędzać.
        </p>
        <p>
          <strong>Czego to nie gwarantuje.</strong> Niczego na pewno. O tym, kiedy powstaje
          zapis, decyduje Word, nie ta aplikacja. Bywa, że historia wersji sama w sobie jest
          gęsta i sprawdzanie w tle niczego nie poprawi, bo nie ma czego poprawiać. Jego
          przewaga polega na czym innym: przy każdym przebiegu odnotowuje, że plik był
          modyfikowany, niezależnie od tego, kiedy Word domknie wersję. Pomaga więc najbardziej
          tam, gdzie wersji jest mało. Pracy sprzed włączenia nie odtworzy.
        </p>
        <p>
          <strong>Warunek.</strong> Karta z aplikacją musi być otwarta, może leżeć w tle na
          innej zakładce, byle przeglądarka działała. Po jej zamknięciu nic się nie dzieje:
          dostęp do Microsoft 365 żyje wyłącznie w przeglądarce, więc nie ma czym pobrać danych.
        </p>
        <p class="text-muted">
          Jedno sprawdzenie to kilka zapytań: pliki pobierane są przyrostowo, czyli tylko to,
          co się zmieniło, a historia wersji tylko dla zmienionych plików. Gdy nic się nie
          działo, przebieg jest praktycznie pusty i niczego nie zobaczysz. Powiadomienie
          pojawia się tylko wtedy, gdy coś naprawdę przybyło albo się zmieniło.
        </p>
        <div class="actions">
          <button class="btn" (click)="showAutoSyncHelp.set(false)">Zamknij</button>
        </div>
      </div>
    }

    @if (statusFilter() === 'archived') {
      <p class="info-box">
        Zarchiwizowane sugestie nadal chronią przed ponownym utworzeniem tej samej pozycji przy synchronizacji.
      </p>
    }

    <!-- Pasek etapów tylko dla synchronizacji uruchomionej przyciskiem. Przebieg
         automatu ma być niewidoczny; jego stan mieści się w statusie w toolbarze. -->
    @if (manualSync()) {
      <div class="info-box sync-progress">
        <span class="spinner"></span>
        <span>{{ stageLabel() }}</span>
      </div>
    }

    @if (syncReport(); as report) {
      <details class="info-box report" open>
        <summary>
          <strong>{{ headline(report) }}</strong>
        </summary>
        <ul>
          <li>{{ checkedLine(report.fetched, report.windowDays) }}</li>
          @if (report.skippedExisting > 0) {
            <li>{{ skippedLine(report.skippedExisting) }}</li>
          }
          @if (report.filteredOut.total > 0) {
            <li>
              {{ filteredLine(report.filteredOut) }}
              @if (report.skippedNotOfficeNames; as names) {
                <!-- Nazwa zamiast samego licznika: delta OneDrive melduje każdą zmianę
                     na dysku, także w plikach spoza tej aplikacji, więc bez nazwy
                     „pominięto 1 pozycję" wygląda jak wzięte z powietrza. -->
                @if (names.length > 0) {
                  <span class="text-muted">Pominięte pliki: {{ names.join(', ') }}.</span>
                }
              }
            </li>
          }
          @if (report.aggregated > 0) {
            <li>Zwinięto {{ report.aggregated }} {{ plural(report.aggregated, 'dodatkową edycję', 'dodatkowe edycje', 'dodatkowych edycji') }} tego samego pliku w jedną sugestię dziennie.</li>
          }
          @if (report.deduplicated > 0) {
            <li>Scalono {{ report.deduplicated }} {{ plural(report.deduplicated, 'zduplikowaną pozycję', 'zduplikowane pozycje', 'zduplikowanych pozycji') }} z Graph (ten sam element pobrany wielokrotnie).</li>
          }
          @if (report.updated > 0) {
            <li>Zaktualizowano {{ report.updated }} {{ plural(report.updated, 'istniejącą sugestię', 'istniejące sugestie', 'istniejących sugestii') }} (np. po zmianie nazwy pliku, tytułu lub terminu spotkania).</li>
          }
          @if (report.removed > 0) {
            <li>Usunięto {{ report.removed }} {{ plural(report.removed, 'nieaktualną sugestię', 'nieaktualne sugestie', 'nieaktualnych sugestii') }} (spotkania usunięte lub nierozliczalne, skasowane pliki).</li>
          }
          @if (report.created > 0) {
            <li>
              Dopasowanie nowych: {{ report.matched.single }} automatycznie,
              {{ report.matched.ambiguous }} niejednoznacznie,
              {{ report.matched.none }} bez sprawy.
            </li>
          }
        </ul>
      </details>
    }

    @if (error()) {
      <div class="error-box">
        {{ error() }}
        <button class="btn btn-ghost" (click)="loadData()">Spróbuj ponownie</button>
      </div>
    }

    @if (loading()) {
      <p class="empty-state">Ładowanie sugestii…</p>
    } @else {
      @for (suggestion of visibleSuggestions(); track suggestion.id) {
        <!-- Kotwica DOM dla nawigacji z osi czasu (przewiń i podświetl). -->
        <app-suggestion-card
          [id]="'suggestion-' + suggestion.id"
          [suggestion]="suggestion"
          [cases]="cases()"
          (resolved)="onResolved($event)"
          (adjusted)="onAdjusted($event)"
        />
      } @empty {
        @if (statusFilter() === 'rejected') {
          <p class="empty-state">Brak odrzuconych sugestii.</p>
        } @else if (statusFilter() === 'archived') {
          <p class="empty-state">Archiwum jest puste. Zarchiwizowane sugestie pojawią się tutaj.</p>
        } @else {
          <div class="empty-state">
            <p><strong>Brak oczekujących sugestii.</strong></p>
            <p>
              Kliknij „Synchronizuj", aby pobrać spotkania z kalendarza Outlook i dokumenty
              z OneDrive z ostatnich {{ syncDaysDraft }} dni. Aplikacja zamieni je na propozycje wpisów czasu pracy.
            </p>
          </div>
        }
      }
    }
  `,
  styles: `
    .toolbar { display: flex; align-items: center; gap: var(--space-5); margin-bottom: var(--space-4); flex-wrap: wrap; }
    /* Kolumna: przełącznik, a POD nim jego podpis i status. Ustawione w jednym wierszu
       gubiły przynależność — odstęp paska narzędzi (--space-5) odsuwał je na tyle, że
       wyglądały jak osobne kontrolki. */
    .auto-sync-group { display: inline-flex; flex-direction: column; align-items: flex-start; gap: var(--space-1); }
    .auto-sync { display: inline-flex; align-items: center; gap: var(--space-2); cursor: pointer; }
    .auto-sync-sub { display: inline-flex; align-items: baseline; gap: var(--space-1); }
    .auto-sync-status { font-size: var(--font-size-sm); }
    /* Odnośnik pomocniczy: ma się czytać jak podpis pod polem wyboru, nie jak
       kolejny przycisk akcji — stąd brak ramki i mniejszy stopień pisma. */
    .auto-sync-help { padding: 0; height: auto; font-size: var(--font-size-sm); text-decoration: underline; }
    .auto-sync-nudge { display: flex; align-items: center; gap: var(--space-4); flex-wrap: wrap; margin-bottom: var(--space-4); }
    .auto-sync-nudge .nudge-text { flex: 1; min-width: 20rem; }
    .auto-sync-nudge p { margin: var(--space-1) 0 0; }
    .auto-sync-nudge .actions { margin-top: 0; }
    .auto-sync-help-panel { margin-bottom: var(--space-4); }
    .auto-sync-help-panel h3 { font-size: var(--font-size-base); margin: 0 0 var(--space-2); }
    .auto-sync-help-panel p { margin: 0 0 var(--space-2); max-width: 70ch; }
    .auto-sync-help-panel .actions { margin-top: var(--space-2); }
    .filter-group { display: flex; align-items: center; gap: var(--space-1); }
    .bulk-note { font-size: var(--font-size-sm); }
    .sync-progress { display: flex; align-items: center; gap: var(--space-3); }
    .spinner {
      width: 14px; height: 14px; border-radius: 50%;
      border: 2px solid var(--accent-soft); border-top-color: var(--accent);
      animation: spin 0.8s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }
    .report summary { cursor: pointer; }
    .report ul { margin: var(--space-2) 0 0; padding-left: var(--space-5); }
  `,
})
export class SuggestionsPage implements OnInit {
  private api = inject(ApiService);
  private summaryStore = inject(SummaryStore);
  private toasts = inject(ToastService);
  private dataRefresh = inject(DataRefreshService);

  /** Publiczny, bo szablon czyta stan automatu wprost (przełącznik, status, blokada przycisku). */
  protected autoSync = inject(AutoSyncService);

  constructor() {
    // Przeładowanie po operacjach spoza tego widoku (np. "Cofnij" z toastu,
    // który mógł zostać kliknięty już po zmianie zakładki). Własnych powiadomień
    // nie obsługujemy — po swojej operacji ta strona odświeżyła się już sama.
    let lastSeen: number | null = null;
    effect(() => {
      const version = this.dataRefresh.changes();
      if (lastSeen !== null && version !== lastSeen && !this.dataRefresh.isOwn(this)) {
        // Cicho: sprawdzenie w tle chodzi co kilka minut, a podmiana całej listy na
        // „Ładowanie sugestii…" byłaby migotaniem ekranu bez powodu.
        untracked(() => void this.loadData({ quiet: true }));
      }
      lastSeen = version;
    });
  }

  protected suggestions = signal<Suggestion[]>([]);
  protected cases = signal<CaseInfo[]>([]);
  protected sourceFilter = signal<SourceFilter>('all');
  protected statusFilter = signal<StatusFilter>('pending');
  protected loading = signal(false);

  /**
   * Trwa JAKAKOLWIEK synchronizacja — także przebieg automatu. Przycisk musi być wtedy
   * zablokowany, bo drugie równoległe pobieranie biłoby się o wskaźnik delty OneDrive.
   */
  protected syncing = computed(() => this.autoSync.busy());

  /** Ta konkretna strona uruchomiła synchronizację — tylko wtedy pokazujemy pasek etapów. */
  protected manualSync = signal(false);
  protected error = signal<string | null>(null);
  protected syncReport = signal<SyncReport | null>(null);
  protected syncStage = signal<SyncStage | null>(null);

  /** Etap synchronizacji po ludzku — user widzi, że 30-sekundowy sync faktycznie pracuje. */
  protected stageLabel = computed(() => {
    const stage = this.syncStage();
    switch (stage?.kind) {
      case 'calendar':
        return `Pobieram spotkania z kalendarza Outlook (strona ${stage.page})…`;
      case 'files':
        return `Przeglądam pliki na OneDrive (strona ${stage.page})…`;
      case 'versions':
        return `Pobieram historię wersji dokumentów (${stage.done}/${stage.total})…`;
      case 'processing':
        return 'Przetwarzam dane i tworzę sugestie…';
      default:
        return 'Rozpoczynam synchronizację…';
    }
  });

  // Teksty raportu budują czyste, testowalne funkcje modułu — tu tylko aliasy dla szablonu.
  protected readonly headline = syncReportHeadline;
  protected readonly checkedLine = syncCheckedLine;
  protected readonly skippedLine = syncSkippedLine;
  protected readonly filteredLine = filteredOutLine;
  protected readonly plural = polishPlural;
  protected readonly syncDaysOptions = SYNC_DAYS_OPTIONS;

  protected visibleSuggestions = computed(() => {
    const activeFilter = this.sourceFilter();
    const all = this.suggestions();
    return activeFilter === 'all' ? all : all.filter((s) => s.source === activeFilter);
  });

  protected bulkApproving = signal(false);
  protected bulkArchiving = signal(false);

  /** Dwustopniowe potwierdzenie hurtowej archiwizacji — operacja jest jednokierunkowa. */
  protected confirm = new TwoStepConfirm();

  /** Preferencja zakresu synchronizacji — trzymana lokalnie, wysyłana z każdą synchronizacją. */
  protected syncDaysDraft = loadSyncDays();

  /** Sugestie z jednoznacznie dopasowaną sprawą — te można zatwierdzić hurtem, bez zastanowienia. */
  protected autoMatchedCount = computed(() =>
    this.statusFilter() === 'pending' ? bulkApprovable(this.visibleSuggestions()).length : 0,
  );

  /**
   * Dopasowane, ale wyjęte z hurtu, bo czekają na czas. Liczba jest widoczna w pasku
   * narzędzi, a nie tylko w komunikacie po operacji: inaczej licznik przy przycisku
   * byłby po prostu mniejszy od liczby zielonych kart i wyglądałby na błąd.
   */
  protected timeReviewCount = computed(() =>
    this.statusFilter() === 'pending'
      ? this.visibleSuggestions()
        .filter((s) => s.caseId !== null && !s.isAmbiguous && s.needsTimeReview).length
      : 0,
  );

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected setStatusFilter(status: StatusFilter): void {
    this.statusFilter.set(status);
    this.confirm.reset();
    void this.loadData();
  }

  /** Etykieta przycisku hurtowej archiwizacji — uzbrojony pyta „Na pewno?" z liczbą. */
  protected archiveAllLabel(): string {
    const count = this.suggestions().length;
    if (this.confirm.isArmed('archive-rejected')) {
      return `Na pewno? Zarchiwizujesz ${count} ${polishPlural(count, 'sugestię', 'sugestie', 'sugestii')}`;
    }
    return `Archiwizuj wszystkie odrzucone (${count})`;
  }

  protected async archiveAllRejected(event: Event): Promise<void> {
    // Klik nie może dolecieć do document — rozbroiłby potwierdzenie, które właśnie uzbrajamy.
    event.stopPropagation();
    if (!this.confirm.confirm('archive-rejected')) {
      return;
    }

    this.bulkArchiving.set(true);
    this.error.set(null);
    try {
      const result = await this.api.archiveRejectedSuggestions();
      // Bez akcji „Cofnij" — archiwum nie ma unarchive.
      this.toasts.show(archivedSuggestionsToast(result.archivedCount));
      await this.loadData();
      await this.summaryStore.refresh();
      this.dataRefresh.notify(this);
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się zarchiwizować sugestii.'));
    } finally {
      this.bulkArchiving.set(false);
    }
  }

  protected async sync(): Promise<void> {
    this.manualSync.set(true);
    this.error.set(null);
    this.syncReport.set(null);
    this.syncStage.set(null);
    try {
      // Przez AutoSyncService, nie prosto do API: to on trzyma blokadę wspólną
      // z przebiegiem w tle i znacznik ostatniego udanego sprawdzenia.
      const report = await this.autoSync.sync((stage) => this.syncStage.set(stage));
      if (report === null) {
        this.error.set('Synchronizacja właśnie trwa (sprawdzenie w tle), poczekaj chwilę.');
        return;
      }

      this.syncReport.set(report);
      await this.loadData();
      // Synchronizacja zmienia dane globalnie — oś czasu ma pokazać nowe pozycje
      // od razu, bez przeładowania przeglądarki.
      this.dataRefresh.notify(this);
    } catch (error) {
      this.error.set(toUserMessage(error, 'Synchronizacja nie powiodła się.'));
    } finally {
      this.manualSync.set(false);
      this.syncStage.set(null);
    }
  }

  /** Rozwinięte wyjaśnienie sprawdzania w tle — domyślnie zwinięte, pamiętane w sesji widoku. */
  protected showAutoSyncHelp = signal(false);

  protected toggleAutoSync(event: Event): void {
    this.autoSync.setEnabled((event.target as HTMLInputElement).checked);
  }

  /** Status automatu w toolbarze — bez niego przełącznik nie mówi, czy cokolwiek się dzieje. */
  protected autoSyncStatus(): string {
    if (this.autoSync.busy()) {
      return 'sprawdzam…';
    }
    const error = this.autoSync.lastError();
    if (error !== null) {
      return `ostatnia próba nieudana: ${error}`;
    }
    const last = this.autoSync.lastSyncAt();
    return last === null
      ? 'pierwsze sprawdzenie wkrótce'
      : `ostatnio ${last.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' })}`;
  }


  /**
   * Scalenie albo doliczenie luki zmienia więcej niż jedną kartę (znika sugestia
   * składowa, sąsiadowi ubywa wolnego czasu, przesuwa się kolejność) — dlatego pełne
   * przeładowanie listy, a nie punktowa aktualizacja.
   */
  protected onAdjusted(message: string): void {
    // Komunikat idzie z karty, bo tylko ona zna „przed" i „po" operacji — a sama karta
    // znika przy przeładowaniu listy, więc potwierdzenie musi je przeżyć.
    this.toasts.show(message);
    void this.loadData();
    void this.summaryStore.refresh();
    this.dataRefresh.notify(this);
  }

  protected onResolved(event: SuggestionResolved): void {
    // Rozstrzygnięta sugestia znika z bieżącej listy bez ponownego pobierania.
    this.suggestions.update((current) => current.filter((s) => s.id !== event.suggestion.id));
    void this.summaryStore.refresh();
    this.dataRefresh.notify(this);
    this.showResolvedToast(event);
  }

  /**
   * Potwierdzenie akcji z możliwością cofnięcia — karta nie znika "w nicość".
   * Callback undo celowo NIE woła loadData() tego komponentu (mógł już zostać
   * zniszczony po zmianie zakładki) — tylko API + powiadomienie o zmianie danych,
   * na które bieżący widok reaguje przeładowaniem.
   */
  private showResolvedToast(event: SuggestionResolved): void {
    switch (event.action) {
      case 'approved': {
        const entry = event.createdEntry;
        const details = entry
          ? `${formatDuration(entry.durationMinutes)}, ${entry.caseName}`
          : event.suggestion.title;
        this.toasts.show(`Zapisano wpis: ${details}. Zobacz zakładkę „Wpisy czasu".`, {
          undo: entry
            ? async () => {
                await this.api.deleteTimeEntry(entry.id);
                this.dataRefresh.notify();
                await this.summaryStore.refresh();
              }
            : undefined,
        });
        // Osobny komunikat, gdy backend musiał przyciąć godziny wpisu albo wykrył
        // pokrycie z inną pozycją. Kiedyś było to odmową zatwierdzenia; dziś wpis
        // powstaje, ale prawnik ma wiedzieć, czemu godziny wyglądają inaczej.
        if (entry?.notice) {
          this.toasts.show(entry.notice);
        }
        break;
      }
      case 'rejected':
        this.toasts.show(`Odrzucono sugestię „${event.suggestion.title}".`, {
          undo: async () => {
            await this.api.restore(event.suggestion.id);
            this.dataRefresh.notify();
            await this.summaryStore.refresh();
          },
        });
        break;
      case 'restored':
        this.toasts.show('Sugestia wróciła na listę oczekujących.');
        break;
      case 'archived':
        // Bez „Cofnij" — archiwum sugestii jest jednokierunkowe.
        this.toasts.show(`Zarchiwizowano sugestię „${event.suggestion.title}".`);
        break;
    }
  }

  /** Hurtowe zatwierdzenie jednoznacznie dopasowanych — obietnica "jednego kliknięcia" w praktyce. */
  protected async approveAllMatched(): Promise<void> {
    const matched = bulkApprovable(this.visibleSuggestions());
    const skipped = this.timeReviewCount();
    if (matched.length === 0) {
      return;
    }

    this.bulkApproving.set(true);
    this.error.set(null);
    try {
      const results = await Promise.allSettled(
        matched.map((suggestion) =>
          this.api.approve(suggestion.id, {
            caseId: suggestion.caseId!,
            durationMinutes: suggestion.durationMinutes,
            description: suggestion.proposedDescription || suggestion.title,
          }),
        ),
      );

      const approvedCount = results.filter((result) => result.status === 'fulfilled').length;
      const failedCount = results.length - approvedCount;
      this.toasts.show(
        bulkApproveToast(approvedCount, failedCount, skipped),
        { kind: failedCount === 0 ? 'success' : 'error' },
      );

      await this.loadData();
      await this.summaryStore.refresh();
      this.dataRefresh.notify(this);
    } finally {
      this.bulkApproving.set(false);
    }
  }

  protected async loadData(options?: { quiet: boolean }): Promise<void> {
    this.loading.set(options?.quiet !== true);
    this.error.set(null);
    try {
      const [suggestions, cases] = await Promise.all([
        this.api.getSuggestions({ status: this.statusFilter() }),
        this.api.getCases(),
      ]);
      this.suggestions.set(suggestions);
      this.cases.set(cases);
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać danych z backendu.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected saveSyncDays(): void {
    this.syncDaysDraft = normalizedSyncDays(this.syncDaysDraft);
    localStorage.setItem(SYNC_DAYS_STORAGE_KEY, String(this.syncDaysDraft));
  }
}
