import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphCalendarResponse, GraphEvent } from '../models/graph.models';
import { FETCH_MARGIN_DAYS, GRAPH_BASE_URL, OUTLOOK_TIMEZONE } from './graph-config';
import { fetchGraphPage } from './graph-http';

/** Wynik pobierania kalendarza wraz z deklaracją kompletności snapshotu. */
export interface CalendarSnapshot {
  events: GraphEvent[];
  /**
   * true WYŁĄCZNIE po przejściu wszystkich stron @odata.nextLink bez błędu —
   * tylko wtedy backend może destrukcyjnie rekonsyliować kalendarz (usuwać
   * oczekujące sugestie spotkań nieobecnych w payloadzie).
   */
  snapshotComplete: boolean;
}

@Injectable({ providedIn: 'root' })
export class GraphCalendarService {
  private auth = inject(AuthService);

  /**
   * Widok kalendarza z jawnym zakresem dat — w odróżnieniu od listy wydarzeń
   * rozwija serie spotkań cyklicznych na pojedyncze wystąpienia.
   * calendarView stronicuje wyniki ($top to rozmiar strony, nie limit całości) —
   * podążamy za @odata.nextLink aż do końca, inaczej część spotkań po cichu przepada.
   */
  async getEventsLastDays(days: number, onPage?: (page: number) => void): Promise<CalendarSnapshot> {
    const end = new Date();
    // Doba zapasu ponad okno backendu (FETCH_MARGIN_DAYS) — patrz komentarz w graph-config.
    const start = new Date(end.getTime() - (days + FETCH_MARGIN_DAYS) * 24 * 60 * 60 * 1000);

    const params = new URLSearchParams({
      startDateTime: start.toISOString(),
      endDateTime: end.toISOString(),
      $select: 'id,subject,start,end,isAllDay,sensitivity,isCancelled',
      $orderby: 'start/dateTime',
      $top: '50',
    });

    let url: string | undefined = `${GRAPH_BASE_URL}/me/calendarView?${params}`;
    const events: GraphEvent[] = [];
    let page = 0;

    while (url) {
      page++;
      onPage?.(page);
      const response = await fetchGraphPage(url, () => this.auth.getToken(), {
        Prefer: `outlook.timezone="${OUTLOOK_TIMEZONE}"`,
      });

      if (!response.ok) {
        // Pierwsza strona: awaria jest najpewniej systemowa (uprawnienia, token) —
        // użytkownik musi zobaczyć błąd zamiast pustej, "udanej" synchronizacji.
        if (page === 1) {
          throw new Error(`Nie udało się pobrać kalendarza (Graph ${response.status}).`);
        }

        // Kolejna strona padła: zwracamy częściowy snapshot BEZ deklaracji
        // kompletności — backend nie uzna brakujących spotkań za usunięte.
        return { events, snapshotComplete: false };
      }

      const body: GraphCalendarResponse = await response.json();
      events.push(...body.value);
      url = body['@odata.nextLink'];
    }

    return { events, snapshotComplete: true };
  }
}
