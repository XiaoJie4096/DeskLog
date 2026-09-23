import test from 'node:test';
import assert from 'node:assert/strict';
import { categoryTotals, previewRange, selectHourlySummary } from '../src/Riji.Web/src/ui-data.ts';
import { activityTime, dayBounds } from '../src/Riji.Web/src/day-time.ts';

const sample = (minute, seconds, name = '开发', color = '#ddb777') => ({
  id: String(minute), utc: new Date(2026, 8, 12, 12, minute).toISOString(), seconds,
  category: { id: 'same-id', name, color }, description: '测试记录'
});
const state = {
  day: '2026-09-12',
  days: [{ day: '2026-09-11', recordCount: 4, sampleSeconds: 480, seconds: 99999 }],
  recognition: { records: [sample(0, 60), sample(5, 300), sample(10, 120)] }
};
test('hour card starts empty, automatic success replaces manual, failure retains last success', () => {
  const start = +new Date('2026-09-12T12:00');
  assert.deepEqual(selectHourlySummary([], start), { latest: undefined, saved: undefined });
  const manual = { id: 'manual', hourly: true, automatic: false, range: { start: new Date(start).toISOString() }, created: new Date(start + 1).toISOString(), state: 'Succeeded', text: '手动摘要' };
  const automatic = { ...manual, id: 'automatic', automatic: true, created: new Date(start + 3600000).toISOString(), text: '整点完整摘要' };
  const otherHour = { ...automatic, range: { start: new Date(start + 3600000).toISOString() } };
  assert.equal(selectHourlySummary([manual, automatic, otherHour], start).saved.id, 'automatic');
  const failed = { ...automatic, id: 'failure', created: new Date(start + 3600001).toISOString(), state: 'Failed', text: null };
  const result = selectHourlySummary([failed, manual], start);
  assert.equal(result.latest.id, 'failure'); assert.equal(result.saved.id, 'manual');
  assert.equal(selectHourlySummary([{ ...manual, hourly: false }], start).saved, undefined);
});
test('category pie preserves historical labels and weights rather than sample counts', () => {
  assert.deepEqual(categoryTotals([sample(0, 60), sample(1, 300), sample(2, 120, '学习'), sample(3, 60, '开发', '#ffffff')]), [
    { name: '开发', color: '#ddb777', seconds: 360 },
    { name: '学习', color: '#ddb777', seconds: 120 },
    { name: '开发', color: '#ffffff', seconds: 60 }
  ]);
  assert.deepEqual(categoryTotals([]), []);
});
test('range preview combines full day recognition weights without application duration', () => {
  assert.deepEqual(previewRange(state, '2026-09-11T00:00', '2026-09-13T00:00'), { count: 7, seconds: 960 });
});
test('partial preview includes start and excludes end exactly', () => {
  assert.deepEqual(previewRange(state, '2026-09-12T12:05', '2026-09-12T12:10'), { count: 1, seconds: 300 });
});
test('unknown partial day is not displayed as zero or a whole-day estimate', () => {
  assert.equal(previewRange(state, '2026-09-11T12:00', '2026-09-12T12:10'), null);
  assert.equal(previewRange(state, '', '2026-09-12T12:10'), null);
  assert.equal(previewRange(state, '2026-09-12T12:10', '2026-09-12T12:00'), null);
  assert.deepEqual(previewRange(state, '2026-09-10T00:00', '2026-09-11T00:00'), { count: 0, seconds: 0 });
});
test('night day and early morning labels follow the selected display mode', () => {
  const settings = { nightMode: true, dayStartHour: 5, extendedHours: false };
  const bounds = dayBounds('2026-09-22', settings);
  assert.equal(bounds.start.getHours(), 5);
  assert.equal(bounds.end.getDate(), 23);
  assert.equal(bounds.end.getHours(), 5);
  const early = new Date(2026, 8, 23, 1, 0).toISOString();
  assert.equal(activityTime(early, '2026-09-22', settings), '次日 01:00');
  assert.equal(activityTime(early, '2026-09-22', { ...settings, extendedHours: true }), '25:00');
  const nightState = { day: '2026-09-22', settings, days: [], recognition: { records: [{ ...sample(0, 60), utc: early }] } };
  assert.deepEqual(previewRange(nightState, '2026-09-22T05:00', '2026-09-23T05:00'), { count: 1, seconds: 60 });
});
