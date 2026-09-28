import { useEffect, useState } from 'react';
import { command, type Snapshot, type JobPage } from './bridge';
import './recognition.css';
import { SummaryText } from './SummaryText';
import { selectHourlySummary } from './ui-data';
import { groupSamplesByHour, sampleGaps } from './record-gaps';
import { activityTime, dayBounds } from './day-time';

export function RecognitionJobs() {
  const [page, setPage] = useState<JobPage | null>(null);
  const [offset, setOffset] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const labels: Record<string, string> = { Capturing: '正在采集', Pending: '等待识别', Running: '正在识别', Retry: '等待重试', Manual: '需手动重试', Succeeded: '识别成功，待处理截图' };
  async function load(next: number) {
    setBusy(true); setError('');
    try { setPage(await command<JobPage>('recognitionJobs', { offset: next })); setOffset(next); }
    catch (error) { setError(error instanceof Error ? error.message : '任务读取失败'); }
    finally { setBusy(false); }
  }
  async function retry() {
    setBusy(true); setError('');
    try { await command('retryRecognition'); await load(0); }
    catch (error) { setError(error instanceof Error ? error.message : '重试任务失败'); setBusy(false); }
  }
  async function openLog() {
    setBusy(true); setError('');
    try { await command('openRecognitionLog'); }
    catch (error) { setError(error instanceof Error ? error.message : '故障详情打开失败'); }
    finally { setBusy(false); }
  }
  return <details className="recognition-jobs" onToggle={event => { if (event.currentTarget.open && !page && !busy) void load(0); }}><summary>查看未完成任务与截图清理</summary>
    <p>按采样时间倒序显示；点击刷新查看最新状态。任务超过 24 小时仍未完成时自动失效并清理截图。</p>
    <div className="summary-actions"><button disabled={busy} onClick={() => void load(0)}>刷新任务</button>{page?.items.some(item => item.status === 'Retry' || item.status === 'Manual') && <button disabled={busy} onClick={() => void retry()}>重试可处理任务</button>}<button disabled={busy} onClick={() => void openLog()}>查看故障详情</button></div>
    {error && <p role="alert">{error}</p>}
    {page && <><p>共 {page.total} 个 · 当前 {page.items.length ? offset + 1 : 0}–{offset + page.items.length}</p>
      {page.items.map(job => <article key={job.id}><strong>{job.waitForConnection ? '等待 AI 服务连接' : labels[job.status] ?? job.status}</strong><p>{new Date(job.utc).toLocaleString('zh-CN')} · 已尝试 {job.attempts} 次</p>{job.error && <p>{job.error}</p>}{job.status === 'Retry' && job.retryAt && <small>最早重试时间：{new Date(job.retryAt).toLocaleString('zh-CN')}</small>}</article>)}
      {!page.total && <p>没有未完成任务或待处理的截图。</p>}
      <div className="summary-actions"><button disabled={busy || offset === 0} onClick={() => void load(Math.max(0, offset - 50))}>上一页</button><button disabled={busy || offset + page.items.length >= page.total} onClick={() => void load(offset + 50)}>下一页</button></div>
    </>}
  </details>;
}

