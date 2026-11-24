const center = 'left:0;top:0;width:1920px;height:1080px;display:flex;align-items:center;justify-content:center;white-space:nowrap;';
const ISL = 'assets/island/';

// ============================================================================================
// J  (18.75 - 19.33)  "Agents": eight Bloubs spin in a ring, then fall into the word.
// ============================================================================================
scene('J', 18.70, 19.27, root => {
  root.style.background = '#e2e2e4';
  const word = el('abs', root, 'Agents', center + 'font-size:132px;font-weight:700;color:#0b0b0d;letter-spacing:-.045em;transform-origin:50% 50%');
  const orbs = [['blue', 'work'], ['red', 'need'], ['purple', 'work'], ['pink', 'done'], ['green', 'work'], ['orange', 'done'], ['teal', 'work'], ['yellow', 'need']]
    .map(([c, b], i) => bloub(root, c, 180, { badge: b, look: [[3, 0], [-3, 2], [0, -2], [4, 2]][i % 4], tilt: [6, -6, 10, 0][i % 4] }));
  return t => {
    const inn = E.out5(prog(t, 18.75, 18.95)), out = E.in3(prog(t, 19.15, 19.37));
    const Rr = 368 * lerp(.3, 1, inn) * (1 - out), th = -100 + 170 * E.out2(prog(t, 18.75, 19.33));
    put(word, { s: lerp(.78, 1, inn) });
    orbs.forEach((o, i) => {
      const a = (th + i * 45) * Math.PI / 180;
      put(o, { x: 960 + Rr * Math.cos(a) - 90, y: 540 + Rr * .84 * Math.sin(a) - 90, s: lerp(.5, 1, inn) * (1 - .55 * out), blur: 12 * (1 - prog(t, 18.75, 18.88)) + 16 * out, o: 1 - .3 * out });
    });
  };
}, .06);

// ============================================================================================
// K  (19.33 - 20.08)  "Answers": the allow card springs in, the word drops in letter by letter.
// ============================================================================================
scene('K', 19.27, 20.02, root => {
  root.style.background = '#e2e2e4';
  const grp = el('fill', root, '', 'transform-origin:50% 55%');
  const word = el('abs', grp, '', 'left:0;top:250px;width:1920px;height:180px;display:flex;align-items:center;justify-content:center;font-size:150px;font-weight:700;color:#0b0b0d;letter-spacing:-.045em');
  const ls = [...'Answers'].map(ch => el('', word, ch, 'display:inline-block'));
  const card = el('abs', grp, '', 'left:400px;top:480px;width:1120px;height:290px;border-radius:66px;background:#cdcdd2;display:flex;align-items:center;padding:0 46px 0 54px;gap:38px;transform-origin:50% 50%');
  const b = bloub(card, 'purple', 180, { badge: 'need' }); b.style.position = 'relative'; b.style.flex = 'none';
  el('', card, '<div style="font-size:68px;font-weight:700;color:#101012;letter-spacing:-.035em;line-height:1.1">notes-cli</div><div style="font-size:39px;font-weight:500;color:#6a6a72;letter-spacing:-.015em;margin-top:6px">wants to edit app.ts</div>', 'flex:1;white-space:nowrap');
  el('', card, 'Allow', 'flex:none;width:330px;height:150px;border-radius:40px;background:#1f4fd8;color:#fff;font-size:66px;font-weight:600;letter-spacing:-.03em;display:flex;align-items:center;justify-content:center');
  return t => {
    const ex = E.in2(prog(t, 20.03, 20.09));
    grp.style.transform = `scale(${(1 + .05 * prog(t, 19.33, 20.08) + .12 * ex).toFixed(4)})`;
    grp.style.filter = ex > .02 ? `blur(${(22 * ex).toFixed(1)}px)` : 'none';
    grp.style.opacity = 1 - .6 * ex;
    put(card, { s: lerp(.42, 1, E.back(prog(t, 19.33, 19.62), 1.5)), o: prog(t, 19.33, 19.4), blur: 16 * (1 - prog(t, 19.33, 19.5)) });
    ls.forEach((l, i) => {
      const a = 19.37 + i * .048, p = E.back(prog(t, a, a + .25), 2.2);
      l.style.opacity = prog(t, a, a + .05); l.style.transform = `translateY(${(-(1 - p) * 140).toFixed(1)}px)`;
    });
  };
}, .06);

