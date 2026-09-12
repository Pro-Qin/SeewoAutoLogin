// 用 CDP 驱动 Edge 逐帧渲染 film.html，输出 JPEG 帧序列（确定性渲染，无过渡残留）
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const HTML = process.argv[2] || 'film.html';        // 用法：node render-film.mjs film-fast.html 16.1
const FPS = 30, DURATION = parseFloat(process.argv[3] || '15.0');
const W = 1280, H = 720;
const FRAMES = join(HERE, 'frames');
const PORT = 9333;
const EDGE = process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

rmSync(FRAMES, { recursive: true, force: true });
mkdirSync(FRAMES, { recursive: true });

const target = 'file:///' + join(HERE, HTML).replace(/\\/g, '/');
const edge = spawn(EDGE, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--mute-audio',
  '--remote-debugging-port=' + PORT,
  '--window-size=' + W + ',' + H,
  '--user-data-dir=' + join(FRAMES, '..', '.edge-profile'),
  'about:blank'
], { stdio: 'ignore' });

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function findPage() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch('http://127.0.0.1:' + PORT + '/json/list');
      const list = await res.json();
      const page = list.find(t => t.type === 'page');
      if (page && page.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch {}
    await sleep(250);
  }
  throw new Error('无法连接到 Edge 调试端口');
}

class CDP {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map();
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
      }
    };
  }
  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
}

const wsUrl = await findPage();
const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
const cdp = new CDP(ws);

await cdp.send('Page.enable');
await cdp.send('Runtime.enable');
await cdp.send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
await cdp.send('Page.navigate', { url: target });

// 等页面脚本就绪
for (let i = 0; i < 80; i++) {
  const r = await cdp.send('Runtime.evaluate', { expression: 'window.__ready === true', returnByValue: true });
  if (r.result && r.result.value === true) break;
  await sleep(150);
}
// 等图片解码完成
await cdp.send('Runtime.evaluate', { expression: 'Promise.all(Array.from(document.images).map(i=>i.decode().catch(()=>0)))', awaitPromise: true });

const total = Math.round(FPS * DURATION);
const t0 = Date.now();
for (let f = 0; f < total; f++) {
  const t = f / FPS;
  await cdp.send('Runtime.evaluate', { expression: 'window.__render(' + t.toFixed(4) + ')' });
  const shot = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 94, captureBeyondViewport: false });
  writeFileSync(join(FRAMES, String(f).padStart(5, '0') + '.jpg'), Buffer.from(shot.data, 'base64'));
  if ((f + 1) % 60 === 0) console.log('  已渲染 ' + (f + 1) + '/' + total + ' 帧');
}
console.log('帧序列完成：' + total + ' 帧，用时 ' + ((Date.now() - t0) / 1000).toFixed(1) + 's');

ws.close();
edge.kill();
await sleep(300);
process.exit(0);