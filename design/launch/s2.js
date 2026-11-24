const Shared = {};

// Warm clouds for the fresco scene (painted here; they turn grey when the colour drains).
function paintClouds() {
  const w = 960, h = 540, c = document.createElement('canvas'); c.width = w; c.height = h;
  const g = c.getContext('2d'), img = g.createImageData(w, h), R = rng(23);
  const P = new Float32Array(65536); for (let i = 0; i < 65536; i++) P[i] = R();
  const n2 = (x, y) => {
    const xi = Math.floor(x), yi = Math.floor(y), xf = x - xi, yf = y - yi, u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    const a = P[(yi & 255) * 256 + (xi & 255)], b = P[(yi & 255) * 256 + ((xi + 1) & 255)], c2 = P[((yi + 1) & 255) * 256 + (xi & 255)], d = P[((yi + 1) & 255) * 256 + ((xi + 1) & 255)];
    return a + (b - a) * u + (c2 - a) * v + (a - b - c2 + d) * u * v;
  };
  const fbm = (x, y, o) => { let s = 0, a = .5, f = 1; for (let k = 0; k < o; k++) { s += a * n2(x * f, y * f); f *= 2.03; a *= .5; } return s; };
  const D = new Float32Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const X = x / 190, Y = y / 135, wx = fbm(X * .6 + 3.1, Y * .6 + 1.7, 3), wy = fbm(X * .6 + 8.4, Y * .6 + 5.2, 3);
    D[y * w + x] = fbm(X + 2.1 * wx, Y + 2.1 * wy, 6);                    // billows: noise bent by noise
  }
  const sm = (a, b, x) => { const t = clamp((x - a) / (b - a)); return t * t * (3 - 2 * t); };
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const d = D[y * w + x], t = sm(.36, .66, d);
    const lit = clamp(.56 + (d - D[Math.min(h - 1, y + 7) * w + Math.min(w - 1, x + 5)]) * 7);   // light from the upper left
    const i = (y * w + x) * 4, deep = sm(.2, .44, d);
    const sr = lerp(128, 190, deep), sg = lerp(82, 138, deep), sb = lerp(30, 62, deep);            // the gaps between clouds
    const cr = lerp(196, 255, lit), cg = lerp(146, 243, lit), cb = lerp(72, 208, lit);             // shaded to sunlit
    img.data[i] = lerp(sr, cr, t); img.data[i + 1] = lerp(sg, cg, t); img.data[i + 2] = lerp(sb, cb, t); img.data[i + 3] = 255;
  }
  g.putImageData(img, 0, 0);
  const big = document.createElement('canvas'); big.width = W; big.height = H;
  const s = big.getContext('2d'); s.imageSmoothingQuality = 'high'; s.filter = 'blur(1.2px)'; s.drawImage(c, 0, 0, W, H);
  return big.toDataURL('image/jpeg', .92);
}