// ============================================================================================
// L  (20.08 - 20.67)  "Tabs": real island screens fly in around the word; one flash frame.
// ============================================================================================
scene('L', 20.02, 20.684, root => {
  el('fill', root, '', 'background:#ececee');
  const sky = el('fill', root, '', 'background:linear-gradient(135deg,#eef8ff,#c4e8ff 55%,#a4e4f4)');
  const grp = el('fill', root, '', 'transform-origin:50% 50%');
  const T = [['tab-usage', 400, 610, 290, -5], ['tab-media', 345, 990, 235, 3], ['team', 280, 1335, 330, 6], ['tab-overview', 420, 480, 580, 2],
             ['tab-timer', 370, 1440, 640, -3], ['tab-servers', 385, 640, 850, 4], ['tab-calendar', 345, 1020, 870, -2], ['focus-0', 285, 1370, 895, -6]];
  const tiles = T.map(([n, w, x, y, r]) => {
    const im = document.createElement('img'); im.src = ISL + n + '.png';
    im.style.cssText = `position:absolute;left:0;top:0;width:${w}px;transform-origin:50% 50%;filter:drop-shadow(0 14px 22px rgba(0,0,0,.22))`;
    grp.appendChild(im);
    const ar = n.startsWith('tab') ? 404 / 724 : n === 'team' ? 352 / 400 : 440 / 480;
    return { im, w, h: w * ar, x, y, r };
  });
  const word = el('abs', grp, 'Tabs', center + 'font-size:178px;font-weight:700;color:#0b0b0d;letter-spacing:-.045em;transform-origin:50% 50%');
  const flash = el('fill', root, '', 'background:linear-gradient(115deg,#2fe05f,#86f26a 42%,#17c8f6)');
  return t => {
    grp.style.transform = `translateY(-22px) scale(${(1.1 + .04 * prog(t, 20.08, 20.67)).toFixed(4)})`;
    put(word, { s: lerp(.6, 1, E.out5(prog(t, 20.08, 20.3))), blur: 14 * (1 - prog(t, 20.08, 20.22)) });
    tiles.forEach((k, i) => {
      const a = 20.08 + i * .011, p = E.out5(prog(t, a, a + .3));
      k.im.style.transform = `translate(${(lerp(960, k.x, p) - k.w / 2).toFixed(1)}px,${(lerp(540, k.y, p) - k.h / 2).toFixed(1)}px) rotate(${lerp(k.r * 4, k.r, p).toFixed(2)}deg) scale(${lerp(.22, 1, p).toFixed(4)})`;
      const bl = 24 * (1 - prog(t, a, a + .2));
      k.im.style.filter = (bl > .3 ? `blur(${bl.toFixed(1)}px) ` : '') + 'drop-shadow(0 14px 22px rgba(0,0,0,.22))';
    });
    sky.style.opacity = prog(t, 20.38, 20.55);
    flash.style.display = t >= 20.626 ? 'block' : 'none';
  };
}, .07);

