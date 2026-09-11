import test from 'node:test';
import assert from 'node:assert/strict';
import { groupSamplesByHour, sampleGaps } from '../src/Riji.Web/src/record-gaps.ts';

const sample = (id, minute, seconds = 60) => ({ id, utc: new Date(Date.UTC(2026, 8, 11, 13, minute)).toISOString(), seconds });
test('twenty minute observation gap is explicit without inventing coverage', () => {
  const records = [sample('b', 20), sample('a', 0)];
  const gap = sampleGaps(records).get('b');
  assert.equal(gap.elapsedSeconds, 1200);
  assert.equal(gap.start, records[1].utc);
  assert.equal(records[0].id, 'b');
});
test('normal sampling and interval changes do not create artificial gaps', () => {
  assert.equal(sampleGaps([sample('a', 0), sample('b', 1), sample('c', 3, 120)]).size, 0);
  assert.equal(sampleGaps([]).size, 0);
  assert.equal(sampleGaps([sample('a', 0)]).size, 0);
});
test('hour groups sort records and conserve sample weights across hour boundaries', () => {
  const a = { id: 'a', utc: new Date(2026, 8, 11, 12, 59).toISOString(), seconds: 120 };
  const b = { id: 'b', utc: new Date(2026, 8, 11, 13, 20).toISOString(), seconds: 60 };
  const c = { id: 'c', utc: new Date(2026, 8, 11, 13, 21).toISOString(), seconds: 60 };
  const groups = groupSamplesByHour([c, a, b]);
  assert.deepEqual(groups.map(group => group.hour), [12, 13]);
  assert.deepEqual(groups.map(group => group.seconds), [120, 120]);
  assert.deepEqual(groups[1].records.map(record => record.id), ['b', 'c']);
  assert.equal(sampleGaps([a, b, c]).get('b').elapsedSeconds, 1260);
});
