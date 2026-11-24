// The film is a pure function of time: render(t) sets every style from t alone (no CSS animations),
// so it plays live, scrubs both ways, and is captured frame by frame to exactly the same picture.
const W = 1920, H = 1080, DUR = 27.3;
const stage = document.getElementById('stage');

const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, t) => a + (b - a) * t;
const prog = (t, a, b) => clamp((t - a) / (b - a));
const E = {
  lin: x => x,
  in2: x => x * x, in3: x => x * x * x,
  out2: x => 1 - (1 - x) * (1 - x), out3: x => 1 - Math.pow(1 - x, 3), out5: x => 1 - Math.pow(1 - x, 5),
  io2: x => x < .5 ? 2 * x * x : 1 - Math.pow(-2 * x + 2, 2) / 2,
  io3: x => x < .5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2,
  back: (x, s = 1.7) => 1 + (s + 1) * Math.pow(x - 1, 3) + s * Math.pow(x - 1, 2),
  bump: x => Math.sin(Math.PI * clamp(x)),
};
function rng(seed) { let a = seed >>> 0; return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const mix = (c1, c2, t) => { const p = c => [1, 3, 5].map(i => parseInt(c.slice(i, i + 2), 16)); const a = p(c1), b = p(c2); return `rgb(${a.map((v, i) => Math.round(lerp(v, b[i], t))).join(',')})`; };

function el(cls, parent, html, css) {
  const d = document.createElement('div');
  if (cls) d.className = cls;
  if (html != null) d.innerHTML = html;
  if (css) d.style.cssText = css;
  if (parent) parent.appendChild(d);
  return d;
}
// position / scale / rotate / fade / blur in one call (x, y in stage pixels)
function put(e, o) {
  const x = o.x || 0, y = o.y || 0, s = o.s == null ? 1 : o.s, sx = o.sx == null ? s : o.sx, sy = o.sy == null ? s : o.sy, r = o.r || 0;
  e.style.transform = `translate(${x.toFixed(2)}px,${y.toFixed(2)}px)` + (r ? ` rotate(${r.toFixed(3)}deg)` : '') + (sx !== 1 || sy !== 1 ? ` scale(${sx.toFixed(5)},${sy.toFixed(5)})` : '');
  if (o.o != null) e.style.opacity = o.o <= .001 ? 0 : o.o.toFixed(4);
  if (o.blur != null) e.style.filter = o.blur > .25 ? `blur(${o.blur.toFixed(2)}px)` : 'none';
}

const scenes = [];
// lead: the scene is drawn as if it were that much later (its own clock runs ahead), to sit exactly on the reference's timing
function scene(id, from, to, build, lead = 0) { scenes.push({ id, from, to, build, lead, on: false, draw: () => {} }); }
// (built once the font is in, because several scenes measure their text)
function buildAll() {
  for (const s of scenes) {
    s.root = el('scene', stage); s.root.id = id(s);
    s.root.style.display = 'block';
    s.draw = s.build(s.root) || s.draw;
    s.root.style.display = 'none';
  }
  function id(s) { return 's' + s.id; }
}
function render(t) {
  for (const s of scenes) {
    const on = t >= s.from && t < s.to;
    if (on !== s.on) { s.root.style.display = on ? 'block' : 'none'; s.on = on; }
    if (on) s.draw(t + s.lead);
  }
}

// ---------------------------------------------------------------- shared pieces

const HAND = `<svg viewBox="0 0 164 200"><defs>
  <path id="hs" d="M42 106V23a14 14 0 0 1 28 0v38a13 13 0 0 1 26 0v6a12.5 12.5 0 0 1 25 0v8a11.5 11.5 0 0 1 23 0v44c0 22-8 34-18 41v20H62v-17C52 154 40 142 30 128 20 122 4 112 7 99 9 88 22 86 30 94Z"/></defs>
  <use href="#hs" transform="translate(9 11)" fill="#2a2a2e" stroke="#2a2a2e" stroke-width="14" stroke-linejoin="round"/>
  <use href="#hs" fill="#111" stroke="#111" stroke-width="14" stroke-linejoin="round"/><use href="#hs" fill="#fff"/>
  <path d="M70 61v17M96 67v13M121 75v9" stroke="#111" stroke-width="5.5" stroke-linecap="round" fill="none"/>
  <path d="M80 101l7 29M100 104l6 27M119 108l5 24" stroke="#111" stroke-width="7.5" stroke-linecap="round" fill="none"/></svg>`;
// a hand whose fingertip sits at (x, y); size in px
function hand(parent, size, css) {
  const h = el('hand', parent, HAND, css || '');
  h.size = size;
  return h;
}
function putHand(h, x, y, o = {}) {
  const k = h.size / 200 * (o.s == null ? 1 : o.s);
  h.style.transform = `translate(${(x - 57).toFixed(2)}px,${(y - 6).toFixed(2)}px) rotate(${(o.r == null ? -30 : o.r).toFixed(2)}deg) scale(${k.toFixed(4)})`;
  h.style.opacity = o.o == null ? 1 : o.o;
  h.style.filter = (o.blur > .25 ? `blur(${o.blur.toFixed(1)}px) ` : '') + 'drop-shadow(0 5px 7px rgba(0,0,0,.28))';
}

const BL = {
  blue: ['#9fd2ff', '#2f8bff', '#0a4fc4'], red: ['#ffa3ad', '#f0344f', '#a40f2c'], purple: ['#cfb0ff', '#7c3aed', '#4a1fa8'],
  pink: ['#ffb3db', '#ff2d92', '#b40f63'], green: ['#b3f7c8', '#30d158', '#128a36'], orange: ['#ffdca6', '#ff9f0a', '#c26a00'],
  teal: ['#b3f3f3', '#32c8d2', '#0e8791'], yellow: ['#fff4ad', '#ffd60a', '#c29b00'], ice: ['#ffffff', '#e3eaf3', '#86acd8'],
};
let blN = 0;
// Bloub: an orb with two capsule eyes. eyes: 'open' | 'sleepy'; look: [dx, dy] in a 100-unit box
function bloubSvg(color, o = {}) {
  const [a, b, c] = BL[color], id = 'bl' + (blN++), [dx, dy] = o.look || [0, 0];
  const eyes = o.eyes === 'sleepy'
    ? `<rect x="${31 + dx}" y="${45 + dy}" width="15" height="5.5" rx="2.75"/><rect x="${55 + dx}" y="${45 + dy}" width="14" height="5.5" rx="2.75"/>`
    : `<g class="eyes" style="transform-origin:50px 45px"><rect x="${35.5 + dx}" y="${29 + dy}" width="12.4" height="31" rx="6.2"/><rect x="${56 + dx}" y="${30.5 + dy}" width="10.6" height="28" rx="5.3"/></g>`;
  return `<svg viewBox="0 0 100 100"><defs><radialGradient id="${id}" cx="40%" cy="26%" r="82%"><stop offset="0" stop-color="${a}"/><stop offset=".5" stop-color="${b}"/><stop offset="1" stop-color="${c}"/></radialGradient></defs>
    <circle cx="50" cy="50" r="48" fill="url(#${id})"/><ellipse cx="33" cy="23" rx="13" ry="6.6" transform="rotate(-28 33 23)" fill="#fff" opacity=".5"/>
    <g fill="#0b0b10" transform="rotate(${o.tilt == null ? 6 : o.tilt} 50 45)">${eyes}</g></svg>`;
}
function bloub(parent, color, size, o = {}) {
  const b = el('bl', parent, bloubSvg(color, o), `width:${size}px;height:${size}px;`);
  if (o.badge === 'need') el('badge', b, '!', `right:${-size * .04}px;top:${-size * .06}px;width:${size * .36}px;height:${size * .36}px;background:#ff9f0a;font-size:${size * .26}px;`);
  if (o.badge === 'work') el('badge', b, '<i></i><i></i><i></i>', `left:${-size * .08}px;top:${-size * .04}px;width:${size * .46}px;height:${size * .28}px;background:#2f8bff;gap:${size * .035}px;`)
    .querySelectorAll('i').forEach(i => i.style.cssText = `width:${size * .07}px;height:${size * .07}px;border-radius:50%;background:#fff;display:block`);
  if (o.badge === 'done') el('badge', b, '✓', `right:${-size * .04}px;top:${-size * .06}px;width:${size * .36}px;height:${size * .36}px;background:#30d158;font-size:${size * .24}px;`);
  return b;
}

// The opening world: soft green hills under a blue sky (painted here, once).
function paintHills() {
  const c = document.createElement('canvas'); c.width = 1920; c.height = 1080;
  const g = c.getContext('2d'), R = rng(11);
  const sky = g.createLinearGradient(0, 0, 0, 640);
  sky.addColorStop(0, '#17557f'); sky.addColorStop(.42, '#2a7fa0'); sky.addColorStop(.8, '#6fb2bf'); sky.addColorStop(1, '#a9d2cc');
  g.fillStyle = sky; g.fillRect(0, 0, W, H);
  const haze = g.createRadialGradient(1500, 520, 0, 1500, 520, 900);
  haze.addColorStop(0, 'rgba(210,235,225,.55)'); haze.addColorStop(1, 'rgba(210,235,225,0)');
  g.fillStyle = haze; g.fillRect(0, 0, W, H);
  const layers = 7;
  for (let i = 0; i < layers; i++) {
    const f = i / (layers - 1), base = 500 + f * f * 420 + i * 22, amp = 34 + f * 46;
    const p1 = R() * 9, p2 = R() * 9, p3 = R() * 9, l1 = 620 - f * 150, l2 = 250 - f * 40;
    const y = x => base + amp * Math.sin(x / l1 + p1) + amp * .45 * Math.sin(x / l2 + p2) + amp * .2 * Math.sin(x / 97 + p3);
    g.beginPath(); g.moveTo(0, H);
    for (let x = 0; x <= W; x += 6) g.lineTo(x, y(x));
    g.lineTo(W, H); g.closePath();
    const top = base - amp * 1.4, gr = g.createLinearGradient(0, top, 0, top + 330);
    const sun = mix('#8fb98a', '#c6d83f', f), mid = mix('#5f9a78', '#86b52c', f), low = mix('#3f7f6a', '#3f7a1e', f);
    gr.addColorStop(0, sun); gr.addColorStop(.3, mid); gr.addColorStop(1, low);
    g.fillStyle = gr; g.fill();
    // light from the left, shade in the dips
    g.save(); g.clip();
    for (let k = 0; k < 7; k++) {
      const x = R() * W, yy = y(x) + 40 + R() * 120, r = 180 + R() * 260;
      const sh = g.createRadialGradient(x, yy, 0, x, yy, r), dark = R() > .45;
      sh.addColorStop(0, dark ? `rgba(20,70,40,${.16 + f * .12})` : `rgba(235,240,120,${.14 + f * .1})`); sh.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = sh; g.fillRect(x - r, yy - r, r * 2, r * 2);
    }
    // flowers
    const n = Math.round(120 + f * f * 2600), cols = ['#f4a3b5', '#ef8a3a', '#fff3d6', '#f6d04a', '#e9739a'];
    for (let k = 0; k < n; k++) {
      const x = R() * W, yy = y(x) + 6 + Math.pow(R(), 1.4) * (90 + f * 220), s = (.7 + R() * 1.5) * (.5 + f * 1.5);
      g.globalAlpha = .55 + R() * .45; g.fillStyle = cols[Math.floor(R() * cols.length)];
      g.beginPath(); g.ellipse(x, yy, s * 1.3, s, 0, 0, 7); g.fill();
    }
    g.globalAlpha = 1; g.restore();
  }
  const soft = document.createElement('canvas'); soft.width = W; soft.height = H;
  const s = soft.getContext('2d'); s.filter = 'blur(1.6px) saturate(1.08)'; s.drawImage(c, 0, 0);
  return soft.toDataURL('image/jpeg', .92);
}
const HILLS = paintHills();
