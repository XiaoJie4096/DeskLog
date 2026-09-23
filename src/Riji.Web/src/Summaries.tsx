import { useEffect, useState } from 'react';
import { command, type Snapshot, type SummaryDocument, type SummaryForm, type GenerationState, type PromptPreset } from './bridge';
import './summaries.css';
import { previewRange } from './ui-data';
import { formatTime } from './DesignedPages';
import { SummaryText } from './SummaryText';
import { dayBounds } from './day-time';

const labels: Record<GenerationState, string> = { Running: '生成中', Succeeded: '已完成', Failed: '失败，可重试', Cancelled: '已取消' };
const localInput = (date: Date) => `${date.toLocaleDateString('sv-SE')}T${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
const dailyFormDay = (form: SummaryForm) => {
  const start = new Date(form.start), end = new Date(form.end);
  if (!Number.isFinite(+start) || !Number.isFinite(+end) || start.getMinutes() !== 0 || start.getHours() > 9) return null;
  const next = new Date(start); next.setDate(next.getDate() + 1);
  return localInput(next) === form.end ? form.start.slice(0, 10) : null;
};

export function Summaries({ state }: { state: Snapshot }) {
  const [form, setForm] = useState<SummaryForm>(() => {
    const { start, end } = dayBounds(state.today, state.settings);
    const saved = state.summaryForm;
    const savedDay = saved && dailyFormDay(saved);
    if (saved && savedDay) {
      const bounds = dayBounds(savedDay <= state.today ? savedDay : state.today, state.settings);
      return { ...saved, start: localInput(bounds.start), end: localInput(bounds.end) };
    }
    return saved ?? { start: localInput(start), end: localInput(end), prompt: state.summaryPresets[0]?.prompt ?? '' };
  });
  const [rangeMode, setRangeMode] = useState<'day' | 'range'>(() => {
    const saved = state.summaryForm;
    if (!saved) return 'day';
    return dailyFormDay(saved) ? 'day' : 'range';
  });
  const preview = previewRange(state, form.start, form.end);
  function chooseDay(day: string) {
    if (!day) return;
    const { start, end } = dayBounds(day, state.settings);
    setForm(value => ({ ...value, start: localInput(start), end: localInput(end) }));
  }
  function quickRange(kind: 'today' | 'yesterday' | 'afternoon') {
    const date = new Date(state.today + 'T12:00');
    if (kind === 'yesterday') date.setDate(date.getDate() - 1);
    if (kind !== 'afternoon') { setRangeMode('day'); chooseDay(date.toLocaleDateString('sv-SE')); }
    else { setRangeMode('range'); setForm(value => ({ ...value, start: state.today + 'T12:00', end: state.today + 'T18:00' })); }
  }
  const [selected, setSelected] = useState(state.summaries[0]?.id ?? '');
  const [document, setDocument] = useState<SummaryDocument | null>(null);
  const [presetName, setPresetName] = useState('');
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [revision, setRevision] = useState(0);
  const busy = saving || state.summaryBusy;
  const current = state.summaries.find(item => item.id === selected);
  useEffect(() => {
    const timer = setTimeout(() => { void command('summaryForm', { form }).catch(error => setError(error.message)); }, 500);
    return () => clearTimeout(timer);
  }, [form]);
  useEffect(() => { if (!selected && state.summaries[0]) setSelected(state.summaries[0].id); }, [state.summaries, selected]);
  useEffect(() => {
    let cancelled = false;
    if (selected) void command<SummaryDocument>('summaryDetail', { summaryId: selected }).then(value => { if (!cancelled) setDocument(value); }).catch(error => { if (!cancelled) setError(error.message); });
    return () => { cancelled = true; };
  }, [selected, state.summaryBusy, revision]);
  useEffect(() => { setDocument(null); }, [selected]);
  async function perform(type: string, payload: Record<string, unknown>) {
    setSaving(true); setError('');
    try {
      const result = await command<string>(type, payload);
      if (type === 'summaryGenerate') setSelected(result);
      setRevision(value => value + 1);
    } catch (error) { setError(error instanceof Error ? error.message : '操作失败'); }
    finally { setSaving(false); }
  }
  return <>
    <section className="panel summary-form">
      {error && <p role="alert" className="notice">{error}</p>}
      <form onSubmit={event => {
        event.preventDefault();
        const start = new Date(form.start), end = new Date(form.end);
        if (!Number.isFinite(start.valueOf()) || !Number.isFinite(end.valueOf()) || start >= end) { setError('请填写有效且先后顺序正确的起止时间。'); return; }
        void perform('summaryGenerate', { start: start.toISOString(), end: end.toISOString(), prompt: form.prompt });
      }}>
        <div className="summary-builder"><div className="builder-side"><div className="eyebrow">01 / 选择时间</div><h2>想回看哪段时间？</h2><div className="segmented"><button type="button" aria-pressed={rangeMode === 'day'} onClick={() => { setRangeMode('day'); chooseDay(form.start.slice(0, 10)); }}>某一天</button><button type="button" aria-pressed={rangeMode === 'range'} onClick={() => setRangeMode('range')}>一段时间</button></div>
        {rangeMode === 'day' ? <label>选择日期<input type="date" required max={state.today} value={form.start.slice(0, 10)} onChange={e => chooseDay(e.target.value)} /></label> : <div className="summary-range"><label>开始时间<input type="datetime-local" required value={form.start} onChange={e => setForm(value => ({ ...value, start: e.target.value }))} /></label><label>结束时间<input type="datetime-local" required value={form.end} onChange={e => setForm(value => ({ ...value, end: e.target.value }))} /></label></div>}
        <div className="quick-days"><button type="button" onClick={() => quickRange('today')}>今天</button><button type="button" onClick={() => quickRange('yesterday')}>昨天</button><button type="button" onClick={() => quickRange('afternoon')}>今天下午</button></div>
        <div className="source-status">{preview ? <><b>{preview.count} 条成功记录</b><p>累计 {formatTime(preview.seconds)}</p></> : <p>生成时读取此范围的完整来源，完成后可展开查看。</p>}<small>按当前电脑时区选择。只使用已保存的识别片段，未记录的时间保持留白。</small></div></div>
        <div className="builder-prompt"><div className="eyebrow">02 / 选择总结方式</div><h2>你想怎样回顾？</h2>
        <div className="presets">{state.summaryPresets.map(preset => <button type="button" key={preset.id} aria-pressed={form.prompt === preset.prompt} onClick={() => setForm(value => ({ ...value, prompt: preset.prompt }))}>{preset.name}</button>)}<button type="button" aria-pressed={!state.summaryPresets.some(preset => preset.prompt === form.prompt)} onClick={() => setForm(value => ({ ...value, prompt: '' }))}>自定义</button></div>
        <label>本次提示词<textarea required maxLength={10000} rows={5} value={form.prompt} onChange={e => setForm(value => ({ ...value, prompt: e.target.value }))} /></label>
        <div className="preset-save"><input aria-label="新预设名称" maxLength={40} placeholder="保存为自定义预设" value={presetName} onChange={e => setPresetName(e.target.value)} /><button type="button" disabled={busy || !presetName.trim() || !form.prompt.trim()} onClick={() => void perform('summaryPresets', { presets: [...state.summaryPresets, { id: crypto.randomUUID(), name: presetName.trim(), prompt: form.prompt }] })}>保存预设</button></div>
        <p className="notice">生成会将此范围内的活动描述发送至设置中的 AI 服务。每次生成保留独立来源快照；已有总结不会随迟到记录自动改变。</p>
        <div className="summary-actions"><button className="primary" disabled={busy || !state.recognition.configured}>{busy ? '正在处理…' : '生成新的总结'}</button>{state.summaryBusy && <button type="button" onClick={() => void command('summaryCancel').catch(error => setError(error.message))}>取消生成</button>}</div>
        {!state.recognition.configured && <small>请先到设置中验证 AI 服务配置。当前不会发送请求。</small>}
        </div></div>
      </form>
      <PresetManager presets={state.summaryPresets} disabled={busy} />
    </section>
    {current && <section className="panel history summary-result"><div className="section-head"><h2>✧ {new Date(current.range.start).toLocaleDateString('zh-CN')} · {labels[current.state]}</h2><span>{current.sourceCount} 条来源 · 已完成 {current.completedBatches} 次请求</span></div>
      <p>{new Date(current.range.start).toLocaleString('zh-CN')} — {new Date(current.range.end).toLocaleString('zh-CN')}</p>
      {current.error && <p role="alert">{current.error}</p>}
      {(current.state === 'Failed' || current.state === 'Cancelled') && <button disabled={busy} onClick={() => void perform('summaryRetry', { summaryId: selected })}>从原始快照继续重试</button>}
      {document?.text && <SummaryText text={document.text} />}
      {document && <details><summary>查看本次提示词与来源快照</summary><p>数据截点：{new Date(document.dataCutoff).toLocaleString('zh-CN')} · 时区 {document.range.zoneId}</p><pre>{document.prompt}</pre><ol>{document.sources.map(source => <li key={source.id}><time>{new Date(source.utc).toLocaleString('zh-CN')}</time> · {source.category.name} · {source.seconds / 60} 分钟采样<p>{source.description}</p></li>)}</ol><details><summary>实际分批请求（共 {document.calls?.length ?? 0} 次）</summary>{document.calls?.map(call => <details key={call.id}><summary>第 {call.id.replace(':', ' 阶段，第 ')} 次请求 · {call.result ? '已完成' : '未完成'}</summary><pre>{call.prompt}</pre></details>)}</details></details>}
      {document?.state === 'Succeeded' && <div className="summary-chat"><h3>继续聊这篇总结</h3><p>对话独立保存，切换到其他总结不会混用上下文。</p>
        {document.conversation?.map((turn, index, items) => <article key={turn.id}><div className="chat-message user"><small>你</small><p className="chat-question">{turn.question}</p></div><div className="chat-message assistant"><small>日迹 · {labels[turn.state]}</small><SummaryText text={turn.reply ?? turn.error ?? '正在生成…'} /></div>{turn.state !== 'Succeeded' && turn.state !== 'Running' && index === items.length - 1 && <button disabled={busy} onClick={() => void perform('summaryChat', { summaryId: selected, question: '', turnId: turn.id })}>重试这条回复</button>}</article>)}
        <ChatComposer key={document.id} document={document} busy={busy} send={question => perform('summaryChat', { summaryId: document.id, question })} />
      </div>}
    </section>}
    <section className="panel history"><h2>历史总结</h2><div className="summary-history">{state.summaries.map(item => <button key={item.id} aria-pressed={selected === item.id} onClick={() => setSelected(item.id)}><strong>{new Date(item.range.start).toLocaleDateString('zh-CN')} · {labels[item.state]}</strong><small>{new Date(item.created).toLocaleString('zh-CN')} · {item.sourceCount} 条来源</small></button>)}</div>{!state.summaries.length && <p className="empty">还没有生成过总结。草稿和自定义预设会保存在本机。</p>}</section>
  </>;
}

function ChatComposer({ document, busy, send }: { document: SummaryDocument; busy: boolean; send: (question: string) => Promise<void> }) {
  const [text, setText] = useState(document.chatDraft ?? '');
  const [error, setError] = useState('');
  const [pending, setPending] = useState(0);
  useEffect(() => { setText(document.chatDraft ?? ''); }, [document.chatDraft]);
  function editDraft(next: string) {
    setText(next); setPending(count => count + 1); setError('');
    void command('summaryChatDraft', { summaryId: document.id, text: next }).catch(error => setError(error.message)).finally(() => setPending(count => count - 1));
  }
  return <form onSubmit={event => { event.preventDefault(); void send(text); }}>
    <div className="followup-suggestions">{['哪些片段值得留意？', '帮我整理主要活动', '有哪些记录空白？'].map(question => <button type="button" key={question} disabled={busy} onClick={() => editDraft(question)}>{question}</button>)}</div>
    <label>你的问题<textarea required maxLength={10000} rows={3} disabled={busy} value={text} onChange={event => {
      editDraft(event.target.value);
    }} /></label>
    <small>{error ? `草稿保存失败：${error}` : pending ? '正在保存草稿…' : '草稿保存在本篇总结中，切换页面后仍保留。'}</small>
    <button className="primary" disabled={busy || pending > 0 || !!error || !text.trim()}>发送</button>
  </form>;
}

function PresetManager({ presets, disabled }: { presets: PromptPreset[]; disabled: boolean }) {
  const [items, setItems] = useState(presets);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState('');
  const source = JSON.stringify(presets);
  useEffect(() => { if (!dirty) setItems(presets); }, [source, dirty]);
  function update(id: string, patch: Partial<PromptPreset>) {
    setDirty(true); setMessage(''); setItems(values => values.map(item => item.id === id ? { ...item, ...patch } : item));
  }
  return <details className="preset-manager"><summary>管理已保存的预设</summary>
    <p>修改名称、提示词或删除预设后保存。历史总结保留生成时的提示词；本次生成表单也不会被改写。</p>
    <form onSubmit={async event => {
      event.preventDefault(); setSaving(true); setMessage('');
      try { await command('summaryPresets', { presets: items }); setDirty(false); setMessage('预设已保存。'); }
      catch (error) { setMessage(error instanceof Error ? error.message : '保存失败，编辑内容已保留。'); }
      finally { setSaving(false); }
    }}>
      <fieldset disabled={disabled || saving}>
        {items.map(item => <div className="preset-editor" key={item.id}>
          <label>预设名称<input required maxLength={40} value={item.name} onChange={event => update(item.id, { name: event.target.value })} /></label>
          <label>提示词<textarea required maxLength={10000} rows={3} value={item.prompt} onChange={event => update(item.id, { prompt: event.target.value })} /></label>
          <button type="button" onClick={() => { setDirty(true); setItems(values => values.filter(value => value.id !== item.id)); }}>删除此预设</button>
        </div>)}
        {!items.length && <p>没有已保存的预设。仍可在上方输入提示词并保存新预设。</p>}
        <div className="summary-actions"><button disabled={!dirty} className="primary">保存预设修改</button><button type="button" disabled={!dirty} onClick={() => { setItems(presets); setDirty(false); setMessage('已放弃未保存的修改。'); }}>放弃修改</button></div>
      </fieldset>
      {message && <p role="status">{message}</p>}
    </form>
  </details>;
}
