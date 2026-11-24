// node shoot.js stills <outDir> <t1,t2,...> [width]      -> one PNG per time
// node shoot.js render <outDir> <fps> [from] [to]        -> every frame as JPEG (for ffmpeg)
const puppeteer = require('puppeteer-core');
const path = require('path'), fs = require('fs');
const FILM = 'file:///F:/Claude%20(%20New%20Start%20)/Project%20Ideas/DynmicIsland/design/launch/film.html?capture';

(async () => {
  const [mode, out, a, b, c] = process.argv.slice(2);
  fs.mkdirSync(out, { recursive: true });
  const browser = await puppeteer.launch({
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true,
    args: ['--allow-file-access-from-files', '--force-device-scale-factor=1', '--hide-scrollbars', '--force-color-profile=srgb', '--disable-lcd-text'],
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080, deviceScaleFactor: 1 });
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text()); });
  await page.goto(FILM);
  await page.waitForFunction('window.__ready === true', { timeout: 30000 });
  const seek = t => page.evaluate(x => new Promise(r => { window.__seek(x); requestAnimationFrame(() => requestAnimationFrame(r)); }), t);
  if (mode === 'stills') {
    for (const s of a.split(',')) {
      await seek(+s);
      await page.screenshot({ path: path.join(out, (+s).toFixed(2).padStart(5, '0') + '.png') });
    }
  } else {
    const fps = +a, from = +(b || 0), to = +(c || 27.3), n0 = Math.round(from * fps), n1 = Math.round(to * fps);
    const t0 = Date.now();
    for (let n = n0; n < n1; n++) {
      await seek(n / fps);
      await page.screenshot({ path: path.join(out, String(n).padStart(5, '0') + '.jpg'), type: 'jpeg', quality: 95 });
      if (n % 120 === 0) console.log(n, '/', n1, ((Date.now() - t0) / 1000).toFixed(0) + 's');
    }
  }
  await browser.close();
})();
