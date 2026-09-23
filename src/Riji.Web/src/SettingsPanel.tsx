import { useEffect, useState } from 'react';
import { command, type Category, type CaptureSettings, type Settings, type Snapshot } from './bridge';
import { RecognitionJobs } from './Recognition';
import { WebsiteRules } from './WebsiteRules';
import './settings-panel.css';

type Tab = 'record' | 'capture' | 'ai' | 'review' | 'browser' | 'data';

type Props = {
  state: Snapshot;
  tab: Tab;
  busy: boolean;
  configure: (patch: Partial<Settings>) => Promise<boolean>;
  run: (type: string, payload?: Record<string, unknown>, success?: string) => Promise<boolean>;
  onTabChange: (tab: Tab) => void;
};

const tabs: [Tab, string][] = [
  ['record', '记录与时间'], ['capture', '截图识别'], ['ai', 'AI 提供方'],
  ['review', '回顾内容'], ['browser', '浏览器与隐私'], ['data', '应用与数据']
];

export function SettingsPanel({ state, tab, busy, configure, run, onTabChange }: Props) {
  return <>
    <nav className="settings-nav" aria-label="设置分区">
      {tabs.map(([id, name]) => <button key={id} className={tab === id ? 'active' : ''} aria-current={tab === id ? 'page' : undefined} onClick={() => onTabChange(id)}>{name}</button>)}
    </nav>
    {tab === 'record' && <RecordSettings state={state} busy={busy} configure={configure} run={run} />}
    {tab === 'capture' && <CaptureSettingsPanel state={state} busy={busy} run={run} />}
    {tab === 'ai' && <AiProviderSettings state={state} busy={busy} run={run} />}
    {tab === 'review' && <ReviewSettings state={state} busy={busy} run={run} />}
    {tab === 'browser' && <BrowserSettings state={state} busy={busy} configure={configure} />}
    {tab === 'data' && <DataSettings state={state} busy={busy} run={run} />}
  </>;
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return <section className="settings-section"><h2>{title}</h2>{children}</section>;
}

function SettingRow({ label, help, children }: { label: string; help?: string; children: React.ReactNode }) {
  return <div className="settings-row"><div><strong>{label}</strong>{help && <small>{help}</small>}</div><div className="settings-control">{children}</div></div>;
}

function Switch({ checked, disabled, onChange, label }: { checked: boolean; disabled?: boolean; onChange: (value: boolean) => void; label: string }) {
  return <input aria-label={label} className="settings-switch" type="checkbox" checked={checked} disabled={disabled} onChange={event => onChange(event.target.checked)} />;
}

function RecordSettings({ state, busy, configure, run }: Pick<Props, 'state' | 'busy' | 'configure' | 'run'>) {
  const [idle, setIdle] = useState(String(state.settings.idleSeconds));
  useEffect(() => setIdle(String(state.settings.idleSeconds)), [state.settings.idleSeconds]);
  return <>
    <Section title="应用">
      <SettingRow label="主题" help="选择深色、浅色，或跟随系统。"><select value={state.settings.followSystemTheme ? 'system' : state.settings.theme} onChange={event => void configure({ theme: event.target.value === 'system' ? state.settings.theme : event.target.value as Settings['theme'], followSystemTheme: event.target.value === 'system' })}><option value="system">跟随系统</option><option value="dark">深色</option><option value="light">浅色</option></select></SettingRow>
      <SettingRow label="开机自启" help="登录 Windows 后自动启动。"><Switch label="开机自启" checked={state.settings.startWithWindows} disabled={busy} onChange={value => void configure({ startWithWindows: value })} /></SettingRow>
      <SettingRow label="退出日迹" help="退出后停止记录；关闭窗口仍会在托盘运行。"><button className="settings-link" disabled={busy} onClick={() => void run('exit')}>退出 ›</button></SettingRow>
    </Section>
    <Section title="记录">
      <SettingRow label="自动记录" help="关闭后停止新采样、新识别请求和应用计时。"><Switch label="自动记录" checked={state.settings.autoRecord} disabled={busy} onChange={value => void configure({ autoRecord: value })} /></SettingRow>
      <SettingRow label="应用使用时长" help="不识屏期间仍可累计；离开、锁屏和休眠时暂停。"><Switch label="应用使用时长" checked={state.settings.appTiming} disabled={busy} onChange={value => void configure({ appTiming: value })} /></SettingRow>
      <SettingRow label="显示桌面时自动离开" help="没有应用窗口且 5 秒无键鼠操作时切换到离开。"><Switch label="显示桌面时自动离开" checked={state.settings.desktopAutoAway} disabled={busy} onChange={value => void configure({ desktopAutoAway: value })} /></SettingRow>
    </Section>
    <Section title="离开判定">
      <SettingRow label="无有效操作后离开" help="30–3600 秒，默认 120 秒。轻微鼠标漂移不会恢复。"><form onSubmit={event => { event.preventDefault(); void configure({ idleSeconds: Number(idle) }); }}><input aria-label="无有效操作后离开" type="number" min="30" max="3600" value={idle} onChange={event => setIdle(event.target.value)} /><button disabled={busy}>保存</button></form></SettingRow>
    </Section>
    <Section title="一天的起点">
      <SettingRow label="夜猫子模式" help="开启后，今天、回顾、统计和按天总结都按设定时间计算。"><Switch label="夜猫子模式" checked={state.settings.nightMode} disabled={busy} onChange={value => void configure({ nightMode: value })} /></SettingRow>
      <SettingRow label="新一天开始时间"><select disabled={!state.settings.nightMode || busy} value={state.settings.dayStartHour} onChange={event => void configure({ dayStartHour: Number(event.target.value) })}>{Array.from({ length: 9 }, (_, index) => index + 1).map(hour => <option key={hour} value={hour}>{String(hour).padStart(2, '0')}:00</option>)}</select></SettingRow>
      <SettingRow label="延长小时显示" help="例如将次日 01:00 显示为 25:00。"><Switch label="延长小时显示" checked={state.settings.extendedHours} disabled={!state.settings.nightMode || busy} onChange={value => void configure({ extendedHours: value })} /></SettingRow>
    </Section>
  </>;
}

