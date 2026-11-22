import type { Snapshot, SummaryHeader } from './bridge';
import { activityTime, dayBounds } from './day-time';
import { formatTime } from './DesignedPages';
import { SummaryText } from './SummaryText';
import { categoryDisplayColor } from './ui-data';

type Props = {
  state: Snapshot;
  busy: boolean;
  run: (type: string, payload: Record<string, unknown>) => Promise<boolean>;
};

// Prefer wider coverage, regardless of when a delayed request finishes.
function compareCoverage(a: SummaryHeader, b: SummaryHeader) {
  return +new Date(b.range.end) - +new Date(a.range.end) || +new Date(b.created) - +new Date(a.created);
}

// Label the supported time window of the displayed summary.
function coverageLabel(summary: SummaryHeader, state: Snapshot) {
  const { start, end } = dayBounds(state.day, state.settings);
  if (+new Date(summary.range.start) === +start && +new Date(summary.range.end) === +end) return '全天';
  return `截至 ${activityTime(summary.range.end, state.day, state.settings)}`;
}

// Display saved category reviews alongside manual generation and retry controls.
export function CategoryReviews({ state, busy, run }: Props) {
  const categories = new Map<string, { id: string; name: string; color: string; seconds: number }>();
  for (const record of state.recognition.records) {
    const { id, name } = record.category;
    const current = categories.get(id);
    categories.set(id, { id, name, color: categoryDisplayColor(record.category, state.recognition.categories), seconds: (current?.seconds ?? 0) + record.seconds });
  }
  const groups = [...categories.values()].sort((a, b) => b.seconds - a.seconds);
  if (!groups.length) return null;
  const reviews = [...(state.categoryReviews ?? [])].sort(compareCoverage);

  return <section className="panel category-reviews" aria-labelledby="category-reviews-title">
    <h2 id="category-reviews-title">分类回顾</h2>
    {state.categoryReviewError && <p role="alert">{state.categoryReviewError}</p>}
    {groups.map(({ id, name, color, seconds }, index) => {
      const results = reviews.filter(item => item.categoryReview?.categoryId === id);
      const latest = results[0];
      const saved = results.find(item => item.state === 'Succeeded' && item.text);
      const running = results.some(item => item.state === 'Running');
      const failed = latest?.state === 'Failed' || latest?.state === 'Cancelled';
      return <details key={id} className="category-review" open={index === 0 ? true : undefined}>
        <summary><span className="category-review-dot" style={{ background: color }} /><strong>{name}</strong><small>{formatTime(seconds)}{saved ? ` · ${coverageLabel(saved, state)}` : ''}</small></summary>
        <div className="category-review-body">
          {saved?.text && <SummaryText text={saved.text} />}
          {!saved && !running && !failed && <p className="category-review-status">{state.recognition.configured ? '还没有摘要。' : '请在设置中配置 AI 后生成摘要。'}</p>}
          {running && <p className="category-review-status" role="status">正在生成摘要…</p>}
          {failed && <p className="category-review-status" role="status">{latest.error ?? '摘要生成未完成。'}</p>}
          <div className="category-review-actions">
            {failed && <button disabled={busy || running || !state.recognition.configured || state.maintenance}
              onClick={() => void run('summaryRetry', { summaryId: latest.id })}>重试</button>}
            <button disabled={busy || running || !state.recognition.configured || state.maintenance}
              onClick={() => void run('categoryReviewGenerate', { day: state.day, categoryId: id })}>{saved ? '重新生成' : '手动生成'}</button>
          </div>
        </div>
      </details>;
    })}
  </section>;
}
