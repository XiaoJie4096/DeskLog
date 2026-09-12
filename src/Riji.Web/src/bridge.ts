export type Mode = 'Default' | 'Away' | 'Locked' | 'NoScreen';
export type Settings = { hourlyMinimumMinutes: number; hourlyPrompt?: string | null; autoRecord: boolean; appTiming: boolean; idleSeconds: number; theme: 'dark' | 'light'; websiteTitles: boolean; followSystemTheme: boolean; startWithWindows: boolean; websiteSnippets: boolean; websiteProjects?: { id: string; name: string; domains: string[] }[] | null; websiteRules?: { domain: string; allow: boolean }[] | null };
export type Category = { id: string; name: string; meaning: string; color: string; enabled: boolean };
export type CaptureSettings = { enabled: boolean; intervalSeconds: number; keepImages: boolean; maxAttempts: number; prompt?: string | null };
export type GenerationState = 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';
export type SummaryForm = { start: string; end: string; prompt: string };
export type PromptPreset = { id: string; name: string; prompt: string };
export type JobPage = { total: number; items: { id: string; utc: string; status: string; attempts: number; retryAt: string | null; error: string | null; cleanupPending: boolean }[] };
export type SummaryHeader = { id: string; range: { start: string; end: string; zoneId: string }; created: string; state: GenerationState; error: string | null; sourceCount: number; completedBatches: number; hourly?: boolean; automatic?: boolean; text?: string | null };
export type SummaryDocument = Omit<SummaryHeader, 'sourceCount' | 'completedBatches'> & { dataCutoff: string; prompt: string; text: string | null; chatDraft: string;
  sources: { id: string; utc: string; seconds: number; description: string; category: Category }[];
  calls: { id: string; prompt: string; result: string | null }[] | null;
  conversation: { id: string; question: string; reply: string | null; state: GenerationState; error: string | null }[] | null };
export type Snapshot = {
  type: 'snapshot'; day: string; today: string; settings: Settings;
  mode: { mode: Mode; until: string | null; cooldownUntil: string | null };
  currentApp: string | null; health: string; recording: boolean; timingStatus: string;
  apps: { appId: string; name: string; seconds: number }[];
  websites: { appId: string; appName?: string; sourceAppName?: string; domain: string; title: string | null; snippet: string | null; seconds: number }[];
  browserConnections: number; browserError: string | null;
  hourlyDefaultPrompt: string; hourlySummaryError?: string | null; summaryBusy: boolean; summaryForm: SummaryForm | null; summaryPresets: PromptPreset[]; summaries: SummaryHeader[];
  maintenance: boolean; dataStatus: string | null; diagnosticLogFailed: boolean;
  recognition: { settings: CaptureSettings; defaultPrompt: string; categories: Category[]; busy: boolean; paused: boolean; error: string | null;
    configured: boolean; endpoint: string | null; model: string | null; summaryModel: string | null; latestSample: string | null; jobs: { status: string; count: number }[];
    records: { id: string; utc: string; seconds: number; description: string; category: Category; confidence: number }[] };
  days: { day: string; seconds: number; recordCount: number; sampleSeconds: number }[]; profile: string; dataPath: string; savedAt: string;
};
type Host = { postMessage: (value: unknown) => void; addEventListener: (event: string, callback: (event: MessageEvent) => void) => void };
declare global { interface Window { chrome?: { webview?: Host } } }
const waiting = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void; timeout: ReturnType<typeof setTimeout> }>();
const listeners = new Set<(state: Snapshot) => void>();
const errors = new Set<(error: string) => void>();
window.chrome?.webview?.addEventListener('message', event => {
  const data = event.data;
  if (data.type === 'snapshot') listeners.forEach(listener => listener(data));
  if (data.type === 'error') errors.forEach(listener => listener(data.error));
  if (data.type === 'result') {
    const entry = waiting.get(data.id);
    if (!entry) return;
    clearTimeout(entry.timeout); waiting.delete(data.id);
    if (data.ok) entry.resolve(data.value); else entry.reject(new Error(data.error ?? '操作失败'));
  }
});

// Fail explicitly outside the native host; never fabricate activity for previews.
export function command<T = void>(type: string, payload: Record<string, unknown> = {}): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (!window.chrome?.webview) { reject(new Error('请通过日迹桌面应用打开，浏览器预览无法读取电脑活动。')); return; }
    const id = crypto.randomUUID();
    const timeout = setTimeout(() => { waiting.delete(id); reject(new Error('后台响应超时，请检查运行状态；生成任务可从历史列表查看。')); }, ['hourSummaryGenerate', 'summaryGenerate', 'summaryRetry', 'summaryChat', 'exportData', 'importData', 'clearData'].includes(type) ? 3600000 : type === 'testAi' ? 100000 : 6000);
    waiting.set(id, { resolve: value => resolve(value as T), reject, timeout });
    window.chrome.webview.postMessage({ id, type, ...payload });
  });
}
export function subscribe(listener: (state: Snapshot) => void, onError: (message: string) => void) {
  listeners.add(listener); errors.add(onError);
  return () => { listeners.delete(listener); errors.delete(onError); };
}