// ============================================================================================
// E  (8.1 - 10.14)  Typewriter: "But, what if one glance". The camera runs with the cursor.
// The typing plays 20% slower than the reference so it can be read; the hands shot after it gives up that time.
// It arrives through the last dot of "But...": that dot opens like an iris onto this page.
// ============================================================================================
scene('E', 8.1, 10.144, root => {
  root.style.background = '#f6e7d3'; root.style.zIndex = 3;
  const band = el('abs', root, '', 'left:0;top:150px;width:1920px;height:740px;background:#f6e7d3');
  const glows = [[-250, -200, 1100], [900, 80, 900], [1900, -260, 1200], [2900, 120, 1000], [3900, -200, 1100]].map(([x, y, s]) =>
    ({ x, e: el('abs', band, '', `top:${y}px;width:${s}px;height:${s}px;border-radius:50%;background:radial-gradient(circle,#d2f695 0%,rgba(210,246,149,0) 66%)`) }));
  const cam = el('abs', band, '', 'left:0;top:0;width:0;height:740px;');
  const line = el('abs', cam, '', 'left:0;top:0;height:740px;display:flex;align-items:center;white-space:pre;font-size:470px;font-weight:500;color:#0a0a0a;letter-spacing:-.035em;');
  const TEXT = 'But, what if one glance', N = TEXT.length;
  const chars = [...TEXT].map((ch, i) => el('', line, ch === ' ' ? '&nbsp;' : ch, 'display:inline-block;' + (i >= 17 ? 'font-weight:700;' : '')));
  const xs = chars.map(c => c.offsetLeft + c.offsetWidth);
  const cur = el('abs', cam, '', 'top:86px;width:56px;height:568px;background:#0a0a0a');
  const barT = el('abs', root, '', 'left:0;top:0;width:1920px;height:150px;background:#000');
  const barB = el('abs', root, '', 'left:0;top:890px;width:1920px;height:190px;background:#000');
  const LEFT = 96, STOP = 1500;
  // how many letters are typed at each moment (it follows the voice: quick at first, slower on the last word)
  const KEYS = [[8.0, 3], [8.44, 3], [8.5, 4], [8.67, 9], [8.83, 12], [9.0, 15], [9.17, 18], [9.33, 20], [9.5, 22], [9.66, 23]];
  const typed = t => { if (t <= KEYS[0][0]) return KEYS[0][1]; for (let i = 1; i < KEYS.length; i++) if (t < KEYS[i][0]) return lerp(KEYS[i - 1][1], KEYS[i][1], (t - KEYS[i - 1][0]) / (KEYS[i][0] - KEYS[i - 1][0])); return N; };
  const edge = t => {                                                   // where the typed text ends, in line pixels
    const nf = typed(t), i = Math.floor(nf), f = nf - i;
    return lerp(i ? xs[i - 1] : 0, i < N ? xs[i] : xs[N - 1], E.out3(f));
  };
  const shift = t => Math.max(0, LEFT + edge(t) + 36 - STOP);

  const SLOW = 1.2, T0 = 8.44;                                          // the typing clock, from the first new key on
  return real => {
    const t = real <= T0 ? real : T0 + (real - T0) / SLOW;
    const nf = typed(t), sh = shift(t), zo = 1 - .07 * E.io2(prog(t, 9.66, 9.92));
    cam.style.transformOrigin = `${STOP}px 370px`;
    cam.style.transform = `scale(${zo.toFixed(4)})`;
    line.style.transform = `translateX(${(LEFT - sh).toFixed(1)}px)`;
    chars.forEach((c, i) => {
      const age = nf - i, p = E.out3(prog(age, 0, 1.1));
      c.style.opacity = age <= 0 ? 0 : prog(age, 0, .45);
      c.style.transform = `translateX(${((1 - p) * 46).toFixed(1)}px)`;
      c.style.filter = p < 1 && age > 0 ? `blur(${((1 - p) * 16).toFixed(1)}px)` : 'none';
    });
    cur.style.transform = `translateX(${(LEFT + edge(t) + 36 - sh).toFixed(1)}px)`;
    const d = Shared.dot(real), ip = prog(real, 8.12, 8.44), r0 = d.r * 1.45;
    root.style.clipPath = ip >= 1 ? 'none' : `circle(${(r0 * prog(real, 8.12, 8.17) * Math.pow(2400 / r0, Math.pow(ip, 1.6))).toFixed(1)}px at ${d.x.toFixed(1)}px ${d.y.toFixed(1)}px)`;
    const lb = 1 - E.io3(prog(real, 8.34, 8.64));                          // the page arrives whole, then the frame closes on the line
    barT.style.transform = `translateY(${(-150 * lb).toFixed(1)}px)`; barB.style.transform = `translateY(${(190 * lb).toFixed(1)}px)`;
    for (const g of glows) g.e.style.transform = `translateX(${(g.x - sh * .45).toFixed(1)}px)`;
  };
});

