// ============================================================================================
// A + B  (0.00 - 3.35)  The hook. Tiny words on the hills, then the punch zoom and the press.
// ============================================================================================
scene('AB', 0, 3.349, root => {
  const bg = el('fill cover', root, '', `background-image:url(${HILLS});transform-origin:50% 62%;`);
  const world = el('abs', root, '', 'width:0;height:0;');                       // its origin is the chip's centre
  const line = el('abs', world, '', 'display:flex;align-items:center;gap:60px;white-space:nowrap;font-size:250px;font-weight:500;color:#fff;letter-spacing:-.025em;text-shadow:0 4px 30px rgba(0,50,70,.22);');
  const ws = [["We’ve", .12], ['all', .36], ['missed', .62], ['this', 1.08]].map(([txt, at]) => ({ at, el: el('', line, txt, 'will-change:transform') }));
  const chip = el('', line, '<span>Allow?</span>', 'position:relative;width:920px;height:310px;border-radius:48px;display:flex;align-items:center;justify-content:center;' +
    'background:linear-gradient(#4683f5,#2459d6);color:#fff;font-weight:700;font-size:176px;letter-spacing:-.03em;text-shadow:0 4px 0 rgba(0,0,0,.16);');
  const shine = el('fill', chip, '', 'border-radius:48px;background:#9cc4ff;opacity:0;');
  chip.querySelector('span').style.cssText = 'position:relative;z-index:1';
  const before = el('', line, 'before', '');
  line.style.left = -(chip.offsetLeft + chip.offsetWidth / 2) + 'px';
  line.style.top = -line.offsetHeight / 2 + 'px';
  const cap = el('cap', root, '[ asked 5 s ago ]', 'color:rgba(10,40,30,.55)');
  const small = hand(root, 58), big = hand(root, 235);

  return t => {
    const z = E.io3(prog(t, 1.76, 2.06));
    const k = .15 * Math.pow(1 / .15, z) * (1 + .07 * prog(t, 2.06, 3.4));
    const cx = lerp(905, 960, z) + 400 * E.bump(E.out2(prog(t, 1.76, 2.22)));
    const cy = lerp(562, 486, z);
    world.style.transform = `translate(${cx.toFixed(2)}px,${cy.toFixed(2)}px) scale(${k.toFixed(5)})`;
    const whip = E.bump(prog(t, 1.76, 2.09));
    line.style.filter = whip > .02 ? `blur(${(24 * whip / k).toFixed(1)}px)` : 'none';
    const bs = lerp(1.07, 1.0, prog(t, 0, 1.76)) * lerp(1, 2.3, z) * (1 + .03 * prog(t, 2.06, 3.4));
    bg.style.transform = `scale(${bs.toFixed(4)})`;
    bg.style.filter = z > .01 ? `blur(${(15 * z).toFixed(1)}px)` : 'none';

    for (const w of ws) {
      const p = E.out3(prog(t, w.at, w.at + .36));
      w.el.style.opacity = p; w.el.style.transform = `translateX(${((1 - p) * 320).toFixed(1)}px)`;
      w.el.style.filter = p < 1 && whip < .02 ? `blur(${((1 - p) * 46).toFixed(1)}px)` : 'none';
    }
    const cp = E.out3(prog(t, 1.62, 1.8)), pr = E.out3(prog(t, 2.96, 3.05));
    chip.style.opacity = cp;
    chip.style.transform = `translateY(${(13 * pr).toFixed(1)}px) scale(${lerp(.72, 1, cp).toFixed(4)})`;
    chip.style.boxShadow = `0 ${(17 - 13 * pr).toFixed(1)}px 0 #1a3d9c, 0 ${(34 - 20 * pr).toFixed(0)}px 60px rgba(0,30,90,.35), inset 0 4px 0 rgba(255,255,255,.28)`;
    shine.style.opacity = .5 * pr;
    before.style.opacity = prog(t, 1.8, 1.94);
    cap.style.opacity = prog(t, 2.2, 2.4);

    const sp = E.io3(prog(t, .95, 1.72));
    putHand(small, lerp(300, cx + 4, sp), lerp(884, cy + 30, sp) + 5 * Math.sin(t * 3.1) * (1 - sp), { o: 1 - prog(t, 1.76, 1.82), r: -14 });
    const bp = E.out3(prog(t, 2.02, 2.72));
    putHand(big, lerp(560, 1195, bp), lerp(1290, 610, bp) + 10 * pr, { o: t > 2.0 ? 1 : 0, s: 1 - .06 * pr, blur: 14 * (1 - prog(t, 2.02, 2.3)) });
  };
});

