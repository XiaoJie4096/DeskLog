import type { Settings } from './bridge';

export function dayBounds(day: string, settings: Pick<Settings, 'nightMode' | 'dayStartHour'>): { start: Date; end: Date } {
  const hour = settings.nightMode ? settings.dayStartHour : 0;
  const start = new Date(`${day}T00:00:00`);
  start.setHours(hour, 0, 0, 0);
  const end = new Date(start);
  end.setDate(end.getDate() + 1);
  return { start, end };
}

export function activityTime(utc: string | number | Date, day: string, settings: Pick<Settings, 'nightMode' | 'extendedHours'>): string {
  const date = new Date(utc);
  const dayAfter = new Date(`${day}T00:00:00`);
  dayAfter.setDate(dayAfter.getDate() + 1);
  const nextDay = settings.nightMode && date >= dayAfter;
  const hour = String(date.getHours() + (nextDay && settings.extendedHours ? 24 : 0)).padStart(2, '0');
  const minute = String(date.getMinutes()).padStart(2, '0');
  return `${nextDay && !settings.extendedHours ? '次日 ' : ''}${hour}:${minute}`;
}