// ============================================================================================
// M + N  (20.67 - 23.08)  "Limits" close on the wall, then "Pure Focus" as the camera pulls back.
// ============================================================================================
scene('MN', 20.684, 23.02, root => {
  root.style.background = '#ededf0';
  const world = el('abs', root, '', 'width:0;height:0;');
  const NAMES = ['tab-usage', 'tab-clocks', 'team', 'tab-media', 'tab-servers', 'focus-0', 'tab-overview', 'tab-timer', 'tab-projects', 'list', 'tab-focus', 'tab-system',
                 'tab-calendar', 'focus-1-plan', 'tab-tasks', 'edit', 'tab-network', 'focus-2-files'];
  const outer = []; let n = 4;
  for (let r = 0; r < 5; r++) for (let c = -1; c < 6; c++) {
    const x = (c - 2) * 830 + (r % 2 ? 415 : 0), y = (r - 2) * 480;
    if (r === 2 && c === 2) continue;
    const name = NAMES[n++ % NAMES.length], hgt = 424;
    const wdt = name.startsWith('tab') || name === 'edit' ? 760 : name.startsWith('focus') ? 462 : 482;
    const im = document.createElement('img'); im.src = ISL + name + '.png';
    im.style.cssText = `position:absolute;left:${x - wdt / 2}px;top:${y - hgt / 2}px;width:${wdt}px;height:${hgt}px;`;
    world.appendChild(im); outer.push(im);
  }
  const tile = el('abs', world, '', 'left:-450px;top:-250px;width:900px;height:500px;border-radius:46px;background:#000;transform-origin:50% 50%;overflow:hidden');
  const a = el('fill', tile, 'Limits', 'display:flex;align-items:center;justify-content:center;font-size:236px;font-weight:700;color:#fff;letter-spacing:-.045em');
  const b = el('fill', tile, '<div style="position:relative"><i style="position:absolute;left:6px;top:-58px;font-size:74px;font-weight:400;color:#b4b4bd;letter-spacing:-.02em">Pure</i>Focus</div>',
    'display:flex;align-items:center;justify-content:center;padding-top:50px;font-size:212px;font-weight:700;color:#fff;letter-spacing:-.045em');
  const white = el('fill', root, '', 'background:#e3e3e5');
  return t => {
    const back = E.io3(prog(t, 21.52, 21.76)), dr = prog(t, 21.52, 23.08);
    const k = lerp(1.07 - .07 * E.out3(prog(t, 20.67, 20.85)) + .05 * prog(t, 20.8, 21.5), .62, back) - .035 * dr * back;
    world.style.transform = `translate(${(960 - 46 * dr).toFixed(1)}px,${(540 - 26 * dr).toFixed(1)}px) scale(${k.toFixed(5)})`;
    const dof = 9 * (1 - back), wi = 16 * (1 - prog(t, 20.67, 20.82));
    for (const im of outer) im.style.filter = dof + wi > .3 ? `blur(${(dof + wi).toFixed(1)}px)` : 'none';
    tile.style.transform = `rotate(${(-9 * (1 - E.out3(prog(t, 20.67, 21.08)))).toFixed(2)}deg)`;
    tile.style.filter = wi > .3 ? `blur(${wi.toFixed(1)}px)` : 'none';
    const x = prog(t, 21.48, 21.64);
    a.style.opacity = 1 - x; b.style.opacity = x;
    white.style.opacity = prog(t, 22.62, 23.0);
  };
});

