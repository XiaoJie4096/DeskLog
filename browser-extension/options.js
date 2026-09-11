'use strict';
const extensionApi = globalThis.browser ?? chrome;
async function refresh() {
  const { profile = 'production', status = '未连接', seen = 0 } = await extensionApi.storage.local.get(['profile', 'status', 'seen']);
  document.getElementById('profile').value = profile;
  document.getElementById('status').textContent = status === '已连接日迹' && Date.now() - seen > 5000 ? '连接已过期，正在等待重连' : status;
}
document.getElementById('profile').addEventListener('change', event => extensionApi.storage.local.set({ profile: event.target.value }));
document.getElementById('extension-id').textContent = '扩展 ID：' + extensionApi.runtime.id;
document.getElementById('reconnect').addEventListener('click', () => extensionApi.runtime.sendMessage({ type: 'reconnect' }));
extensionApi.storage.onChanged.addListener(refresh);
setInterval(refresh, 2000);
void refresh();
