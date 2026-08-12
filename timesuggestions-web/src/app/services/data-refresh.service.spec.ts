import { describe, expect, it } from 'vitest';
import { DataRefreshService } from './data-refresh.service';

describe('DataRefreshService', () => {
  it('licznik zmian rośnie z każdym powiadomieniem', () => {
    const service = new DataRefreshService();
    const before = service.changes();

    service.notify();

    expect(service.changes()).toBe(before + 1);
  });

  it('inicjator rozpoznaje własne powiadomienie i nie ładuje danych drugi raz', () => {
    const service = new DataRefreshService();
    const page = {};

    service.notify(page);

    expect(service.isOwn(page)).toBe(true);
    expect(service.isOwn({})).toBe(false);
  });

  it('powiadomienie bez inicjatora (np. sprawdzenie w tle) odświeża wszystkich', () => {
    const service = new DataRefreshService();
    const page = {};
    service.notify(page);

    service.notify();

    expect(service.isOwn(page)).toBe(false);
  });
});
