import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { command, subscribe, type Mode, type Settings, type Snapshot } from './bridge';
import './styles.css';
import { TodayOverview, ApplicationStatistics, formatTime } from './DesignedPages';
import { RecognitionTimeline } from './Recognition';
import { Summaries } from './Summaries';
import { DesignIcon } from './DesignIcon';
import { ModeHelp } from './ModeHelp';
import './design-restoration.css';
import { SettingsPanel } from './SettingsPanel';

const pages = [['home', '今天', '◷'], ['review', '回顾', '▤'], ['summaries', 'AI 总结', '✧'], ['statistics', '应用统计', '▥'], ['settings', '设置', '⚙']] as const;
const modes: Record<Mode, string> = { Default: '默认', Away: '离开', Locked: '锁定', NoScreen: '不识屏' };
const localDay = () => new Date().toLocaleDateString('sv-SE');


function App() {
  const [settingsTab, setSettingsTab] = useState('record');
  const [healthOpen, setHealthOpen] = useState(false);
  const [page, setPage] = useState<string>('home');
  const [state, setState] = useState<Snapshot | null>(null);
  const [day, setDay] = useState(localDay);
  const [error, setError] = useState('');
  const [dialog, setDialog] = useState(false);
  const [mode, setMode] = useState<Mode>('Default');
  const [minutes, setMinutes] = useState('30');
  const [busy, setBusy] = useState(false);
  const run = async (type: string, payload: Record<string, unknown> = {}) => {
    setError(''); setBusy(true);
    try { await command(type, payload); return true; }
    catch (e) { setError(e instanceof Error ? e.message : '操作失败'); return false; }
    finally { setBusy(false); }
  };
  useEffect(() => subscribe(setState, setError), []);
  useEffect(() => { if (page === 'home' && state?.today) setDay(state.today); }, [page, state?.today]);
  useEffect(() => { if (state?.today) setDay(state.today); }, [state?.settings.nightMode, state?.settings.dayStartHour]);
  useEffect(() => { void run('snapshot', { day }); }, [day]);
  useEffect(() => { const follow = state?.settings.followSystemTheme; const apply = () => { document.documentElement.dataset.theme = follow ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : (state?.settings.theme ?? 'dark'); }; apply(); if (!follow) return; const media = matchMedia('(prefers-color-scheme: light)'); media.addEventListener('change', apply); return () => media.removeEventListener('change', apply); }, [state?.settings.theme, state?.settings.followSystemTheme]);
  const configure = (patch: Partial<Settings>): Promise<boolean> => state ? run('settings', { settings: { ...state.settings, ...patch } }) : Promise.resolve(false);
  const quickMode = (value: Mode) => { if (!state || busy) return; if (value === 'Locked' || value === 'NoScreen') { setMode(value); setDialog(true); } else void run('mode', { mode: value, minutes: 0 }); };
  const heading = (title: string, subtitle: string, eyebrow: string) => <header className="heading"><div><div className="eyebrow">{eyebrow}</div><h1>{title}</h1><p>{subtitle}</p></div><time>{state?.today ?? localDay()}</time></header>;
  const shiftDay = (offset: number) => { const date = new Date(day + 'T12:00:00'); date.setDate(date.getDate() + offset); setDay(date.toLocaleDateString('sv-SE')); };
  const datePicker = <div className="date-picker"><button aria-label="前一天" onClick={() => shiftDay(-1)}>←</button><input aria-label="查看日期" type="date" value={day} max={state?.today ?? localDay()} onChange={e => e.target.value && setDay(e.target.value)} /><button aria-label="后一天" disabled={day >= (state?.today ?? localDay())} onClick={() => shiftDay(1)}>→</button><button onClick={() => setDay(state?.today ?? localDay())}>回到今天</button><span>{new Date(day + 'T12:00:00').toLocaleDateString('zh-CN', { month: 'long', day: 'numeric', weekday: 'long' })}{state?.settings.nightMode ? ` · ${String(state.settings.dayStartHour).padStart(2, '0')}:00 至次日 ${String(state.settings.dayStartHour).padStart(2, '0')}:00` : ''}</span></div>;
  return <div className="shell"><aside><a className="brand" href="#" onClick={e => { e.preventDefault(); setPage('home'); setDay(state?.today ?? localDay()); }}><DesignIcon name="sun" /><span>日迹<small>RIJI / PERSONAL</small></span></a><nav aria-label="主要页面">{pages.map(([id, name]) => <button key={id} className={page === id ? 'active' : ''} aria-current={page === id ? 'page' : undefined} onClick={() => { setPage(id); if (id === 'home') setDay(state?.today ?? localDay()); }}><DesignIcon name={id} />{name}</button>)}</nav><div className="sidebar-bottom"><button disabled={!state || busy} onClick={() => configure({ theme: state?.settings.theme === 'light' ? 'dark' : 'light' })}>☼　{state?.settings.theme === 'light' ? '浅色模式' : '深色模式'}　⇄</button><small>留住片段，回到自己。<br />{state?.profile === 'Production' ? '个人记录' : '开发版 · 独立数据'}</small></div></aside>
    <main className={page === 'settings' ? 'settings-page' : ''}>
      {state?.diagnosticLogFailed && <div role="alert" className="error">故障日志写入失败，请检查数据目录权限和磁盘空间。记录保存状态请同时查看“记录状态”。</div>}
      {error && <div role="alert" className="error">{error}<button aria-label="关闭错误" onClick={() => setError('')}>×</button></div>}
      {state && state.day !== day && ['home', 'review', 'statistics'].includes(page) && <p role="status">正在读取所选日期…</p>}
      {!state && <div className="notice">正在连接本地记录服务。只有连接桌面后台后才会显示真实统计。</div>}
      {page === 'home' && <>{heading('留一盏灯，回看今天。', '那些投入过的时间，都有迹可循。', 'A QUIET RECORD')}
        <section className="state-strip"><div className="state-info"><span className={`dot ${state?.recording ? 'on' : ''}`} /><strong>{state ? modes[state.mode.mode] : '未连接'}</strong><small>{state?.mode.until ? `持续至 ${new Date(state.mode.until).toLocaleTimeString('zh-CN')}` : state?.timingStatus ?? '等待后台状态'}</small></div><div className="mode-quick" role="group" aria-label="记录状态">{(Object.keys(modes) as Mode[]).map(value => <button key={value} disabled={!state || busy} aria-pressed={state?.mode.mode === value} onClick={() => quickMode(value)}>{modes[value]}</button>)}</div><ModeHelp /></section>
        {state && state.day === state.today && <TodayOverview state={state} review={() => setPage('review')} />}
        <section className="health-compact"><button aria-expanded={healthOpen} onClick={() => setHealthOpen(!healthOpen)}>◉　记录状态 · {state?.recognition.busy ? '正在识别' : '运行详情'}　{healthOpen ? '收起' : '查看详情'}</button>{healthOpen && <p>{state?.health ?? '等待连接'}<small>应用计时与截图识别独立。截图开关及失败任务可在设置中管理。</small></p>}</section></>}
      {page === 'statistics' && <>{heading('时间，在应用之间流动。', '看清应用与常用网站分别用了多久。', 'APPLICATION USAGE')}{/* Keep the connection status beside the date controls. */}<div className="statistics-date">{datePicker}<button onClick={() => { setSettingsTab('browser'); setPage('settings'); }}>{state?.browserConnections ? '浏览器插件已连接' : '浏览器插件未连接'} ↗</button></div>{state && state.day === day && <ApplicationStatistics state={state} />}</>}
      {page === 'review' && <>{heading('回到这一天。', '按成功记录回顾片段，未记录的时间保持留白。', 'LOOK BACK')}{datePicker}{state?.day === day && <section className="review-summary"><div><small>成功识别累计</small><strong>{formatTime(state?.recognition.records.reduce((n, r) => n + r.seconds, 0) ?? 0)}</strong></div><div><small>成功识别</small><strong>{state?.recognition.records.length ?? 0} 次</strong></div><div><small>待重试任务（所有日期）</small><strong>{state?.recognition.jobs.filter(j => j.status === 'Retry' || j.status === 'Manual').reduce((n, j) => n + j.count, 0) ?? 0} 个</strong></div></section>}</>}
      {page === 'summaries' && <>{heading('把片段，串成一段回顾。', '选一段时间，理清观察到的事情。', 'AI SUMMARY')}{state && <Summaries state={state} />}</>}
      {page === 'review' && state && state.day === day && <RecognitionTimeline state={state} />}
      {page === 'settings' && state && <SettingsPanel state={state} tab={settingsTab as 'record' | 'capture' | 'ai' | 'review' | 'browser' | 'data'} busy={busy} configure={async patch => { return await configure(patch); }} run={run} onTabChange={value => setSettingsTab(value)} />}
    </main>
    {dialog && <div className="overlay" onClick={e => { if (e.target === e.currentTarget && !busy) setDialog(false); }}>
      <section className="modal" role="dialog" aria-modal="true" aria-labelledby="mode-title">
        <div className="section-head"><h2 id="mode-title">{modes[mode]}</h2><button disabled={busy} onClick={() => setDialog(false)} aria-label="关闭">×</button></div>
        <p>{mode === 'Locked' ? '锁定持续记录，不自动切到离开状态。用于看视频等不操作的场景。' : '停止截图和识别，继续应用计时。用于隐私或不值得浪费 token 的事。'}</p>
        <label className="duration-field">持续时间（分钟）
          <input autoFocus disabled={busy} type="number" min="1" max="1440" value={minutes} onChange={e => setMinutes(e.target.value)} />
          <div className="presets">{[5, 15, 30, 60, 120].map(n => <button disabled={busy} key={n} onClick={() => setMinutes(String(n))}>{n} 分钟</button>)}</div>
        </label>
        <p className="notice">到期后恢复默认状态。</p>
        <div className="modal-footer"><button disabled={busy} onClick={() => setDialog(false)}>取消</button>
          <button className="primary" disabled={busy || !Number.isInteger(Number(minutes)) || Number(minutes) < 1 || Number(minutes) > 1440} onClick={async () => { if (await run('mode', { mode, minutes: Number(minutes) })) setDialog(false); }}>开启{modes[mode]}</button>
        </div>
      </section>
    </div>}
  </div>;
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>);