function CaptureSettingsPanel({ state, busy, run }: { state: Snapshot; busy: boolean; run: Props['run'] }) {
  const current = state.recognition;
  const [prompt, setPrompt] = useState(current.settings.prompt ?? current.defaultPrompt);
  useEffect(() => setPrompt(current.settings.prompt ?? current.defaultPrompt), [current.settings.prompt, current.defaultPrompt]);
  const saveCapture = (patch: Partial<CaptureSettings>) => run('captureSettings', { settings: { ...current.settings, ...patch } });
  return <>
    <Section title="采集">
      <SettingRow label="启用截图识别" help="截取所有显示器的完整桌面并发送至 AI 服务。"><Switch label="启用截图识别" checked={current.settings.enabled} disabled={busy || !current.configured} onChange={value => void saveCapture({ enabled: value })} /></SettingRow>
      <SettingRow label="采样间隔" help="成功识别时长按任务创建时的间隔累计，与应用使用时长分开。"><select disabled={busy} value={current.settings.intervalSeconds} onChange={event => void saveCapture({ intervalSeconds: Number(event.target.value) })}><option value="60">1 分钟</option><option value="120">2 分钟</option><option value="300">5 分钟</option></select></SettingRow>
      <SettingRow label="保留成功截图" help="关闭时结果保存后删除；失败任务截图最多保留 24 小时。"><Switch label="保留成功截图" checked={current.settings.keepImages} disabled={busy} onChange={value => void saveCapture({ keepImages: value })} /></SettingRow>
    </Section>
    <Section title="截图识别提示词">
      <p>设置描述重点、表达方式和分类原则。已有任务重试沿用采样时的提示词、分类与辅助信息。</p>
      <textarea className="prompt-editor" value={prompt} maxLength={10000} rows={8} disabled={busy} onChange={event => setPrompt(event.target.value)} />
      <div className="settings-actions"><button className="primary" disabled={busy || !prompt.trim()} onClick={() => void saveCapture({ prompt })}>保存提示词</button><button disabled={busy} onClick={() => setPrompt(current.defaultPrompt)}>恢复默认提示词</button></div>
    </Section>
    <Section title="识别状态">
      <p>{current.configured ? `有效配置：${current.model}` : '尚未验证 AI 配置'} · {current.busy ? '正在处理' : current.paused ? '授权异常，已暂停' : current.settings.enabled ? '等待下一次采样' : '截图关闭'}</p>
      {current.error && <p role="alert" className="settings-error">{current.error}</p>}
      <SettingRow label="最近成功采样"><span>{current.latestSample ? new Date(current.latestSample).toLocaleString('zh-CN') : '暂无成功记录'}</span></SettingRow>
      <RecognitionJobs />
    </Section>
  </>;
}

