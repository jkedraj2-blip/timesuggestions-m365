import { Injectable, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { Summary } from '../models/api.models';

/**
 * Współdzielony stan kafelków podsumowania. Strony wołają refresh()
 * po każdej akcji zmieniającej dane (sync, zatwierdzenie, odrzucenie, usunięcie),
 * dzięki czemu nagłówek zawsze pokazuje aktualne liczby.
 */
@Injectable({ providedIn: 'root' })
export class SummaryStore {
  private api = inject(ApiService);

  readonly summary = signal<Summary | null>(null);

  async refresh(): Promise<void> {
    try {
      const summary = await this.api.getSummary();
      // Backend zapisuje czas w UTC; po rundzie przez SQLite znacznik "Z" może zniknąć —
      // przywracamy go, żeby przeglądarka poprawnie przeliczyła na czas lokalny.
      if (summary.lastSyncAt && !summary.lastSyncAt.endsWith('Z')) {
        summary.lastSyncAt += 'Z';
      }
      this.summary.set(summary);
    } catch {
      // Kafelki to informacja pomocnicza — błąd ich odświeżenia nie może blokować pracy.
    }
  }
}
