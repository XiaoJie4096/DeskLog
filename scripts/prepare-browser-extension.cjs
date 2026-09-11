const fs = require('node:fs');
const path = require('node:path');
const browser = process.argv[2];
if (!['chrome', 'edge', 'firefox'].includes(browser)) throw new Error('用法：node scripts/prepare-browser-extension.cjs chrome|edge|firefox');
const root = path.resolve(__dirname, '..');
const source = path.join(root, 'browser-extension');
const destination = path.join(root, 'artifacts', 'extensions', browser);
// Prepare an unpacked development directory from one shared source; no ZIP or installer.
fs.mkdirSync(destination, { recursive: true });
for (const name of ['worker.js', 'options.js', 'options.html', 'options.css']) fs.copyFileSync(path.join(source, name), path.join(destination, name));
fs.copyFileSync(path.join(source, browser === 'firefox' ? 'manifest.firefox.json' : 'manifest.json'), path.join(destination, 'manifest.json'));
console.log(destination);
