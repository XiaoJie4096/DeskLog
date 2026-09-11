'use strict';
const extensionApi = globalThis.browser ?? chrome;
let sequence = 0;
let session = crypto.randomUUID();
let connecting = false;
let generation = 0;
let timer = null;
let activeRequest = null;

// Report only a fresh query in response to the selected local environment's challenge.
async function report(probe, destination) {
  const base = { nonce: probe.nonce, sequence: ++sequence };
  try {
    const window = await extensionApi.windows.getLastFocused();
    if (!window.focused || window.incognito) { destination.postMessage({ ...base, type: 'none' }); return; }
    const [tab] = await extensionApi.tabs.query({ active: true, windowId: window.id });
    if (!tab || tab.incognito || !tab.url) { destination.postMessage({ ...base, type: 'none' }); return; }
    let url;
    try { url = new URL(tab.url); } catch { destination.postMessage({ ...base, type: 'none' }); return; }
    if (!['http:', 'https:'].includes(url.protocol)) { destination.postMessage({ ...base, type: 'none' }); return; }
    const domain = url.hostname.toLowerCase().replace(/\.$/, '');
    destination.postMessage({ ...base, type: 'activity', windowId: window.id, domain,
      title: probe.titles ? (tab.title || '').slice(0, 200) : null });
  } catch {
    try { destination.postMessage({ ...base, type: 'transient' }); } catch { /* A disconnected host cannot receive data. */ }
  }
}


// Connect only the selected environment; browser-enforced origin rules exclude web pages.
async function connect() {
  if (connecting) return;
  connecting = true;
  const version = generation;
  try {
    const { profile = 'production' } = await extensionApi.storage.local.get('profile');
    if (!['development', 'production'].includes(profile)) return;
    const endpoint = 'http://127.0.0.1:' + (profile === 'development' ? '4177' : '4178');
    activeRequest = new AbortController();
    const timeout = setTimeout(() => activeRequest?.abort(), 2500);
    try {
      const post = async (path, body) => {
        const response = await fetch(endpoint + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Riji-Extension': extensionApi.runtime.id },
          body: JSON.stringify(body), signal: activeRequest.signal, redirect: 'error', cache: 'no-store' });
        if (response.status === 403) throw new Error('identity');
        if (!response.ok) throw new Error('service');
        return response;
      };
      const browser = navigator.userAgent.includes('Firefox/') ? 'firefox' : navigator.userAgent.includes('Edg/') ? 'msedge' : 'chrome';
      const probe = await (await post('/v1/probe', { session, browser })).json();
      let packet;
      await report(probe, { postMessage: value => { packet = value; } });
      if (version !== generation) return;
      await post('/v1/report', { ...packet, session });
      if (version === generation) await extensionApi.storage.local.set({ status: '已连接日迹', seen: Date.now() });
    } finally { clearTimeout(timeout); activeRequest = null; }
  } catch (error) {
    if (version === generation) await extensionApi.storage.local.set({ status: error.message === 'identity'
      ? '日迹拒绝了此扩展，请确认安装的是日迹官方扩展'
      : error.message === 'service' ? '日迹连接暂不可用，正在重试'
      : '无法连接本机日迹：请确认新版日迹正在运行，且环境选择一致' });
  } finally {
    connecting = false;
    clearTimeout(timer);
    timer = setTimeout(connect, version === generation ? 750 : 0);
  }
}

// Refresh quickly for user actions; slow alarms also wake a discarded service worker.
function wake() { clearTimeout(timer); void connect(); }
function restart() {
  generation++; session = crypto.randomUUID(); sequence = 0;
  activeRequest?.abort(); wake();
}
extensionApi.runtime.onStartup.addListener(wake);
extensionApi.runtime.onInstalled.addListener(() => { void extensionApi.alarms.create('reconnect', { periodInMinutes: 0.5 }); wake(); });
extensionApi.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'reconnect') wake(); });
extensionApi.tabs.onActivated.addListener(wake);
extensionApi.windows.onFocusChanged.addListener(wake);
extensionApi.action.onClicked.addListener(() => extensionApi.runtime.openOptionsPage());
extensionApi.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.profile) restart();
});
extensionApi.runtime.onMessage.addListener(message => { if (message.type === 'reconnect') restart(); });
void connect();
