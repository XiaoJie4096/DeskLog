import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { command, subscribe, type Mode, type Settings, type Snapshot } from './bridge';
import './styles.css';
import { TodayOverview, ApplicationStatistics, formatTime } from './DesignedPages';
import { RecognitionSettings, RecognitionTimeline } from './Recognition';
import { Summaries } from './Summaries';
import { WebsiteRules } from './WebsiteRules';
import { DesignIcon } from './DesignIcon';
import { HourlySettings } from './HourlySettings';
import { ModeHelp } from './ModeHelp';
import './design-restoration.css';

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
  const [idleDraft, setIdleDraft] = useState('120');
  const run = async (type: string, payload: Record<string, unknown> = {}) => {
    setError(''); setBusy(true);
    try { await command(type, payload); return true; }
    catch (e) { setError(e instanceof Error ? e.message : '操作失败'); return false; }
    finally { setBusy(false); }
  };
  useEffect(() => subscribe(setState, setError), []);
  useEffect(() => { if (state) setIdleDraft(String(state.settings.idleSeconds)); }, [state?.settings.idleSeconds]);
  useEffect(() => { if (page === 'home' && state?.today) setDay(state.today); }, [page, state?.today]);
  useEffect(() => { void run('snapshot', { day }); }, [day]);
  useEffect(() => { const follow = state?.settings.followSystemTheme; const apply = () => { document.documentElement.dataset.theme = follow ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : (state?.settings.theme ?? 'dark'); }; apply(); if (!follow) return; const media = matchMedia('(prefers-color-scheme: light)'); media.addEventListener('change', apply); return () => media.removeEventListener('change', apply); }, [state?.settings.theme, state?.settings.followSystemTheme]);
  const configure = (patch: Partial<Settings>) => state && run('settings', { settings: { ...state.settings, ...patch } });
  const quickMode = (value: Mode) => { if (!state || busy) return; if (value === 'Locked' || value === 'NoScreen') { setMode(value); setDialog(true); } else void run('mode', { mode: value, minutes: 0 }); };
  const heading = (title: string, subtitle: string, eyebrow: string) => <header className="heading"><div><div className="eyebrow">{eyebrow}</div><h1>{title}</h1><p>{subtitle}</p></div><time>{state?.today ?? localDay()}</time></header>;
  const shiftDay = (offset: number) => { const date = new Date(day + 'T12:00:00'); date.setDate(date.getDate() + offset); setDay(date.toLocaleDateString('sv-SE')); };
  const datePicker = <div className="date-picker"><button aria-label="前一天" onClick={() => shiftDay(-1)}>←</button><input aria-label="查看日期" type="date" value={day} max={state?.today ?? localDay()} onChange={e => e.target.value && setDay(e.target.value)} /><button aria-label="后一天" disabled={day >= (state?.today ?? localDay())} onClick={() => shiftDay(1)}>→</button><button onClick={() => setDay(state?.today ?? localDay())}>回到今天</button><span>{new Date(day + 'T12:00:00').toLocaleDateString('zh-CN', { month: 'long', day: 'numeric', weekday: 'long' })}</span></div>;
  return <div className="shell"><aside><a className="brand" href="#" onClick={e => { e.preventDefault(); setPage('home'); setDay(localDay()); }}><DesignIcon name="sun" /><span>日迹<small>RIJI / PERSONAL</small></span></a><nav aria-label="主要页面">{pages.map(([id, name]) => <button key={id} className={page === id ? 'active' : ''} aria-current={page === id ? 'page' : undefined} onClick={() => { setPage(id); if (id === 'home') setDay(localDay()); }}><DesignIcon name={id} />{name}</button>)}</nav><div className="sidebar-bottom"><button disabled={!state || busy} onClick={() => configure({ theme: state?.settings.theme === 'light' ? 'dark' : 'light' })}>☼　{state?.settings.theme === 'light' ? '浅色模式' : '深色模式'}　⇄</button><small>留住片段，回到自己。<br />{state?.profile === 'Production' ? '个人记录' : '开发版 · 独立数据'}</small></div></aside>
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
      {page === 'settings' && <>{heading('按你的习惯，慢慢调整。', '记录、隐私与数据，都由你决定。', 'PREFERENCES')}<nav className="settings-nav" aria-label="设置分区">{[['record','记录与状态'],['capture','截图与 AI'],['categories','活动分类'],['browser','浏览器与隐私'],['data','数据与备份']].map(([id,name]) => <button key={id} className={settingsTab === id ? 'active' : ''} aria-current={settingsTab === id ? 'page' : undefined} onClick={() => setSettingsTab(id)}>{name}</button>)}</nav></>}
      {page === 'settings' && settingsTab === 'record' && <><section className="panel settings"><h2>记录设置</h2><label className="setting"><span>主题<small>选择深色、浅色，或跟随系统。</small></span><select value={state?.settings.followSystemTheme ? "system" : state?.settings.theme} onChange={e => configure({ theme: e.target.value === "system" ? (state?.settings.theme ?? "dark") : (e.target.value as "dark" | "light"), followSystemTheme: e.target.value === "system" })}><option value="system">跟随系统</option><option value="dark">深色</option><option value="light">浅色</option></select></label><label className="setting"><span>开机自启<small>登录 Windows 后自动启动。</small></span><input type="checkbox" checked={state?.settings.startWithWindows ?? false} onChange={e => configure({ startWithWindows: e.target.checked })} /></label><label className="setting"><span>自动记录<small>关闭后停止新采样、新识别请求和应用计时。</small></span><input type="checkbox" disabled={!state || busy} checked={state?.settings.autoRecord ?? false} onChange={e => configure({ autoRecord: e.target.checked })} /></label><label className="setting"><span>应用使用时长<small>不识屏期间仍可累计；离开、锁屏和休眠时暂停。</small></span><input type="checkbox" disabled={!state || busy} checked={state?.settings.appTiming ?? false} onChange={e => configure({ appTiming: e.target.checked })} /></label><label className="setting"><span>显示桌面时自动离开<small>没有应用窗口且 5 秒无键鼠操作时切换到离开。</small></span><input type="checkbox" disabled={!state || busy} checked={state?.settings.desktopAutoAway ?? true} onChange={e => configure({ desktopAutoAway: e.target.checked })} /></label><form className="setting" onSubmit={e => { e.preventDefault(); void configure({ idleSeconds: Number(idleDraft) }); }}><label htmlFor="idle">无有效操作后离开<small>30–3600 秒，默认 120 秒。轻微鼠标漂移不会恢复。</small></label><div><input id="idle" type="number" min="30" max="3600" value={idleDraft} onChange={e => setIdleDraft(e.target.value)} required /><button disabled={!state || busy}>保存</button></div></form><div className="setting"><span>当前数据位置<small className="path">{state?.dataPath ?? '未连接'}</small></span><span>{state?.profile === 'Production' ? '正式' : '开发'}数据</span></div><p className="notice">应用计时不读取窗口标题或键盘内容。截图和网页标题在对应分区独立控制。关闭窗口后在托盘继续运行；退出会停止记录。</p><button className="outline" disabled={!state} onClick={() => void run('exit')}>退出日迹</button></section></>}
      {page === 'review' && state && state.day === day && <RecognitionTimeline state={state} />}
      {page === 'settings' && state && (settingsTab === 'capture' || settingsTab === 'categories') && <RecognitionSettings state={state} section={settingsTab} />}
      {page === 'settings' && settingsTab === 'capture' && state && <HourlySettings state={state} />}
      {page === 'settings' && settingsTab === 'browser' && state && <WebsiteRules state={state} />}
      {page === 'settings' && settingsTab === 'data' && <section className="panel history settings"><h2>数据与备份</h2><p>备份包含记录、分类、预设、总结、对话和仍保留的关联截图，不包含 API Key。请妥善保存。</p><p className="notice">维护会暂时暂停采集并取消正在进行的 AI 任务，已完成的数据保留。导入采用完整替换，原数据先自动备份；导入或清空后自动记录与截图保持关闭。</p><div className="summary-actions"><button disabled={busy || !state || state.maintenance} onClick={() => void run('exportData')}>导出备份</button><button disabled={busy || !state || state.maintenance} onClick={() => void run('importData')}>导入并替换</button><button disabled={busy || !state || state.maintenance} onClick={() => void run('clearData')}>清空记录</button></div>{state?.dataStatus && <p role="status" className="path">{state.dataStatus}</p>}{state?.maintenance && <p role="status">数据维护中，请等待完成。</p>}</section>}
      {page === 'settings' && settingsTab === 'browser' && <section className="panel history settings"><h2>浏览器与隐私</h2><p>{state?.browserConnections ? `已连接 ${state.browserConnections} 个浏览器会话` : '尚未连接浏览器扩展'}</p><label className="setting"><span>记录网页标题<small>开启后将当前标签页标题保存在日迹本地数据库，可在应用统计详情查看；启用截图识别时，也会将采样时的标题作为辅助信息发送给 AI。关闭只影响后续采样，历史标题保留。</small></span><input type="checkbox" disabled={!state || busy} checked={state?.settings.websiteTitles ?? false} onChange={e => configure({ websiteTitles: e.target.checked })} /></label><p className="notice">安装日迹扩展后自动连接，无需配对码。使用开发版时，请在扩展选项中选择“开发版”；正式版默认自动连接。</p>{state?.browserError && <p role="alert">{state.browserError}</p>}</section>}
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
