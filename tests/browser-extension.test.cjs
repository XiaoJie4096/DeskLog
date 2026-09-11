const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../browser-extension/worker.js'), 'utf8');

// Exercise the extension against synthetic browser APIs without accessing a user's browser.
async function query({ url = 'https://user:password@example.com/path?token=secret#fragment', title = 'private title', focused = true, incognito = false, allowTitles = false, rules = [], snippets = false, permission = false, metadata = '  page   summary  ', navigate = false, revoke = false } = {}) {
  const messages = [];
  const event = { addListener() {} };
  let reads = 0, checks = 0;
  const chrome = {
    windows: { getLastFocused: async () => ({ id: 7, focused, incognito }), onFocusChanged: event },
    tabs: { query: async () => [{ id: 2, url: navigate && reads ? url + '/changed' : url, title, incognito }], onActivated: event },
    permissions: { contains: async () => permission && !(revoke && checks++ > 0) },
    scripting: { executeScript: async ({ func }) => {
      reads++;
      const result = vm.runInNewContext('(' + func.toString() + ')()', {
        document: { querySelector: selector => { assert.equal(selector, 'meta[name="description"]'); return metadata == null ? null : { getAttribute: name => { assert.equal(name, 'content'); return metadata; } }; } },
        location: { href: url },
      });
      return [{ result }];
    } },
    runtime: { onStartup: event, onInstalled: event, onMessage: event }, alarms: { onAlarm: event }, action: { onClicked: event },
    storage: { local: { get: async () => ({ profile: 'disabled-for-test' }) }, onChanged: event },
  };
  const context = vm.createContext({ chrome, URL, Date, crypto: require('node:crypto').webcrypto, setTimeout: () => 0, clearTimeout() {}, probe: { nonce: 'n', titles: allowTitles, rules, snippets }, destination: { postMessage: message => messages.push(message) } });
  vm.runInContext(source, context);
  await vm.runInContext('report(probe, destination)', context);
  return { ...JSON.parse(JSON.stringify(messages[0])), reads };
}

test('removed snippet feature never reads or sends page metadata even to an older host', async () => {
  const message = await query({ snippets: true, permission: true, allowTitles: true });
  assert.equal(message.reads, 0);
  assert.equal(message.snippet, undefined);
  assert.equal(message.title, 'private title');
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '../browser-extension/manifest.json'), 'utf8'));
  assert.equal(manifest.optional_permissions, undefined);
  assert.equal(manifest.optional_host_permissions, undefined);
});

test('default reporting removes URL secrets and title', async () => {
  const message = await query();
  assert.equal(message.domain, 'example.com'); assert.equal(message.title, null);
  assert.equal(JSON.stringify(message).includes('secret'), false);
  assert.equal(JSON.stringify(message).includes('password'), false);
});
test('titles require explicit opt-in', async () => { assert.equal((await query({ allowTitles: true })).title, 'private title'); });
test('legacy block rules do not suppress website evidence', async () => {
  const rules = [{ domain: 'example.com', allow: false }, { domain: 'docs.example.com', allow: true }];
  const blocked = await query({ rules, allowTitles: true });
  assert.equal(blocked.type, 'activity'); assert.equal(blocked.domain, 'example.com'); assert.equal(blocked.title, 'private title');
  assert.equal((await query({ rules, url: 'https://docs.example.com' })).type, 'activity');
  assert.equal((await query({ rules, url: 'https://evil-example.com' })).type, 'activity');
});
test('private windows explicitly end attribution', async () => { assert.equal((await query({ incognito: true })).type, 'none'); });
test('background windows cannot report foreground activity', async () => { assert.equal((await query({ focused: false })).type, 'none'); });
for (const url of ['chrome://settings', 'edge://newtab', 'file:///C:/notes.txt', 'about:blank'])
  test(`non-web URL is excluded: ${url}`, async () => { assert.equal((await query({ url })).type, 'none'); });

