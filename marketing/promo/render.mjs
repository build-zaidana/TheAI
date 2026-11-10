// Draws video.html frame by frame in headless Chrome and pipes the frames to ffmpeg.
//   node render.mjs <lang> <out.mp4> [fps] [--still t1,t2,...]
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const [lang, out, fpsArg, stillFlag, stillList] = process.argv.slice(2);
const fps = Number(fpsArg || 30);
const vertical = process.env.FORMAT === 'vertical';
const [width, height] = vertical ? [1080, 1920] : [1920, 1080];
const chrome = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const ffmpeg = process.env.FFMPEG;
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(path.join(tmpdir(), 'promo-'));
const browser = spawn(chrome, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });

async function target() {
  for (let i = 0; i < 60; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = list.find(t => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await new Promise(r => setTimeout(r, 250));
  }
  throw new Error('Chrome did not start');
}

const ws = new WebSocket(await target());
await new Promise(r => { ws.onopen = r; });
let id = 0;
const waiting = new Map();
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); }
};
const send = (method, params = {}) => new Promise(resolve => { const n = ++id; waiting.set(n, resolve); ws.send(JSON.stringify({ id: n, method, params })); });

await send('Page.enable');
await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: pathToFileURL(path.join(here, 'video.html')).href + `?lang=${lang}${vertical ? '&format=vertical' : ''}` });
await new Promise(r => setTimeout(r, 1500));
const failed = await send('Runtime.evaluate', { expression: 'typeof render', returnByValue: true });
if (failed.result.result.value !== 'function') throw new Error('render() missing: ' + JSON.stringify(failed.result));

async function frame(t, format = 'jpeg') {
  const r = await send('Runtime.evaluate', { expression: `render(${t})`, returnByValue: true, awaitPromise: true });
  if (r.result.exceptionDetails) throw new Error(`t=${t}: ` + JSON.stringify(r.result.exceptionDetails.exception?.description));
  const shot = await send('Page.captureScreenshot', format === 'jpeg' ? { format: 'jpeg', quality: 97 } : { format: 'png' });
  return Buffer.from(shot.result.data, 'base64');
}

if (stillFlag === '--still') {
  mkdirSync(out, { recursive: true });
  for (const t of stillList.split(',').map(Number)) {
    writeFileSync(path.join(out, `${lang}-${String(t).padStart(5, '0')}.png`), await frame(t, 'png'));
  }
} else {
  const { result } = await send('Runtime.evaluate', { expression: 'DURATION', returnByValue: true });
  const total = Math.round(result.result.value * fps);
  const encoder = spawn(ffmpeg, ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'mjpeg', '-i', '-',
    '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-shortest',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '20', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-c:a', 'aac', '-b:a', '96k', out], { stdio: ['pipe', 'inherit', 'inherit'] });
  const started = Date.now();
  for (let i = 0; i < total; i++) {
    const data = await frame(i / fps);
    if (!encoder.stdin.write(data)) await new Promise(r => encoder.stdin.once('drain', r));
    if (i % 150 === 0) console.log(`${lang}: ${i}/${total} in ${Math.round((Date.now() - started) / 1000)} s`);
  }
  encoder.stdin.end();
  await new Promise(r => encoder.on('close', r));
  console.log(`${lang}: done, ${total} frames`);
}
ws.close();
browser.kill();
process.exit(0);
