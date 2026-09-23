export type SampleTime = { id: string; utc: string; seconds: number };
export type SampleGap = { afterId: string; beforeId: string; start: string; end: string; elapsedSeconds: number };

export function groupSamplesByHour<T extends SampleTime>(records: T[]): { hour: number; start: number; records: T[]; seconds: number }[] {
  const groups = new Map<number, { hour: number; start: number; records: T[]; seconds: number }>();
  for (const record of [...records].sort((left, right) => Date.parse(left.utc) - Date.parse(right.utc))) {
    const date = new Date(record.utc);
    const hour = date.getHours();
    const start = +date - (date.getMinutes() * 60 + date.getSeconds()) * 1000 - date.getMilliseconds();
    let group = groups.get(start);
    if (!group) { group = { hour, start, records: [], seconds: 0 }; groups.set(start, group); }
    group.records.push(record); group.seconds += record.seconds;
  }
  return [...groups.values()];
}

// Mark missing observations, never turn sampling weights into continuous activity intervals.
export function sampleGaps(records: SampleTime[]): Map<string, SampleGap> {
  const ordered = [...records].sort((left, right) => Date.parse(left.utc) - Date.parse(right.utc));
  const gaps = new Map<string, SampleGap>();
  for (let index = 1; index < ordered.length; index++) {
    const previous = ordered[index - 1], next = ordered[index];
    const elapsed = (Date.parse(next.utc) - Date.parse(previous.utc)) / 1000;
    if (elapsed > Math.max(previous.seconds, next.seconds) + 2) {
      gaps.set(next.id, { afterId: previous.id, beforeId: next.id, start: previous.utc, end: next.utc, elapsedSeconds: elapsed });
    }
  }
  return gaps;
}
