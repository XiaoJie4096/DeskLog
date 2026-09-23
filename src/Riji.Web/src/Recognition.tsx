import { useEffect, useState } from 'react';
import { command, type Snapshot, type Category, type CaptureSettings, type JobPage } from './bridge';
import './recognition.css';
import { SummaryText } from './SummaryText';
import { selectHourlySummary } from './ui-data';
import { groupSamplesByHour, sampleGaps } from './record-gaps';
import { activityTime, dayBounds } from './day-time';

export function RecognitionSettings({ state, section }: { state: Snapshot; section: 'capture' | 'categories' }) {
  const current = state.recognition;
  const [endpoint, setEndpoint] = useState(current.endpoint ?? '');
  const [model, setModel] = useState(current.model ?? '');
  const [summaryModel, setSummaryModel] = useState(current.summaryModel ?? current.model ?? '');
  const [summaryModelEdited, setSummaryModelEdited] = useState(false);
  const [contextK, setContextK] = useState(100);
  const [models, setModels] = useState<{ id: string; contextK: number | null }[]>([]);
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [categories, setCategories] = useState<Category[]>(current.categories);
  const [prompt, setPrompt] = useState(current.settings.prompt ?? current.defaultPrompt);
  useEffect(() => setPrompt(current.settings.prompt ?? current.defaultPrompt), [current.settings.prompt, current.defaultPrompt]);
  const categorySource = JSON.stringify(current.categories);
  useEffect(() => setCategories(current.categories), [categorySource]);
  async function run(type: string, payload: Record<string, unknown>, success = '已保存') {
    setBusy(true); setMessage('');
    try { await command(type, payload); setMessage(success); }
    catch (error) { setMessage(error instanceof Error ? error.message : '操作失败'); }
    finally { setBusy(false); }
  }
  const configure = (patch: Partial<CaptureSettings>) => run('captureSettings', { settings: { ...current.settings, ...patch } });
  const editCategory = (index: number, patch: Partial<Category>) => setCategories(items => items.map((item, i) => i === index ? { ...item, ...patch } : item));
  return <>
    {section === 'capture' && <section className="panel history settings recognition-settings"><h2>截图与识别</h2>
      <p>截取所有显示器的完整桌面，发送至你配置的 AI 服务，保存活动描述和分类。默认关闭。</p>
      {message && <p role="status" className="notice">{message}</p>}
      <label className="setting"><span>启用截图识别<small>按间隔发送真实截图；离开、不识屏、系统锁屏或休眠时暂停。</small></span><input type="checkbox" disabled={busy || !current.configured} checked={current.settings.enabled} onChange={e => void configure({ enabled: e.target.checked })} /></label>
      <label className="setting"><span>采样间隔<small>成功识别时长按任务创建时的间隔累计，与应用使用时长分开。</small></span><select disabled={busy} value={current.settings.intervalSeconds} onChange={e => void configure({ intervalSeconds: Number(e.target.value) })}>{[60, 120, 300].map(seconds => <option value={seconds} key={seconds}>{seconds / 60} 分钟</option>)}</select></label>
      <label className="setting"><span>保留成功截图<small>默认在结果保存成功后删除。失败任务的截图最多保留 24 小时。</small></span><input type="checkbox" disabled={busy} checked={current.settings.keepImages} onChange={e => void configure({ keepImages: e.target.checked })} /></label>
      <p>{current.configured ? `有效配置：${current.model}` : '尚未验证 AI 配置'} · {current.busy ? '正在处理' : current.paused ? '授权异常，已暂停' : current.settings.enabled ? '等待下一次采样' : '截图关闭'}</p>
      {current.error && <p role="alert">{current.error}</p>}
      <p>未完成任务：{current.jobs.filter(job => job.status !== 'Succeeded').reduce((sum, job) => sum + job.count, 0)} 个</p>
      <p>最近成功记录的采样时间：{current.latestSample ? new Date(current.latestSample).toLocaleString('zh-CN') : '暂无成功记录'}</p>
      <RecognitionJobs />
      <button disabled={busy || current.busy} onClick={() => void run('retryRecognition', {}, '失败任务已加入待处理队列；恢复识别后继续。')}>重试失败任务与截图清理</button>
      <button disabled={busy} onClick={() => void run('openRecognitionLog', {}, '已打开最近一次截图识别故障报告。')}>查看最近识别故障详情</button>
      <form className="ai-form" onSubmit={event => { event.preventDefault(); void configure({ prompt }); }}>
        <h3>截图识别提示词</h3>
        <p>设置描述重点、表达方式和分类原则。程序会自动附加启用分类的名称与说明、返回格式和采样时的前台辅助信息。</p>
        <label>识别规则<textarea required maxLength={10000} rows={15} value={prompt} disabled={busy} onChange={event => setPrompt(event.target.value)} /></label>
        <small>保存后用于新采样和配置验证；已有任务重试沿用采样时的提示词、分类与辅助信息。</small>
        <p>浏览器当前标签页标题由扩展提供，需在“浏览器与隐私”开启“记录网页标题”。未连接、信息过期或隐私过滤时不附带标题。</p>
        <div className="summary-actions"><button className="primary" disabled={busy || !prompt?.trim()}>保存提示词</button><button type="button" disabled={busy} onClick={() => setPrompt(current.defaultPrompt)}>恢复默认</button></div>
        <small>恢复默认后点击“保存提示词”生效。</small>
      </form>
      <form className="ai-form" onSubmit={event => { event.preventDefault(); const submittedKey = key; setKey(''); const selectedSummaryModel = summaryModelEdited ? summaryModel.trim() : model.trim(); void run('testAi', { endpoint, model, summaryModel: selectedSummaryModel, contextK, key: submittedKey }, '真实图片和两个模型验证成功，配置已启用。截图开关保持原设置。'); }}>
        <h3>AI 服务配置</h3><p>填写支持图片的 Chat Completions 接口。验证失败时保留旧有效配置。</p>
        <label>接口地址<input type="url" required value={endpoint} onChange={e => { setEndpoint(e.target.value); }} placeholder="https://服务地址/v1" disabled={busy} /></label>
        <label>API Key<input type="password" autoComplete="off" required={!current.configured} value={key} placeholder={current.configured ? '********（已保存，留空沿用）' : '请输入 API Key'} onChange={e => setKey(e.target.value)} disabled={busy} /><small>已保存的 Key 不会回传到界面；留空会沿用本机保存的 Key，输入新值可替换。</small></label>
        <button type="button" className="secondary-action" disabled={busy || !endpoint || (!key && !current.configured)} onClick={async () => { try { const list = await command<{ id: string; contextK: number | null }[]>('models', { endpoint, key }); setModels(list); setMessage('已读取 ' + list.length + ' 个模型'); } catch (error) { setMessage(error instanceof Error ? error.message : '读取模型失败'); } }}>获取模型信息</button>
        <label>识图模型{models.length ? <select value={model} onChange={e => { const value = e.target.value; setModel(value); if (!summaryModelEdited) setSummaryModel(value); const hit = models.find(item => item.id === value); if (hit?.contextK) setContextK(hit.contextK); }} disabled={busy}>{models.map(item => <option key={item.id}>{item.id}</option>)}</select> : <input required maxLength={200} value={model} onChange={e => { const value = e.target.value; setModel(value); if (!summaryModelEdited) setSummaryModel(value); }} disabled={busy} />}</label>
        <label>总结（对话）模型{models.length ? <select value={summaryModel} onChange={e => { setSummaryModelEdited(true); setSummaryModel(e.target.value); }} disabled={busy}>{models.map(item => <option key={item.id}>{item.id}</option>)}</select> : <input maxLength={200} value={summaryModel} onChange={e => { setSummaryModelEdited(true); setSummaryModel(e.target.value); }} disabled={busy} />}<small>用于时段摘要、AI 总结和后续对话；未单独修改时自动跟随识图模型。</small></label>
        <label>上下文长度（K token）<input type="number" min="4" max="150" value={contextK} onChange={e => setContextK(Number(e.target.value))} disabled={busy} /><small>默认 100K。程序会为提示词和输出预留空间。</small></label>
        <button type="button" disabled={busy || !endpoint || !model} onClick={() => void run('saveAi', { endpoint, model, summaryModel: summaryModelEdited ? summaryModel : model, contextK, key }, 'AI 配置已保存。')}>保存配置</button>
        {models.length > 0 && <small>已读取 {models.length} 个模型；接口提供上下文长度时，选择模型会自动填入。</small>}
        <button className="primary" disabled={busy || current.busy}>{busy ? '正在验证…' : '发送真实截图并验证配置'}</button>
      </form>
    </section>}
    {section === 'categories' && <section className="panel history settings"><h2>活动分类</h2><p>按活动目的分类。修改、停用或删除只影响新采样，历史记录保留当时的分类快照。编辑后点击“保存分类”生效。</p>
      <form onSubmit={event => { event.preventDefault(); void run('categories', { categories }); }}>
        <div className="category-editor">{categories.map((category, index) => <div className="category-edit" key={category.id}>
          <input aria-label={`启用分类 ${category.name}`} type="checkbox" checked={category.enabled} onChange={e => editCategory(index, { enabled: e.target.checked })} />
          <input aria-label="分类颜色" type="color" value={category.color} onChange={e => editCategory(index, { color: e.target.value })} />
          <input aria-label="分类名称" required maxLength={10} value={category.name} onChange={e => editCategory(index, { name: e.target.value })} />
          <input aria-label="分类含义" required maxLength={80} value={category.meaning} onChange={e => editCategory(index, { meaning: e.target.value })} />
          <button type="button" disabled={busy} aria-label={`删除分类 ${category.name}`} onClick={() => setCategories(items => items.filter(item => item.id !== category.id))}>删除</button>
        </div>)}</div><div className="category-actions"><button type="button" disabled={categories.length >= 100} onClick={() => setCategories(items => [...items, { id: crypto.randomUUID(), name: '', meaning: '', color: '#ddb777', enabled: true }])}>添加分类</button><button className="primary" disabled={busy}>保存分类</button></div>
      </form>
    {message && <p role="status">{message}</p>}</section>}
  </>;
}

