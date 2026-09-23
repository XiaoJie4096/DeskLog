import type { Snapshot, SummaryHeader } from './bridge';
import { dayBounds } from './day-time.ts';

// Failed replacements keep the last successful text visible.
export function selectHourlySummary(summaries: SummaryHeader[], start: number) {
  const attempts = summaries.filter(s => s.hourly && +new Date(s.range.start) === start)
    .sort((a, b) => +new Date(b.created) - +new Date(a.created));
  return { latest: attempts[0], saved: attempts.find(s => s.state === 'Succeeded' && s.text) };
}

// Preserve category snapshots and sample weights when classifications change.
export function categoryTotals(records: Snapshot['recognition']['records']) {
  const groups = new Map<string, { name: string; color: string; seconds: number }>();
  for (const record of records) {
    const key = JSON.stringify([record.category.id, record.category.name, record.category.color]);
    const group = groups.get(key) ?? { name: record.category.name, color: record.category.color, seconds: 0 };
    group.seconds += record.seconds;
    groups.set(key, group);
  }
  return [...groups.values()].sort((a, b) => b.seconds - a.seconds);
}

// Use daily totals only for whole local days; partial days need actual samples.
export function previewRange(state: Pick<Snapshot, 'day' | 'days' | 'recognition'> & Partial<Pick<Snapshot, 'settings'>>, startText: string, endText: string) {
  const start = new Date(startText), end = new Date(endText);
  if (!Number.isFinite(+start) || !Number.isFinite(+end) || start >= end) return null;
  const settings = state.settings ?? { nightMode: false, dayStartHour: 5 };
  const first = new Date(start);
  if (settings.nightMode && first.getHours() < settings.dayStartHour) first.setDate(first.getDate() - 1);
  const firstDay = first.toLocaleDateString('sv-SE');
  let cursor = dayBounds(firstDay, settings).start;
  let count = 0, seconds = 0, iterations = 0;
  while (cursor < end) {
    if (++iterations > 3660) return null;
    const day = cursor.toLocaleDateString('sv-SE');
    const next = dayBounds(day, settings).end;
    if (day === state.day) {
      const records = state.recognition.records.filter(r => +new Date(r.utc) >= +start && +new Date(r.utc) < +end);
      count += records.length; seconds += records.reduce((n, r) => n + r.seconds, 0);
    } else if (start <= cursor && end >= next) {
      const totals = state.days.find(d => d.day === day);
      count += totals?.recordCount ?? 0; seconds += totals?.sampleSeconds ?? 0;
    } else return null;
    cursor = next;
  }
  return { count, seconds };
}
