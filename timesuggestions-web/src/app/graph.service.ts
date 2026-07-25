import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';

export interface GraphDateTime {
  dateTime: string;
  timeZone: string;
}

export interface GraphEvent {
  id: string;
  subject: string;
  start: GraphDateTime;
  end: GraphDateTime;
  isAllDay: boolean;
  sensitivity: string;
}

@Injectable({ providedIn: 'root' })
export class GraphService {
  private auth = inject(AuthService);

  async getEventsLastDays(days = 7): Promise<GraphEvent[]> {
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
    const response = await fetch(
      `https://graph.microsoft.com/v1.0/me/calendarView?${params}`,
      {
        headers: {
          Authorization: `Bearer ${token}`,
          Prefer: 'outlook.timezone="Central European Standard Time"',
        },
      },
    );

    if (!response.ok) {
      throw new Error(`Graph ${response.status}: ${await response.text()}`);
    }

    const body = await response.json();
    return body.value as GraphEvent[];
  }
}