// ============================================================================================
// C  (3.35 - 7.60)  The build. The same question in 19 skins; the chip never moves.
// ============================================================================================
scene('C', 3.349, 7.601, root => {
  const CUTS = [3.349, 3.852, 4.173, 4.496, 4.722, 4.947, 5.157, 5.367, 5.561, 5.756, 5.950, 6.163, 6.355, 6.565, 6.693, 6.890, 7.08, 7.247, 7.440, 7.601];
  const CW = 1000, CHH = 350, CX = (W - CW) / 2, CY = (H - CHH) / 2;
  const box = (p, css) => el('abs', p, '', css);
  const txt = (p, y, html, css) => el('abs', p, html, `left:${CX + 10}px;top:${y}px;width:${CW - 20}px;white-space:nowrap;${css}`);
  const tiles = (c, r = 46) => p => { for (let row = -1; row < 4; row++) for (let col = -1; col < 5; col++) box(p, `left:${col * 590 + (row % 2 ? -205 : 90)}px;top:${CY + (row - 1) * 380 + 10}px;width:560px;height:${CHH - 20}px;border-radius:${r}px;background:${c}`); };
  const stars = (n, seed) => p => { const R = rng(seed); for (let i = 0; i < n; i++) { const s = 2 + R() * 5; box(p, `left:${R() * W}px;top:${R() * H}px;width:${s}px;height:${s}px;border-radius:50%;background:#fff;opacity:${.25 + R() * .7}`); } };
  const both = (...f) => p => f.forEach(g => g(p));
  const twin = c => p => { box(p, `left:${CX}px;top:${CY - 390}px;width:${CW}px;height:${CHH}px;border-radius:54px;background:${c}`); box(p, `left:${CX}px;top:${CY + 390}px;width:${CW}px;height:${CHH}px;border-radius:54px;background:${c}`); };

  const SK = [
    { label: 'Allow edit?', cap: 'asked 5 s ago', bg: '#e6e6e8', chip: 'background:#000;color:#fff', deco: p => {
        box(p, 'left:300px;top:-90px;width:1320px;height:900px;border-radius:76px;background:#c5c5c9');
        txt(p, 70, '<span>Edit file</span><span>app.ts</span>', 'display:flex;justify-content:space-between;font-size:62px;color:#55555a');
        box(p, `left:${CX + 10}px;top:178px;width:${CW - 20}px;height:3px;background:#a9a9ae`);
        txt(p, 222, '<span>Changes</span><span style="font-weight:600">+12 &nbsp;−3</span>', 'display:flex;justify-content:space-between;font-size:72px;color:#2e2e33');
      } },
    { label: 'npm install?', cap: 'asked 20 s ago', capc: 'rgba(255,255,255,.42)', bg: '#222226', radius: 175,
      chip: 'background:linear-gradient(#7c77c8,#5a55a6);color:#eceaff;font-weight:500;box-shadow:inset 0 3px 0 rgba(255,255,255,.2)', deco: p => {
        box(p, 'left:250px;top:-40px;width:1420px;height:1200px;background:linear-gradient(#4b4689,#37336c)');
        txt(p, 90, '– Bash command', 'font-size:66px;color:#8d89cf'); txt(p, 196, '– npm install', 'font-size:66px;color:#8d89cf');
        txt(p, 790, '✧&nbsp; Claude wants to run this', 'font-size:52px;color:#dcdaf6'); txt(p, 892, '◌&nbsp; Waiting for your answer', 'font-size:52px;color:#dcdaf6');
      } },
    { label: 'Proceed?', cap: 'asked 1 min ago', capc: 'rgba(255,255,255,.42)', bg: '#060606', chip: 'background:linear-gradient(#e0e0e0,#b7b7b7);color:#262626;font-weight:500', deco: stars(90, 3) },
    { label: 'Allow Bash?', cap: 'asked 2 min ago', capc: 'rgba(255,255,255,.42)', bg: '#2e2e2e', chip: 'background:#dedede;color:#d4623a;font-weight:500', deco: p => {
        box(p, 'left:330px;top:-40px;width:1260px;height:1200px;background:#474747');
        txt(p, 66, 'Bash', 'font-size:84px;font-weight:700;color:#fff'); txt(p, 186, 'rm -rf node_modules', 'font-size:52px;color:#c2c2c2');
        box(p, `left:330px;top:770px;width:1260px;height:3px;background:#5c5c5c`);
        txt(p, 800, 'Do you want to proceed?', 'font-size:56px;font-weight:600;color:#f0f0f0'); txt(p, 896, '✓&nbsp; 1. Yes &nbsp;&nbsp; 2. No', 'font-size:52px;color:#bdbdbd');
      } },
    { label: 'Trust <b>folder?</b>', cap: 'asked 3 min ago', capc: 'rgba(255,255,255,.5)', bg: 'linear-gradient(#27345f 0%,#5d5c86 36%,#d69c6e 54%,#c4733c 100%)', chip: 'background:#16244f;color:#fff;font-weight:400', deco: p => {
        box(p, 'left:-300px;top:560px;width:1700px;height:900px;border-radius:50%;background:linear-gradient(#e09a58,#b9632c)');
        box(p, 'left:700px;top:640px;width:1800px;height:900px;border-radius:50%;background:linear-gradient(#cf8344,#9c4f22)');
        twin('rgba(22,30,70,.82)')(p);
      } },
    { label: 'Approve <b>plan?</b>', cap: 'asked 5 min ago', bg: 'linear-gradient(#77777a,#aaa59a 40%,#d9cdb4 52%,#b3a58c 60%,#6f6c66)', chip: 'background:#f3efe3;color:#1c1c1c;font-weight:400', deco: twin('#4d4944') },
    { label: 'Continue?', cap: 'asked 7 min ago', capc: 'rgba(255,255,255,.42)', bg: 'radial-gradient(circle at 50% 62%,#20349a,#070b2c 78%)', chip: 'background:#eef2ff;color:#1b2c7a;font-weight:500', deco: stars(70, 9) },
    { label: 'Overwrite?', cap: 'asked 9 min ago', bg: '#8f959a', chip: 'background:#14171b;color:#3ccccc;font-weight:500', deco: p => box(p, 'left:0;top:240px;width:1920px;height:600px;background:#fdfdfd') },
    { label: 'Allow fetch?', cap: 'asked 12 min ago', bg: 'linear-gradient(120deg,#e6c3ee,#f7dad3 58%,#f9c7b4)', chip: 'background:#1273d8;color:#fff;font-weight:500',
      deco: p => el('fill', p, '<svg viewBox="0 0 1920 1080" style="width:100%;height:100%"><path d="M250 1130C380 330 590 30 770 300S930 1090 1180 660 1500 60 1720 330" fill="none" stroke="#a56cc0" stroke-width="50" stroke-linecap="round"/></svg>') },
    { label: 'Run tests?', cap: 'asked 15 min ago', capc: 'rgba(255,255,255,.42)', bg: '#343f1e', chip: 'background:#9ad52c;color:#fff;font-weight:500', deco: tiles('#44542a') },
    { label: 'Delete files?', cap: 'asked 18 min ago', capc: 'rgba(255,255,255,.42)', bg: '#13151c', chip: 'background:transparent;border:3px solid #2f8f86;color:#fff;font-weight:500', deco: p => {
        box(p, 'left:200px;top:120px;width:1500px;height:520px;border-radius:50%;background:#1f2533;filter:blur(60px)');
        const c = box(p, `left:${CX}px;top:${CY}px;width:${CW}px;height:${CHH}px;border-radius:54px;overflow:hidden`);
        box(c, 'left:210px;top:-115px;width:580px;height:580px;border-radius:50%;background:#25d6b2');
      } },
    { label: 'Push to main?', cap: 'asked 21 min ago', bg: '#a3abd3', chip: 'background:#4a446f;color:#fff;font-weight:500', deco: p => {
        box(p, `left:${CX - 130}px;top:${CY - 130}px;width:${CW + 260}px;height:${CHH + 260}px;border-radius:170px;background:#959dc8;box-shadow:0 0 0 5px #5c5e96`);
        box(p, `left:${CX - 62}px;top:${CY - 62}px;width:${CW + 124}px;height:${CHH + 124}px;border-radius:112px;background:#8a91c0;box-shadow:0 0 0 5px #5c5e96`);
      } },
    { label: 'Yes or no?', cap: 'asked 25 min ago', capc: 'rgba(255,255,255,.42)', bg: 'radial-gradient(circle at 50% 30%,#1c5ae0,#0a2c94 82%)', chip: 'background:#e7eaf0;color:#17377f;font-weight:500', deco: stars(60, 21) },
    { label: 'Hello?', cap: 'asked 29 min ago', bg: '#e2e2e2', chip: 'background:#000;color:#fff' },
    { label: 'Still there?', cap: 'asked 33 min ago', bg: '#fbf1e8', chip: 'background:#d97757;color:#fff;font-weight:500' },
    { label: 'Anyone?', cap: 'asked 38 min ago', capc: 'rgba(255,255,255,.42)', bg: '#5d4e3e', chip: 'background:#dccdb0;color:#5c4b37;font-weight:500', deco: tiles('#6e5d49') },
    { label: 'Please?', cap: 'asked 42 min ago', capc: 'rgba(255,255,255,.5)', bg: '#8b2020', chip: 'background:#f3efe3;color:#1a1a1a', deco: tiles('#a02a2a') },
    { label: 'Zzz…', cap: 'asked 47 min ago', bg: '#f8c41c', chip: 'background:#27388f;color:#fff', deco: p => {
        const a = bloub(p, 'blue', 470, { eyes: 'sleepy', tilt: -4 }); a.style.left = '90px'; a.style.top = '330px'; a.style.transform = 'rotate(-9deg)';
        const b = bloub(p, 'purple', 470, { eyes: 'sleepy', tilt: 5 }); b.style.left = '1360px'; b.style.top = '350px'; b.style.transform = 'rotate(8deg)';
      } },
    { label: '47 min later', cap: 'you were watching videos', capc: 'rgba(255,255,255,.55)', bg: '#a98479', chip: 'background:#de7b56;color:#fff;font-weight:500', deco: p => {
        box(p, 'left:215px;top:0;width:70px;height:1080px;background:#f7eadf'); box(p, 'left:1635px;top:0;width:70px;height:1080px;background:#f7eadf');
        box(p, 'left:330px;top:-40px;width:1260px;height:1200px;background:#99756a');
        txt(p, 66, 'Waiting for you', 'font-size:84px;font-weight:700;color:#fff'); txt(p, 186, 'Claude asked a question', 'font-size:52px;color:#ecdcd5');
        box(p, `left:330px;top:770px;width:1260px;height:3px;background:#b08d83`);
        txt(p, 800, 'Asked at 14:02', 'font-size:56px;font-weight:600;color:#fff'); txt(p, 896, '✓&nbsp; It is 14:49 now', 'font-size:52px;color:#ecdcd5');
      } },
  ];

  const skins = SK.map(o => {
    const s = el('fill', root, '', `background:${o.bg};display:none;overflow:hidden`);
    const cam = el('fill', s, '', 'transform-origin:50% 50%');
    if (o.deco) o.deco(cam);
    const chip = el('chip', cam, `<span>${o.label}</span>`, `left:${CX}px;top:${CY}px;width:${CW}px;height:${CHH}px;border-radius:${o.radius || 54}px;font-size:150px;${o.chip}`);
    s.style.display = 'block';
    const w = chip.firstChild.offsetWidth;                                   // fit the words to the chip
    chip.style.fontSize = Math.min(150, 150 * CW * .8 / w).toFixed(1) + 'px';
    s.style.display = 'none';
    el('cap', s, `[ ${o.cap} ]`, `color:${o.capc || 'rgba(0,0,0,.48)'}`);
    return { s, cam };
  });
  const big = hand(root, 235);
  let shown = -1;

  return t => {
    let i = 0; while (i < SK.length - 1 && t >= CUTS[i + 1]) i++;
    if (i !== shown) { skins.forEach((k, j) => k.s.style.display = j === i ? 'block' : 'none'); shown = i; }
    const lt = t - CUTS[i], len = CUTS[i + 1] - CUTS[i];
    const sc = i === 0 ? 1 + .075 * (lt / len) : 1.045 - .045 * E.out3(prog(lt, 0, .13)) + .012 * (lt / len);
    skins[i].cam.style.transform = `scale(${sc.toFixed(5)})`;
    // the hand is still pressing on the first cut, and leaves on the second
    const out = E.in2(prog(t, CUTS[1], CUTS[1] + .27));
    putHand(big, lerp(1195, 1790, out) + (i === 0 ? 14 * (lt / len) : 0), lerp(620, -190, out) + (i === 0 ? 6 * (lt / len) : 0), { o: t < CUTS[1] + .27 ? 1 : 0, s: i === 0 ? .94 + .03 * (lt / len) : 1, blur: 12 * out });
  };
});
