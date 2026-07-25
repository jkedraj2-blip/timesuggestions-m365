import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphEvent } from '../models/graph.models';
import { GRAPH_BASE_URL, OUTLOOK_TIMEZONE } from './graph-config';

@Injectable({ providedIn: 'root' })
export class GraphCalendarService {
  private auth = inject(AuthService);

  /**
   * Widok kalendarza z jawnym zakresem dat — w odróżnieniu od listy wydarzeń
   * rozwija serie spotkań cyklicznych na pojedyncze wystąpienia.
   */
  async getEventsLastDays(days: number): Promise<GraphEvent[]> {
    const end = new Date();
    const start = new Date(end.getTime() - days * 24 * 60 * 60 * 1000);

    const params = new URLSearchParams({
      startDateTime: start.toISOString(),
      endDateTime: end.toISOString(),
      $select: 'id,subject,start,end,isAllDay,sensitivity',
      $orderby: 'start/dateTime',
      $top: '50',
    });

    const token = await this.auth.getToken();
    const response = await fetch(`${GRAPH_BASE_URL}/me/calendarView?${params}`, {
      headers: {
        Authorization: `Bearer ${token}`,
        Prefer: `outlook.timezone="${OUTLOOK_TIMEZONE}"`,
      },
    });

    if (!response.ok) {
      throw new Error(`Nie udało się pobrać kalendarza (Graph ${response.status}).`);
    }

    const body = await response.json();
    return body.value as GraphEvent[];
  }
}