function AiProviderSettings({ state, busy, run }: { state: Snapshot; busy: boolean; run: Props['run'] }) {
  const current = state.recognition;
  const [endpoint, setEndpoint] = useState(current.endpoint ?? '');
  const [model, setModel] = useState(current.model ?? '');
  const [summaryModel, setSummaryModel] = useState(current.summaryModel ?? current.model ?? '');
  const [contextK, setContextK] = useState(100);
  const [key, setKey] = useState('');
  const [models, setModels] = useState<{ id: string; contextK: number | null }[]>([]);
  const [message, setMessage] = useState('');
  useEffect(() => { setEndpoint(current.endpoint ?? ''); setModel(current.model ?? ''); setSummaryModel(current.summaryModel ?? current.model ?? ''); }, [current.endpoint, current.model, current.summaryModel]);
  const readModels = async () => { setMessage(''); try { const items = await command<typeof models>('models', { endpoint, key }); setModels(items); setMessage(`已读取 ${items.length} 个模型`); } catch (error) { setMessage(error instanceof Error ? error.message : '读取模型失败'); } };
  const save = (type: 'saveAi' | 'testAi') => run(type, { endpoint, model, summaryModel, contextK, key }, type === 'saveAi' ? 'AI 提供方配置已保存。' : '真实截图和两个模型验证成功，配置已启用。');
  return <Section title="连接配置">
    <p>{current.configured ? `当前已配置：${current.endpoint} · ${current.model}` : '尚未配置 AI 提供方。'}</p>
    {message && <p role="status" className="settings-note">{message}</p>}
    <label className="settings-field">接口地址<input type="url" required value={endpoint} onChange={event => setEndpoint(event.target.value)} disabled={busy} /></label>
    <label className="settings-field">API Key<input type="password" autoComplete="off" required={!current.configured} value={key} placeholder={current.configured ? '已保存，留空沿用' : '请输入 API Key'} onChange={event => setKey(event.target.value)} disabled={busy} /><small>已保存的 Key 不会回传到界面；留空会沿用本机保存的 Key。</small></label>
    <div className="settings-actions"><button disabled={busy || !endpoint || (!key && !current.configured)} onClick={() => void readModels()}>获取模型信息</button></div>
    <label className="settings-field">识图模型{models.length ? <select value={model} onChange={event => { setModel(event.target.value); const found = models.find(item => item.id === event.target.value); if (found?.contextK) setContextK(found.contextK); }} disabled={busy}>{models.map(item => <option key={item.id} value={item.id}>{item.id}</option>)}</select> : <input required value={model} onChange={event => setModel(event.target.value)} disabled={busy} />}</label>
    <label className="settings-field">总结（对话）模型{models.length ? <select value={summaryModel} onChange={event => setSummaryModel(event.target.value)} disabled={busy}>{models.map(item => <option key={item.id} value={item.id}>{item.id}</option>)}</select> : <input value={summaryModel} onChange={event => setSummaryModel(event.target.value)} disabled={busy} />}<small>用于时段摘要、AI 总结和后续对话。</small></label>
    <label className="settings-field">上下文长度（K token）<input type="number" min="4" max="150" value={contextK} onChange={event => setContextK(Number(event.target.value))} disabled={busy} /><small>接口提供上下文长度时，选择模型会自动填入。</small></label>
    <div className="settings-actions"><button disabled={busy || !endpoint || !model} onClick={() => void save('saveAi')}>保存配置</button><button className="primary" disabled={busy || current.busy || !endpoint || !model} onClick={() => void save('testAi')}>发送真实截图并验证配置</button></div>
  </Section>;
}

