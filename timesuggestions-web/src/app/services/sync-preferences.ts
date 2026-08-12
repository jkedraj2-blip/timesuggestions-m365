/**
 * Preferencje synchronizacji trzymane lokalnie (per przeglądarka).
 *
 * Nie ma tu już "domyślnego czasu dokumentu". Była to liczba wzięta znikąd, która
 * trafiała prosto do rozliczenia klienta: Graph nie mówi, ile trwała praca nad plikiem,
 * więc każda wartość w tym polu była zgadywaniem udającym pomiar. Plik bez historii
 * wersji dostaje dziś minimum sesji i jawne „czas do uzupełnienia" — decyzję o czasie
 * podejmuje człowiek, nie ustawienie.
 */

import { SYNC_DAYS_BACK } from './graph-config';

/** Klucz localStorage z preferencją zakresu synchronizacji. */
export const SYNC_DAYS_STORAGE_KEY = 'timesuggestions.syncDaysBack';
/** Zakresy do wyboru w UI — szerzej niż 30 dni rośnie tylko czas pobierania kalendarza. */
export const SYNC_DAYS_OPTIONS = [7, 14, 30] as const;

/** Zakres spoza listy (ręcznie zmieniony localStorage) wraca do domyślnego okna. */
export function normalizedSyncDays(raw: unknown): number {
  const value = Number(raw);
  return (SYNC_DAYS_OPTIONS as readonly number[]).includes(value) ? value : SYNC_DAYS_BACK;
}

export function loadSyncDays(): number {
  return normalizedSyncDays(localStorage.getItem(SYNC_DAYS_STORAGE_KEY));
}
