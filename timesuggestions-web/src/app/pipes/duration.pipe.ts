import { Pipe, PipeTransform } from '@angular/core';

/** Formatuje minuty po ludzku: 90 → "1 godz. 30 min", 45 → "45 min". */
export function formatDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;

  if (hours === 0) {
    return `${remainingMinutes} min`;
  }
  if (remainingMinutes === 0) {
    return `${hours} godz.`;
  }
  return `${hours} godz. ${remainingMinutes} min`;
}

/** Wariant dla szablonów — kod TS używa bezpośrednio formatDuration. */
@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(minutes: number): string {
    return formatDuration(minutes);
  }
}