function ReviewSettings({ state, busy, run }: { state: Snapshot; busy: boolean; run: Props['run'] }) {
  const [categories, setCategories] = useState<Category[]>(state.recognition.categories);
  const [minutes, setMinutes] = useState(String(state.settings.hourlyMinimumMinutes));
  const [prompt, setPrompt] = useState(state.settings.hourlyPrompt ?? state.hourlyDefaultPrompt);
  useEffect(() => setCategories(state.recognition.categories), [state.recognition.categories]);
  useEffect(() => setMinutes(String(state.settings.hourlyMinimumMinutes)), [state.settings.hourlyMinimumMinutes]);
  useEffect(() => setPrompt(state.settings.hourlyPrompt ?? state.hourlyDefaultPrompt), [state.settings.hourlyPrompt, state.hourlyDefaultPrompt]);
  const updateCategory = (index: number, patch: Partial<Category>) => setCategories(items => items.map((item, current) => current === index ? { ...item, ...patch } : item));
  return <>
    <Section title="活动分类"><div className="category-editor-list">{categories.map((category, index) => <div className="category-edit" key={category.id}><Switch label={`启用 ${category.name}`} checked={category.enabled} disabled={busy} onChange={value => updateCategory(index, { enabled: value })} /><input aria-label="分类颜色" type="color" value={category.color} onChange={event => updateCategory(index, { color: event.target.value })} /><input aria-label="分类名称" maxLength={10} value={category.name} onChange={event => updateCategory(index, { name: event.target.value })} /><input aria-label="分类描述" maxLength={80} value={category.meaning} onChange={event => updateCategory(index, { meaning: event.target.value })} /><button aria-label={`删除分类 ${category.name}`} disabled={busy} onClick={() => setCategories(items => items.filter((_, current) => current !== index))}>删除</button></div>)}</div><div className="settings-actions"><button disabled={busy || categories.length >= 100} onClick={() => setCategories(items => [...items, { id: crypto.randomUUID(), name: '', meaning: '', color: '#ddb777', enabled: true }])}>添加分类</button><button className="primary" disabled={busy || categories.length === 0} onClick={() => void run('categories', { categories }, '分类已保存。')}>保存分类</button></div></Section>
    <Section title="时段摘要"><label className="settings-field">自动摘要最低记录时长<input type="number" min="1" max="60" value={minutes} disabled={busy} onChange={event => setMinutes(event.target.value)} /><small>成功识别记录累计达到门槛后，在整点自动生成；手动生成和失败重试不受限制。</small></label><label className="settings-field">时段摘要提示词<textarea rows={8} maxLength={10000} value={prompt} disabled={busy} onChange={event => setPrompt(event.target.value)} /><small>新摘要使用修改后的提示词；历史摘要不变。</small></label><div className="settings-actions"><button className="primary" disabled={busy} onClick={() => void run('settings', { settings: { ...state.settings, hourlyMinimumMinutes: Number(minutes), hourlyPrompt: prompt } }, '摘要设置已保存。')}>保存摘要设置</button><button disabled={busy} onClick={() => setPrompt(state.hourlyDefaultPrompt)}>恢复默认提示词</button></div></Section>
  </>;
}

function BrowserSettings({ state, busy, configure }: { state: Snapshot; busy: boolean; configure: Props['configure'] }) {
  return <>
    <Section title="网站归属"><WebsiteRules state={state} embedded /></Section>
    <Section title="网页信息"><SettingRow label="记录网页标题" help="后续标题保存在本地；启用截图识别时可作为 AI 辅助信息。"><Switch label="记录网页标题" checked={state.settings.websiteTitles} disabled={busy} onChange={value => void configure({ websiteTitles: value })} /></SettingRow><p>已连接 {state.browserConnections} 个浏览器会话。安装日迹扩展后自动连接，无需配对码。</p>{state.browserError && <p role="alert" className="settings-error">{state.browserError}</p>}</Section>
  </>;
}

function DataSettings({ state, busy, run }: { state: Snapshot; busy: boolean; run: Props['run'] }) {
  return <>
    <Section title="数据与备份"><SettingRow label="导出备份" help="包含记录、分类、预设、总结、对话和仍保留的关联截图，不包含 API Key。"><button disabled={busy || state.maintenance} onClick={() => void run('exportData')}>导出备份</button></SettingRow><SettingRow label="导入并替换" help="原数据先自动备份；导入后自动记录与截图保持关闭。"><button disabled={busy || state.maintenance} onClick={() => void run('importData')}>导入并替换</button></SettingRow><SettingRow label="清空记录" help="清空前会要求再次确认。"><button className="danger" disabled={busy || state.maintenance} onClick={() => void run('clearData')}>清空记录</button></SettingRow></Section>
    <Section title="当前数据位置"><SettingRow label="数据目录"><span className="data-path">{state.dataPath}</span><button onClick={() => void run('openDataFolder')}>打开</button></SettingRow></Section>
    <Section title="关于"><SettingRow label="当前版本" help="以正在运行的桌面程序版本为准。"><span>日迹 0.1.2</span></SettingRow></Section>
  </>;
}