// ============================================================================================
// O + P  (23.0 - 27.3)  The island's own line opens into the pill, Bloub looks; then the end card.
// ============================================================================================
scene('OP', 23.02, DUR + 1, root => {
  const grp = el('fill', root, '', 'transform-origin:50% 44%');
  const pill = el('abs', grp, '', 'left:0;top:0;background:#0a0a0c;display:flex;align-items:center;justify-content:center;gap:42px;overflow:hidden');
  const eyes = [0, 1].map(() => el('', pill, '', 'width:33px;height:70px;border-radius:17px;background:#fff;flex:none;transform-origin:50% 50%'));
  const line = el('abs', grp, '', 'left:0;top:566px;width:1920px;display:flex;justify-content:center;gap:16px;white-space:nowrap;font-size:58px;font-weight:500;color:#101012;letter-spacing:-.03em');
  const ws = [['All', 23.12], ['in', 23.5], ['one', 23.83], ['<span style="color:#1f4fd8;font-weight:600">glance.</span>', 24.25]].map(([s, at]) => ({ at, el: el('', line, s, '') }));

  const end = el('fill', root, '', 'transform-origin:50% 50%');
  const name = el('abs', end, 'Island', 'left:0;top:0;font-size:176px;font-weight:700;color:#fff;letter-spacing:-.045em;line-height:1;white-space:nowrap');
  const orb = bloub(end, 'ice', 156, { look: [6, -3], tilt: 12 });
  const NW = name.offsetWidth, NH = name.offsetHeight, total = 156 + 34 + NW, left = (W - total) / 2, cy = 500;
  name.style.left = left + 190 + 'px'; name.style.top = cy - NH / 2 - 4 + 'px';
  const tag = el('abs', end, 'All in one <span style="color:#5b93ff;font-weight:600">glance.</span>', `left:0;top:${cy + 112}px;width:1920px;text-align:center;font-size:46px;font-weight:500;color:#fff;letter-spacing:-.025em`);
  const cta = el('abs', end, '<div style="color:#8e8e93">Free and open source &nbsp;·&nbsp; Windows 10 and 11</div><div style="color:#d1d1d6;margin-top:10px">github.com/UsoguiH/dynamic-island-windows</div>',
    `left:0;top:${cy + 262}px;width:1920px;text-align:center;font-size:33px;font-weight:500;letter-spacing:-.01em`);

  return t => {
    const p = E.io3(prog(t, 25.38, 25.78));
    root.style.background = mix('#e3e3e5', '#000000', p);
    grp.style.transform = `scale(${lerp(1, 1.07, prog(t, 23.08, 25.3)).toFixed(4)})`;
    // the folded island (a thin line) opens into the pill
    const lw = 172 * E.out3(prog(t, 23.08, 23.32)), m = E.io3(prog(t, 23.38, 23.8)), mb = E.back(prog(t, 23.38, 23.86), 1.6);
    const w = lerp(lw, 540, mb), h = lerp(5, 156, m);
    pill.style.width = w.toFixed(1) + 'px'; pill.style.height = h.toFixed(1) + 'px'; pill.style.borderRadius = (h / 2).toFixed(1) + 'px';
    pill.style.transform = `translate(${(960 - w / 2).toFixed(1)}px,${(410 - h / 2).toFixed(1)}px) scale(${lerp(1, .5, p).toFixed(4)})`;
    pill.style.opacity = t < 23.08 ? 0 : 1 - prog(t, 25.36, 25.52);
    const open = E.back(prog(t, 23.8, 24.08), 2.4), blink = Math.max(E.bump(prog(t, 24.66, 24.8)), 0);
    const gl = E.io2(prog(t, 24.2, 24.42)) * (1 - E.io2(prog(t, 24.95, 25.15)));           // he glances at the word
    eyes.forEach(e => e.style.transform = `translate(${(12 * gl).toFixed(1)}px,${(9 * gl).toFixed(1)}px) scaleY(${(open * (1 - .9 * blink)).toFixed(3)})`);
    for (const k of ws) {
      const q = E.out3(prog(t, k.at, k.at + .3));
      k.el.style.opacity = q * (1 - prog(t, 25.36, 25.5)); k.el.style.transform = `translateX(${((1 - q) * 56).toFixed(1)}px)`;
      k.el.style.filter = q < 1 ? `blur(${((1 - q) * 12).toFixed(1)}px)` : 'none';
    }
    // end card
    end.style.transform = `scale(${lerp(1, 1.03, prog(t, 25.6, DUR)).toFixed(4)})`;
    put(orb, { x: lerp(960, left + 78, p) - 78, y: lerp(410, cy, p) - 78, s: lerp(.5, 1, E.back(p, 1.4)), o: prog(t, 25.4, 25.54) });
    const wp = E.out3(prog(t, 25.5, 25.82));
    put(name, { x: (1 - wp) * 90, o: wp, blur: (1 - wp) * 22 });
    const tp = E.out3(prog(t, 25.6, 25.9));
    put(tag, { y: (1 - tp) * 24, o: tp });
    const cp = E.out3(prog(t, 26.1, 26.5));
    put(cta, { y: (1 - cp) * 16, o: cp });
  };
}, .1);