async function connectHttp(profile, fail = false, browser = 'chrome') {
  const calls = [], states = [];
  const event = { addListener() {} };
  const chrome = {
    windows: { getLastFocused: async () => ({ id: 7, focused: true, incognito: false }), onFocusChanged: event },
    tabs: { query: async () => [{ url: 'https://example.com/path?secret=hidden', title: 'private title' }], onActivated: event },
    runtime: { id: browser === 'firefox' ? 'riji-browser@riji.local' : 'pngdbmhpmldhdhiihalmlfecglmhiibk', onStartup: event, onInstalled: event, onMessage: event },
    alarms: { onAlarm: event }, action: { onClicked: event },
    storage: { local: { get: async () => ({ profile }), set: async value => states.push(value) }, onChanged: event },
  };
  const context = vm.createContext({ ...(browser === 'firefox' ? { browser: chrome } : { chrome }), URL, Date, crypto: require('node:crypto').webcrypto, AbortController,
    navigator: { userAgent: browser === 'firefox' ? 'Firefox/128.0' : browser === 'msedge' ? 'Edg/130.0' : 'Chrome' }, setTimeout: () => 0, clearTimeout() {},
    fetch: async (url, options) => {
      calls.push({ url, options });
      if (fail) throw new Error('offline');
      return { ok: true, status: 200, json: async () => ({ nonce: 'test-nonce', titles: false }) };
    },
  });
  vm.runInContext(source, context);
  await new Promise(resolve => setImmediate(resolve));
  return { calls, states };
}

test('HTTP connection uses only the selected environment and sends domain-only evidence', async () => {
  const { calls, states } = await connectHttp('production');
  assert.equal(calls.length, 2);
  assert.equal(calls[0].url, 'http://127.0.0.1:4178/v1/probe');
  assert.equal(calls[1].url, 'http://127.0.0.1:4178/v1/report');
  assert.equal(calls[1].options.headers['X-Riji-Extension'], 'pngdbmhpmldhdhiihalmlfecglmhiibk');
  assert.equal(calls[1].options.headers['X-Riji-Token'], undefined);
  const body = JSON.parse(calls[1].options.body);
  assert.equal(body.domain, 'example.com'); assert.equal(body.title, null);
  assert.equal(body.nonce, 'test-nonce'); assert.equal(body.sequence, 1);
  assert.equal(calls[1].options.body.includes('secret'), false);
  assert.equal(calls[1].options.redirect, 'error');
  assert.equal(states.at(-1).status, '已连接日迹');
});

for (const browser of ['chrome', 'msedge', 'firefox']) test(`${browser} sends its own browser identity with the shared protocol`, async () => {
  const { calls, states } = await connectHttp('development', false, browser);
  assert.equal(JSON.parse(calls[0].options.body).browser, browser);
  assert.equal(calls[0].options.headers['X-Riji-Extension'], browser === 'firefox' ? 'riji-browser@riji.local' : 'pngdbmhpmldhdhiihalmlfecglmhiibk');
  assert.equal(states.at(-1).status, '已连接日迹');
});

test('Firefox manifest uses the shared scripts and a stable Gecko identity', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '../browser-extension/manifest.firefox.json'), 'utf8'));
  assert.deepEqual(manifest.background, { scripts: ['worker.js'] });
  assert.equal(manifest.browser_specific_settings.gecko.id, 'riji-browser@riji.local');
  assert.equal(manifest.incognito, 'not_allowed');
  assert.deepEqual(manifest.permissions, ['tabs', 'storage', 'alarms']);
});

test('connection failure only contacts the selected environment endpoint', async () => {
  const { calls, states } = await connectHttp('development', true);
  assert.equal(calls.length, 1); assert.equal(calls[0].url, 'http://127.0.0.1:4177/v1/probe');
  assert.match(states.at(-1).status, /无法连接/);
});

test('a fresh installation connects to production without a pairing code', async () => {
  const { calls, states } = await connectHttp(undefined);
  assert.equal(calls.length, 2); assert.equal(calls[0].url, 'http://127.0.0.1:4178/v1/probe');
  assert.equal(states.at(-1).status, '已连接日迹');
});
