// Plays the film in the page. ?t=12.5 opens paused on a moment; ?capture hides the controls (for rendering).
(async () => {
  const q = new URLSearchParams(location.search);
  if (q.has('capture')) document.body.classList.add('capture');
  await Promise.all(['400', '500', '600', '700', 'italic 400'].map(w => document.fonts.load(`${w} 100px Inter`)));
  buildAll();

  const fit = document.getElementById('fit'), play = document.getElementById('play'), scrub = document.getElementById('scrub'), clock = document.getElementById('clock');
  function resize() {
    const k = Math.min(fit.clientWidth / W, fit.clientHeight / H);
    stage.style.transform = `translate(${(fit.clientWidth - W * k) / 2}px,${(fit.clientHeight - H * k) / 2}px) scale(${k})`;
  }
  addEventListener('resize', resize); resize();

  let t = +q.get('t') || 0, playing = !q.has('t') && !q.has('capture'), last = null;
  function show() { render(t); scrub.value = t; clock.textContent = t.toFixed(2); play.textContent = playing ? 'Pause' : 'Play'; }
  function frame(now) {
    if (playing) { if (last != null) t += (now - last) / 1000; if (t >= DUR) t = 0; show(); }
    last = now; requestAnimationFrame(frame);
  }
  play.onclick = () => { playing = !playing; show(); };
  scrub.oninput = () => { t = +scrub.value; playing = false; show(); };
  addEventListener('keydown', e => {
    if (e.code === 'Space') { e.preventDefault(); playing = !playing; }
    else if (e.code === 'ArrowRight') { playing = false; t = Math.min(DUR - .001, t + 1 / 60); }
    else if (e.code === 'ArrowLeft') { playing = false; t = Math.max(0, t - 1 / 60); }
    else if (e.code === 'KeyF') { document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen(); }
    show();
  });
  window.__seek = x => { t = x; playing = false; show(); };
  show(); requestAnimationFrame(frame);
  window.__ready = true;
})();
