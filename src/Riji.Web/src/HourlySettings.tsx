import { useEffect, useState } from 'react';
import { command, type Snapshot } from './bridge';

export function HourlySettings({ state }: { state: Snapshot }) {
  const [minutes, setMinutes] = useState(String(state.settings.hourlyMinimumMinutes));
  const [prompt, setPrompt] = useState(state.settings.hourlyPrompt ?? state.hourlyDefaultPrompt);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  useEffect(() => setMinutes(String(state.settings.hourlyMinimumMinutes)), [state.settings.hourlyMinimumMinutes]);
  useEffect(() => setPrompt(state.settings.hourlyPrompt ?? state.hourlyDefaultPrompt), [state.settings.hourlyPrompt, state.hourlyDefaultPrompt]);
  return <section className="panel history settings"><h2>回顾 · 时段摘要</h2>
    <form className="ai-form" onSubmit={async event => {
      event.preventDefault(); setBusy(true); setMessage('');
      try {
        await command('settings', { settings: { ...state.settings, hourlyMinimumMinutes: Number(minutes), hourlyPrompt: prompt } });
        setMessage('已保存。之后的新摘要使用新提示词，失败任务重试保留原提示词。');
      } catch (error) { setMessage(error instanceof Error ? error.message : '保存失败'); }
      finally { setBusy(false); }
    }}>
      <label>自动摘要最低记录时长（分钟）<input required disabled={busy} type="number" min="1" max="60" step="1" value={minutes} onChange={event => setMinutes(event.target.value)} /></label>
      <small>默认 12 分钟，按每条成功识别记录的采样间隔累加。例如 1 分钟间隔需 12 次，2 分钟间隔需 6 次。只限制自动摘要，手动生成和失败重试不受限制。修改门槛不补生成之前已跳过的时段。</small>
      <label>时段摘要提示词<textarea required disabled={busy} maxLength={10000} rows={10} value={prompt} onChange={event => setPrompt(event.target.value)} /></label>
      <small>用于回顾页的手动和整点自动摘要。程序附加该时段的记录与范围；历史摘要不变。</small>
      <div className="summary-actions"><button className="primary" disabled={busy}>保存摘要设置</button><button type="button" disabled={busy} onClick={() => setPrompt(state.hourlyDefaultPrompt)}>恢复默认提示词</button></div>
      <small>恢复默认后点击保存生效。</small>
      {message && <p role="status">{message}</p>}
    </form>
  </section>;
}