function RecognitionJobs() {
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
  return <details className="recognition-jobs" onToggle={event => { if (event.currentTarget.open && !page && !busy) void load(0); }}><summary>查看未完成任务与截图清理</summary>
    <p>按采样时间倒序显示；点击刷新查看最新状态。任务超过 24 小时仍未完成时自动失效并清理截图。</p>
    <button disabled={busy} onClick={() => void load(0)}>刷新任务</button>
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
        <div className="section-head hour-heading"><h3>{activityTime(start, state.day, state.settings)} — {activityTime(start + 3600000, state.day, state.settings)}</h3><span>{Math.round(group.seconds / 60)} 分钟 · {group.records.length} 次识别</span><button disabled={busy !== null || state.summaryBusy || !state.recognition.configured} onClick={() => void summarize(start)}>{generating ? '正在生成…' : '生成时段摘要'}</button></div>
        {saved?.text ? <div className="hour-overview"><SummaryText text={saved.text} /></div> : <p className="hour-placeholder">{generating ? '正在整理本时段的活动记录…' : '暂无时段摘要，点击“生成时段摘要”开始整理。'}</p>}
        {saved && generating && <small role="status">正在生成新版摘要，完成后更新。</small>}
        {generating && <button onClick={() => void command('summaryCancel').catch(error => setError(error.message))}>取消生成</button>}
        {latest && (latest.state === 'Failed' || latest.state === 'Cancelled') && <p role="alert">{latest.automatic ? '自动摘要' : '时段摘要'}{latest.state === 'Cancelled' ? '已取消' : '生成失败'}：{latest.error}{saved ? '。仍显示上次成功生成的摘要。' : ''} <button disabled={busy !== null || state.summaryBusy || !state.recognition.configured} onClick={() => void summarize(start, latest.id)}>重试摘要</button></p>}
        {!state.recognition.configured && <small>请先在设置中验证 AI 服务。</small>}
        <div className="hour-categories">{Array.from(group.records.reduce((map, record) => { const key = record.category.name; map.set(key, (map.get(key) ?? 0) + record.seconds); return map; }, new Map<string, number>())).map(([name, seconds]) => <span key={name}>{name} · {Math.round(seconds / 60)} 分钟</span>)}{saved && <small className="hour-generated">{saved.automatic ? '整点自动生成' : '手动生成'} · {new Date(saved.created).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })}</small>}</div>
        <details className="hour-records"><summary>展开 {group.records.length} 条原始记录</summary><ol className="recognition-timeline">{[...group.records].reverse().map(record => <li key={record.id}><time>{activityTime(record.utc, state.day, state.settings)}</time><div>{gaps.has(record.id) && <p className="sample-gap">记录缺口：{activityTime(gaps.get(record.id)!.start, state.day, state.settings)} 至 {activityTime(record.utc, state.day, state.settings)}，两次采样相隔 {Math.round(gaps.get(record.id)!.elapsedSeconds / 60)} 分钟。之间未保存其他成功采样。</p>}<span className="category-label" style={{ borderColor: record.category.color }}>{record.category.name}</span><p>{record.description}</p></div></li>)}</ol></details>
      </section>;
    }) : <div className="empty">这段时间没有成功识别记录。<small>空白不代表没有活动，日迹不会补写未知内容。</small></div>}
  </section>;
}




