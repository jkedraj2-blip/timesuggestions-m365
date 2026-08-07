import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphCalendarResponse, GraphEvent } from '../models/graph.models';
import { GRAPH_BASE_URL, OUTLOOK_TIMEZONE } from './graph-config';
import { fetchGraphPage } from './graph-http';

@Injectable({ providedIn: 'root' })
export class GraphCalendarService {
  private auth = inject(AuthService);

  /**
   * Widok kalendarza z jawnym zakresem dat — w odróżnieniu od listy wydarzeń
   * rozwija serie spotkań cyklicznych na pojedyncze wystąpienia.
   * calendarView stronicuje wyniki ($top to rozmiar strony, nie limit całości) —
   * podążamy za @odata.nextLink aż do końca, inaczej część spotkań po cichu przepada.
   */
  async getEventsLastDays(days: number, onPage?: (page: number) => void): Promise<GraphEvent[]> {
    const end = new Date();
    const start = new Date(end.getTime() - days * 24 * 60 * 60 * 1000);

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
        throw new Error(`Nie udało się pobrać kalendarza (Graph ${response.status}).`);
      }

      const body: GraphCalendarResponse = await response.json();
      events.push(...body.value);
      url = body['@odata.nextLink'];
    }

    return events;
  }
}