// ============================================================================================
// D  (7.60 - 8.5)  "But..." on the hills. The last dot swells and opens onto the typewriter page,
// while the camera settles so that this white "But" lies exactly on the page's black one.
// ============================================================================================
scene('D', 7.601, 8.5, root => {
  const cam = el('fill', root, '', 'transform-origin:0 0');
  const bg = el('fill cover', cam, '', `background-image:url(${HILLS});transform:scale(1.75);transform-origin:38% 64%;filter:blur(3px)`);
  const shade = el('fill', cam, '', 'background:#062a33;opacity:0;transform:scale(1.7)');
  const line = el('fill', cam, '', 'display:flex;align-items:center;justify-content:center;padding-bottom:70px;font-size:520px;font-weight:500;color:#fff;letter-spacing:-.035em;text-shadow:0 6px 40px rgba(0,50,70,.2)');
  const ls = [...'But...'].map((ch, i) => ({ at: i < 3 ? 7.601 + i * .036 : 7.74 + (i - 3) * .08, el: el('', line, ch, 'display:inline-block') }));
  // where the ink of the last dot is
  const c = document.createElement('canvas').getContext('2d'); c.font = '500 520px Inter';
  const m = c.measureText('.'), last = ls[5].el;
  const dot = { x: last.offsetLeft + (m.actualBoundingBoxRight - m.actualBoundingBoxLeft) / 2,
                y: last.offsetTop + m.fontBoundingBoxAscent - (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2,
                r: (m.actualBoundingBoxRight + m.actualBoundingBoxLeft) / 2 };
  last.style.transformOrigin = `${(dot.x - last.offsetLeft).toFixed(1)}px ${(dot.y - last.offsetTop).toFixed(1)}px`;
  // the typewriter's "But": 470px letters starting at x 96, centred on y 520 (ours: 520px letters centred on y 505)
  const S1 = 470 / 520, TX = 96 - S1 * ls[0].el.offsetLeft, TY = 520 - S1 * 505;
  const camAt = t => { const k = E.io2(prog(t, 7.98, 8.34)); return { s: lerp(1, S1, k), x: TX * k, y: TY * k }; };
  Shared.dot = t => { const k = camAt(t); return { x: k.s * dot.x + k.x, y: k.s * dot.y + k.y, r: k.s * dot.r }; };
  return t => {
    ls.forEach((l, i) => {
      const p = E.out3(prog(t, l.at, l.at + .2));
      l.el.style.opacity = prog(t, l.at, l.at + .05);
      const swell = i === 5 ? 1 + .6 * E.back(prog(t, 8.0, 8.18), 2.4) : 1;           // the dot takes a breath before it opens
      l.el.style.transform = `translateY(${((1 - p) * 330).toFixed(1)}px) scale(${swell.toFixed(4)})`;
      l.el.style.filter = p < 1 ? `blur(${((1 - p) * 42).toFixed(1)}px)` : 'none';
    });
    const k = camAt(t);
    cam.style.transform = `translate(${k.x.toFixed(2)}px,${k.y.toFixed(2)}px) scale(${k.s.toFixed(5)})`;
    shade.style.opacity = .26 * prog(t, 7.98, 8.3);
  };
});

// ============================================================================================
// F  (10.14 - 12.78)  The island between the two hands, the style flashes, the name.
// ============================================================================================
scene('F', 10.144, 12.78, root => {
  const cam = el('fill', root, '', 'transform-origin:50% 50%');
  const fres = el('fill', cam, '', 'background:#d9a54a');
  const sky = el('fill cover', fres, '', `background-image:url(${paintClouds()});transform-origin:50% 50%`);
  el('fill', fres, '', 'background:radial-gradient(ellipse at 50% 46%,rgba(255,246,220,.4) 0%,rgba(255,246,220,0) 42%,rgba(84,48,10,.42) 100%)');
  const SH = 'filter:saturate(1.75) contrast(1.1) brightness(1.02) drop-shadow(0 24px 34px rgba(70,34,0,.3))';
  const hl = el('abs', fres, '', `width:684px;height:380px;background:url(assets/handL.png) 0 0/100% 100%;transform-origin:656px 241px;${SH}`);
  const hr = el('abs', fres, '', `width:732px;height:450px;background:url(assets/handR.png) 0 0/100% 100%;transform-origin:11.5px 239.5px;${SH}`);
  // looks for the one-frame flashes
  const R = rng(5), P = [];
  for (let j = 0; j <= 7; j++) { P.push([]); for (let i = 0; i <= 11; i++) P[j].push([i * 175 + (R() - .5) * 120, j * 155 + (R() - .5) * 110]); }
  let d = '';
  for (let j = 0; j <= 7; j++) for (let i = 0; i <= 11; i++) {
    if (i < 11) d += `M${P[j][i]}L${P[j][i + 1]}`;
    if (j < 7) d += `M${P[j][i]}L${P[j + 1][i + (R() > .5 && i < 11 ? 1 : 0)]}`;
  }
  const glass = el('fill', fres, `<svg viewBox="0 0 1920 1080" style="width:100%;height:100%"><path d="${d}" stroke="#2a1608" stroke-width="7" fill="none" stroke-linejoin="round"/></svg>`, 'display:none');
  const hatch = el('fill', fres, '', 'display:none;background:repeating-linear-gradient(38deg,rgba(40,60,110,.0) 0 7px,rgba(40,60,110,.22) 7px 9px)');

  const pill = el('abs', cam, '', 'left:460px;top:390px;width:1000px;height:300px;border-radius:150px;background:#000;box-shadow:0 34px 90px rgba(70,35,0,.38);transform-origin:50% 50%;overflow:hidden');
  const A = el('fill', pill, '', 'display:flex;align-items:center;justify-content:center;gap:44px');
  const trio = el('', A, '', 'position:relative;width:392px;height:160px');
  [['blue', 'work', 0], ['red', 'work', 116], ['purple', 'need', 232]].forEach(([c, b, x]) => { const o = bloub(trio, c, 160, { badge: b }); o.style.left = x + 'px'; o.style.top = '0'; });
  el('', A, '<div style="font-size:38px;font-weight:600;color:#8e8e93;letter-spacing:-.01em;margin-bottom:4px">Claude Code</div><div style="font-size:84px;font-weight:600;color:#ff9f0a;letter-spacing:-.03em;line-height:1">1 needs you</div>', 'white-space:nowrap');
  const B = el('fill', pill, '', 'display:none;align-items:center;justify-content:center;gap:36px');
  const orb = bloub(B, 'ice', 176, { look: [6, -3], tilt: 12 }); orb.style.position = 'relative';
  el('', B, 'Island', 'font-size:158px;font-weight:700;color:#fff;letter-spacing:-.04em');

  const FL = [11.42, 11.50, 11.58, 11.67, 11.75];
  const LOOK = ['saturate(2.1) contrast(1.8) brightness(1.12)', 'saturate(1.35) brightness(1.06)', 'grayscale(1) contrast(2.6) brightness(1.5)', 'saturate(1.7) contrast(1.1) brightness(1.08)'];
  const PBG = ['#6b3f22', '#7a4a28', '#2c2c2e', '#7a4a28'], PS = [1, .82, .68, .58], G = [.25, .5, .7, .86];

  return t => {
    const dr = prog(t, 10.204, 11.42), f = t < FL[0] ? -1 : t >= FL[4] ? 4 : FL.findIndex((x, i) => t >= x && t < FL[i + 1]);
    const end = prog(t, 11.75, 12.78);
    const g = f < 0 ? 0 : f < 4 ? G[f] : 1;
    const wi = 1 - E.out5(prog(t, 10.204, 10.444));
    cam.style.transform = `translateX(${(300 * wi).toFixed(1)}px) scale(${(1 + .035 * dr - (f === 4 ? .03 * end : 0)).toFixed(4)})`;
    cam.style.filter = wi > .02 ? `blur(${(20 * wi).toFixed(1)}px)` : 'none';
    const q = prog(t, 11.75, 12.02);
    fres.style.filter = f < 0 ? 'saturate(1.15) contrast(1.04)' : f < 4 ? LOOK[f]
      : `saturate(${lerp(1.15, 0, q).toFixed(3)}) contrast(1.04) brightness(${lerp(1, 1.12, q).toFixed(3)})`;
    glass.style.display = f === 0 ? 'block' : 'none'; hatch.style.display = f === 2 ? 'block' : 'none';
    sky.style.transform = `scale(${(1.25 - .2 * g + .04 * dr).toFixed(4)}) translateX(${(-30 * dr).toFixed(1)}px)`;
    const hs = lerp(1.95, 1.36, g);
    hl.style.transform = `translate(${(lerp(lerp(392, 446, dr), 538, g) - 656).toFixed(1)}px,${(lerp(505, 528, g) - 241).toFixed(1)}px) scale(${hs.toFixed(4)})`;
    hr.style.transform = `translate(${(lerp(lerp(1600, 1508, dr), 1382, g) - 11.5).toFixed(1)}px,${(lerp(560, 548, g) - 239.5).toFixed(1)}px) scale(${hs.toFixed(4)})`;
    const ps = f < 0 ? 1 : f < 4 ? PS[f] : lerp(.54, .5, end);
    pill.style.transform = `translateX(${(lerp(70, 0, E.out2(dr))).toFixed(1)}px) scale(${ps.toFixed(4)})`;
    pill.style.background = f >= 0 && f < 4 ? PBG[f] : '#000';
    const name = f >= 1;
    A.style.display = name ? 'none' : 'flex'; B.style.display = name ? 'flex' : 'none';
  };
}, .06);

// ============================================================================================
// G  (12.5 - 15.17)  "No more endless alt-tabbing" + the reaction bubble.
// ============================================================================================
scene('G', 12.42, 15.14, root => {
  const bg = el('fill', root, '', 'background:#e4e4e7');
  const grp = el('fill', root, '', 'transform-origin:50% 50%');
  const line = el('abs', grp, '', 'left:0;top:0;width:1920px;height:1080px;display:flex;align-items:center;justify-content:center;gap:28px;white-space:nowrap;font-size:104px;font-weight:500;color:#0c0c0e;letter-spacing:-.03em;');
  const ws = [['No', 12.75], ['more', 12.84], ['<b style="color:#1f4fd8;font-weight:700">endless</b>', 12.93], ['alt-tabbing', 13.1]].map(([h, at]) => ({ at, el: el('', line, h, '') }));
  const last = ws[3].el, rx = last.offsetLeft + last.offsetWidth, ry = last.offsetTop;
  const BX = rx + 18, BY = ry - 62;
  const dot = el('abs', grp, '', `left:${rx - 6}px;top:${ry + 26}px;width:30px;height:30px;border-radius:50%;background:#1f4fd8;transform-origin:50% 50%`);
  const bub = el('abs', grp, '', `left:${BX - 100}px;top:${BY - 100}px;width:200px;height:200px;border-radius:50%;background:#1f4fd8;transform-origin:30% 85%;display:flex;align-items:center;justify-content:center`);
  const heart = el('abs', bub, '<svg viewBox="0 0 100 100" style="width:100%;height:100%"><path d="M50 84C20 62 8 46 8 30 8 16 19 8 30 8c9 0 16 5 20 12 4-7 11-12 20-12 11 0 22 8 22 22 0 16-12 32-42 54Z" fill="none" stroke="#fff" stroke-width="8" stroke-linejoin="round"/></svg>', 'left:48px;top:52px;width:104px;height:104px');
  const face = bloub(bub, 'ice', 150, { look: [-3, 2], tilt: 4 }); face.style.left = '25px'; face.style.top = '25px';
  const eyes = face.querySelector('.eyes');
  const h = hand(root, 74);
  return t => {
    bg.style.opacity = prog(t, 12.5, 12.76);
    grp.style.transform = `scale(${lerp(.86, 1, prog(t, 12.75, 15.17)).toFixed(4)})`;
    for (const w of ws) {
      const p = E.out3(prog(t, w.at, w.at + .32));
      w.el.style.opacity = p; w.el.style.transform = `translateX(${((1 - p) * 70).toFixed(1)}px)`;
      w.el.style.filter = p < 1 ? `blur(${((1 - p) * 16).toFixed(1)}px)` : 'none';
    }
    put(dot, { s: E.back(prog(t, 13.42, 13.58)), o: t < 13.42 ? 0 : 1 });
    const bp = prog(t, 13.52, 13.78);
    bub.style.opacity = t < 13.52 ? 0 : 1;
    bub.style.transform = `scale(${E.back(bp, 2.2).toFixed(4)}) rotate(${(5 * Math.sin((t - 13.5) * 2.6) * prog(t, 13.8, 14.4)).toFixed(2)}deg)`;
    heart.style.opacity = 1 - prog(t, 13.72, 13.8);
    const fp = prog(t, 13.74, 13.98);
    face.style.opacity = fp > 0 ? 1 : 0; face.style.transform = `scale(${E.back(fp, 2.4).toFixed(4)})`;
    const bl = Math.max(E.bump(prog(t, 14.3, 14.44)), E.bump(prog(t, 14.84, 14.98)));
    eyes.style.transform = `scaleY(${(1 - .9 * bl).toFixed(3)})`;
    const hp = prog(t, 13.98, 15.17), up = E.io2(prog(t, 15.0, 15.17));
    putHand(h, lerp(-40, 800, E.out2(hp)), lerp(815, 772, hp) - 150 * up, { o: t < 13.98 ? 0 : 1, r: -14, blur: 9 * (1 - prog(t, 13.98, 14.25)) });
  };
}, .08);

// ============================================================================================
// H  (15.17 - 17.06)  "No more forgotten agents": red letters dancing on an arch, in the dark.
// ============================================================================================
scene('H', 15.14, 17.05, root => {
  root.style.background = '#050505';
  const cam = el('fill', root, '', 'transform-origin:50% 45%');
  const wins = [[-110, -90, -24], [1470, -110, 28], [-170, 760, 19], [1500, 790, -21], [600, 880, -7], [1060, 930, 11], [740, -230, 5]].map(([x, y, r], i) => {
    const w = el('abs', cam, `<div style="display:flex;gap:12px;padding:26px 28px"><i></i><i></i><i></i></div>${[78, 52, 66, 40].map(p => `<div style="height:15px;width:${p}%;margin:20px 30px;border-radius:8px;background:${p === 52 ? '#6d1414' : '#2c2c2c'}"></div>`).join('')}`,
      'left:0;top:0;width:560px;height:350px;border-radius:30px;background:#181818;filter:blur(7px);');
    w.querySelectorAll('i').forEach((d, k) => d.style.cssText = `display:block;width:20px;height:20px;border-radius:50%;background:${k ? '#333' : '#8a1616'}`);
    return { w, x, y, r, i };
  });
  const mk = (s, css) => el('abs', cam, s, 'left:0;top:0;white-space:nowrap;line-height:1;transform-origin:50% 50%;' + css);
  const no = mk('No', 'font-size:178px;font-weight:500;color:#fff'), more = mk('more', 'font-size:92px;font-weight:500;color:#fff'), ag = mk('agents', 'font-size:116px;font-weight:500;color:#fff');
  const SZ = [132, 152, 122, 172, 128, 150, 124, 144, 132], JY = [-18, 26, -30, -44, 20, 34, -12, 22, -8], JR = [-10, 8, -6, 4, 12, -8, 10, -4, 8];
  const reds = [...'forgotten'].map((ch, i) => mk(ch, `font-size:${SZ[i]}px;font-weight:700;color:#ff1d1d`));
  const all = [no, more, ...reds, ag];
  all.forEach(e => { e.hw = e.offsetWidth / 2; e.hh = e.offsetHeight / 2; });
  const U = [.065, .2, ...reds.map((_, i) => .33 + i * .0475), .885];
  const h = hand(cam, 78);
  const X = u => 150 + 1620 * u, Y = u => 650 - 410 * Math.sin(Math.PI * u), ROT = u => Math.atan2(-410 * Math.PI * Math.cos(Math.PI * u), 1620) * 180 / Math.PI;
  return t => {
    const sp = E.in2(prog(t, 16.55, 17.06)), wi = 1 - E.out5(prog(t, 15.17, 15.42));
    cam.style.transform = `scale(${(1 + .22 * wi + .2 * sp).toFixed(4)})`;
    cam.style.filter = wi > .02 ? `blur(${(16 * wi).toFixed(1)}px)` : 'none';
    for (const k of wins) k.w.style.transform = `translate(${k.x}px,${(k.y + 14 * Math.sin(t * .9 + k.i)).toFixed(1)}px) rotate(${(k.r + 3 * Math.sin(t * .7 + k.i * 2)).toFixed(2)}deg)`;
    all.forEach((e, n) => {
      const red = n >= 2 && n < 11, i = n - 2, u = .5 + (U[n] - .5) * (1 + .5 * sp);
      let x = X(u), y = Y(u) - 70 * sp * ((n * 37) % 5 / 4), r = ROT(u), s = 1, blur = n === 0 ? 7 : n === 11 ? 3.5 : n === 1 ? 1.2 : 0;
      if (red) { y += JY[i] + 30 * Math.sin(t * 4.4 + i * 1.15); r = r * .55 + JR[i] + 9 * Math.sin(t * 3.4 + i * 2.3); s = 1 + .1 * Math.sin(t * 5.2 + i * 1.7); }
      e.style.transform = `translate(${(x - e.hw).toFixed(1)}px,${(y - e.hh).toFixed(1)}px) rotate(${r.toFixed(2)}deg) scale(${s.toFixed(4)})`;
      e.style.filter = blur + 18 * sp > .3 ? `blur(${(blur + 18 * sp).toFixed(1)}px)` : 'none';
      e.style.opacity = 1 - .55 * sp;
    });
    const click = E.bump(prog(t, 16.06, 16.3)), u4 = U[6];
    putHand(h, X(u4) + 8 + 6 * Math.sin(t * 2.2), Y(u4) + JY[4] - 6 + 9 * Math.sin(t * 8.5) * (1 - click), { r: -22, s: 1 - .16 * click, o: 1 - sp, blur: 12 * sp });
  };
}, .03);

// ============================================================================================
// I  (17.06 - 18.75)  "One click, And done (:"  The hand draws the line under it.
// ============================================================================================
scene('I', 17.05, 18.70, root => {
  root.style.background = '#e2e2e4';
  const grp = el('fill', root, '', 'transform-origin:50% 50%');
  const line = el('abs', grp, '', 'left:0;top:0;width:1920px;height:1080px;display:flex;align-items:center;justify-content:center;gap:20px;white-space:nowrap;font-size:80px;font-weight:500;color:#0c0c0e;letter-spacing:-.03em;');
  const ws = [['One', 17.08, 0], ['click,', 17.17, 0], ['And', 17.92, 1], ['done', 17.97, 1], ['(:', 18.03, 1]].map(([s, at, blue]) => ({ at, el: el('', line, s, blue ? 'color:#1f4fd8;font-weight:700' : '') }));
  const a = ws[0].el, b = ws[1].el, ux = a.offsetLeft - 6, uw = b.offsetLeft + b.offsetWidth - ux + 10, uy = a.offsetTop + a.offsetHeight - 6;
  const under = el('abs', grp, `<svg viewBox="0 0 400 44" preserveAspectRatio="none" style="width:100%;height:100%;overflow:visible"><path d="M2 12C100 2 250 4 340 20c22 4 40 10 56 20C340 26 200 14 4 24Z" fill="#1f4fd8"/></svg>`, `left:${ux}px;top:${uy}px;width:${uw}px;height:40px`);
  const h = hand(grp, 84);
  return t => {
    const ex = E.in2(prog(t, 18.56, 18.75));
    const sc = 1 + .5 * E.out5(prog(t, 17.92, 18.16)) + .28 * prog(t, 18.0, 18.7);
    grp.style.transform = `translate(${(-60 * ex).toFixed(1)}px,${(46 * ex).toFixed(1)}px) scale(${sc.toFixed(4)})`;
    grp.style.filter = ex > .02 ? `blur(${(34 * ex).toFixed(1)}px)` : 'none';
    grp.style.opacity = 1 - .5 * ex;
    for (const w of ws) {
      const p = E.out3(prog(t, w.at, w.at + .26));
      w.el.style.opacity = p; w.el.style.transform = `translateX(${((1 - p) * 50).toFixed(1)}px)`;
      w.el.style.filter = p < 1 ? `blur(${((1 - p) * 12).toFixed(1)}px)` : 'none';
    }
    const up = E.io2(prog(t, 17.2, 17.5));
    under.style.clipPath = `inset(-10px ${((1 - up) * 100).toFixed(2)}% -10px 0)`;
    const inn = E.out3(prog(t, 17.08, 17.22)), rest = E.io2(prog(t, 17.5, 17.9)), off = E.in2(prog(t, 17.93, 18.12));
    putHand(h, lerp(ux - 190, ux + 8, inn) + uw * up * .97 + 60 * rest + 900 * off, uy + 20 + 150 * (1 - inn) + 24 * up + 26 * rest + 330 * off,
      { r: -14, o: t < 17.08 || off >= 1 ? 0 : 1, blur: 10 * (1 - inn) + 16 * off });
  };
}, .08);