export function RecognitionTimeline({ state }: { state: Snapshot }) {
  const records = state.recognition.records;
  const [hour, setHour] = useState('all');
  const [busy, setBusy] = useState<number | null>(null);
  const [error, setError] = useState('');
  useEffect(() => { setError(''); }, [state.day, hour]);
  useEffect(() => { setHour('all'); }, [state.day, state.settings.nightMode, state.settings.dayStartHour]);
  async function summarize(selectedHour: number, retryId?: string) {
    const start = new Date(selectedHour);
    setBusy(selectedHour); setError('');
    try { if (retryId) await command('summaryRetry', { summaryId: retryId }); else await command('hourSummaryGenerate', { start: start.toISOString() }); }
    catch (error) { setError(error instanceof Error ? error.message : '时段摘要生成失败'); }
    finally { setBusy(null); }
  }
  const { start: dayStart, end: dayEnd } = dayBounds(state.day, state.settings);
  const hours: number[] = [];
  for (let cursor = +dayStart; cursor < +dayEnd; cursor += 3600000) hours.push(cursor);
  const visible = records.filter(record => {
    if (hour === 'all') return true;
    const date = new Date(record.utc);
    return +date - (date.getMinutes() * 60 + date.getSeconds()) * 1000 - date.getMilliseconds() === Number(hour);
  });
  const gaps = sampleGaps(records);
  const groups = groupSamplesByHour(visible).reverse();
  return <section className="review-timeline history"><div className="section-head"><h2>活动片段</h2><label>查看小时 <select value={hour} onChange={e => setHour(e.target.value)}><option value="all">全天</option>{hours.map(start => <option key={start} value={start}>{activityTime(start, state.day, state.settings)}</option>)}</select></label></div>
    <p>每个整点，成功识别累计达到 {state.settings.hourlyMinimumMinutes} 分钟的时段自动生成摘要；不足门槛可手动生成。</p>
    {(error || state.hourlySummaryError) && <p role="alert">{error || state.hourlySummaryError}</p>}
    {groups.length ? groups.map(group => {
      const start = group.start;
      const { latest, saved } = selectHourlySummary(state.summaries, start);
      const generating = busy === start || latest?.state === 'Running';
      return <section className="hour-group" key={start}>
        <div className="section-head hour-heading"><h3>{activityTime(start, state.day, state.settings)} — {activityTime(start + 3600000, state.day, state.settings)}</h3><span>{Math.round(group.seconds / 60)} 分钟 · {group.records.length} 次识别</span><button disabled={busy !== null || !state.recognition.configured} onClick={() => void summarize(start)}>{generating ? '正在生成…' : '生成时段摘要'}</button></div>
        {saved?.text ? <div className="hour-overview"><SummaryText text={saved.text} /></div> : <p className="hour-placeholder">{generating ? '正在整理本时段的活动记录…' : '暂无时段摘要，点击“生成时段摘要”开始整理。'}</p>}
        {saved && generating && <small role="status">正在生成新版摘要，完成后更新。</small>}
        {generating && latest && <button onClick={() => void command('summaryCancel', { summaryId: latest.id }).catch(error => setError(error.message))}>取消生成</button>}
        {latest && (latest.state === 'Failed' || latest.state === 'Cancelled') && <p role="alert">{latest.automatic ? '自动摘要' : '时段摘要'}{latest.state === 'Cancelled' ? '已取消' : '生成失败'}：{latest.error}{saved ? '。仍显示上次成功生成的摘要。' : ''} <button disabled={busy !== null || !state.recognition.configured} onClick={() => void summarize(start, latest.id)}>重试摘要</button></p>}
        {!state.recognition.configured && <small>请先在设置中验证 AI 服务。</small>}
        <div className="hour-categories">{Array.from(group.records.reduce((map, record) => { const key = record.category.name; map.set(key, (map.get(key) ?? 0) + record.seconds); return map; }, new Map<string, number>())).map(([name, seconds]) => <span key={name}>{name} · {Math.round(seconds / 60)} 分钟</span>)}{saved && <small className="hour-generated">{saved.automatic ? '整点自动生成' : '手动生成'} · {new Date(saved.created).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })}</small>}</div>
        <details className="hour-records"><summary>展开 {group.records.length} 条原始记录</summary><ol className="recognition-timeline">{[...group.records].reverse().map(record => <li key={record.id}><time>{activityTime(record.utc, state.day, state.settings)}</time><div>{gaps.has(record.id) && <p className="sample-gap">记录缺口：{activityTime(gaps.get(record.id)!.start, state.day, state.settings)} 至 {activityTime(record.utc, state.day, state.settings)}，两次采样相隔 {Math.round(gaps.get(record.id)!.elapsedSeconds / 60)} 分钟。之间未保存其他成功采样。</p>}<span className="category-label" style={{ borderColor: record.category.color }}>{record.category.name}</span><p>{record.description}</p></div></li>)}</ol></details>
      </section>;
    }) : <div className="empty">这段时间没有成功识别记录。<small>空白不代表没有活动，日迹不会补写未知内容。</small></div>}
  </section>;
}




