// Builds an instrumented copy of the FROZEN prototype (h5/index.html) for headless
// screenshot verification. h5/index.html is NEVER modified: this only re-emits it with an
// epilogue appended INSIDE the prototype's own IIFE closure, plus a __FREEZE hook in
// animation() so a chosen frame stays on screen long enough to be captured.
//
// Why: there is no human at the keyboard in this session, so nothing would ever shoot,
// rewind, or settle. This page drives the prototype by itself, deterministically.
//
// Usage:
//   node make_probe.cjs            # max-power break, settle, then freeze  (default)
//   node make_probe.cjs rewind     # break, settle, then hold REWIND mid-history
//   node make_probe.cjs rack       # untouched rack, settled (geometry reference)
//   PROBE_SECONDS=4 node make_probe.cjs break
const fs = require('fs');
const path = require('path');

const DIR = __dirname;
const MODE = (process.argv[2] || 'break').trim();
const SETTLE_SECONDS = parseFloat(process.env.PROBE_SECONDS || '12');
if (!['break', 'rewind', 'rack'].includes(MODE)) {
  console.error(`FATAL: unknown mode "${MODE}" (expected break|rewind|rack)`);
  process.exit(1);
}

const src = fs.readFileSync(path.join(DIR, 'baseline.html'), 'utf8');

const openIdx = src.indexOf('<script>');
const closeIdx = src.lastIndexOf('</script>');
if (openIdx < 0 || closeIdx < 0 || closeIdx < openIdx) {
  console.error('FATAL: no script block');
  process.exit(1);
}
let body = src.slice(openIdx + '<script>'.length, closeIdx);
if (body.includes('</script>')) {
  console.error('FATAL: script body contains a nested </script>; slicing is unsafe');
  process.exit(1);
}

const MARKER = 'init();requestAnimationFrame(animation);';
if (!body.includes(MARKER)) {
  console.error('FATAL: could not find the init/animation call site. ' +
    'baseline.html is not the expected frozen prototype.');
  process.exit(1);
}

const STEP = 1 / 120;

// ---- epilogue, evaluated INSIDE the IIFE so it sees the real private state ----
const EPILOGUE = `init();
  const __STEP = ${STEP};
  globalThis.__R = {
    simulation, startRewind, endRewind, render, init, ballById,
    balls: () => balls,
    forceShotUsed: () => { shotUsed = true; if (typeof updateUI === 'function') updateUI(); },
    setPaused: v => { paused = v; }
  };
  globalThis.__PROBE = function () {
    const R = globalThis.__R;
    R.setPaused(true);                 // stop the rAF loop stepping physics, so every
                                       // number below is byte-for-byte reproducible
    const cue = R.ballById(0);
    let probeT = 0;
    const snap = () => {
      const bs = R.balls().map(b => ({ id: b.id, x: +b.x.toFixed(2), y: +b.y.toFixed(2),
        v: +Math.hypot(b.vx, b.vy).toFixed(1), pocketed: b.pocketed,
        finite: isFinite(b.x) && isFinite(b.y) }));
      return { t: +probeT.toFixed(3),
               pocketedObjectBalls: bs.filter(b => b.id !== 0 && b.pocketed).length,
               cueHist: cue.history.length, allFinite: bs.every(b => b.finite), balls: bs };
    };
    const advance = (seconds) => {
      const n = Math.round(seconds / __STEP);
      for (let i = 0; i < n; i++) { R.simulation(__STEP); probeT += __STEP; }
    };
    const log = [];
    const mark = (label) => { const s = snap(); s.label = label; log.push(s); };

    const MODE = ${JSON.stringify(MODE)};
    if (MODE === 'rack') {
      advance(1.0);
      mark('settled');
    } else {
      // Exactly what releaseShot() does at full power: v = unit * min(len,170) * 7.4
      const speed = 170 * 7.4;
      cue.vx = speed; cue.vy = 0;
      R.forceShotUsed();
      advance(0.02); mark('just_after_strike');
      advance(${SETTLE_SECONDS}); mark('settled');
      if (MODE === 'rewind') { R.startRewind(); advance(0.35); mark('rewinding'); }
    }

    globalThis.__FREEZE = true;        // hold this frame
    R.render();
    return { mode: MODE, stepSeconds: __STEP, steps: Math.round(probeT / __STEP),
             seconds: +probeT.toFixed(3), log, final: snap() };
  };
  globalThis.__PROBE_READY = true;`;

body = body.replace(MARKER, EPILOGUE);

// ---- patch animation() so it stops stepping/rendering once __FREEZE is set ----
const animRe = /if\(!paused\)\{[\s\S]*?\}\s*render\(\);requestAnimationFrame\(animation\);/;
const animMatch = body.match(animRe);
if (!animMatch) {
  console.error('FATAL: animation() shape changed; refusing to emit an unverified probe');
  process.exit(1);
}
const animBody = animMatch[0];
const patched =
  'if (!globalThis.__FREEZE) { ' + animBody.replace(
    /render\(\);requestAnimationFrame\(animation\);$/,
    'render(); requestAnimationFrame(animation);') + ' } else { requestAnimationFrame(animation); }';
body = body.replace(animRe, patched);
if (!body.includes('__FREEZE')) { console.error('FATAL: animation() patch failed'); process.exit(1); }

// Suppress only the "已暂停" overlay: we use the paused flag as a physics gate, but we do
// not want a paused banner sitting on top of the screenshot we are trying to capture.
const pauseGuard = 'if(paused){';
const renders = body.split(pauseGuard).length - 1;
if (renders !== 1) {
  console.error(`FATAL: expected exactly 1 paused overlay in render(), found ${renders}`);
  process.exit(1);
}
body = body.replace(pauseGuard, 'if(paused && !globalThis.__FREEZE){');

// ---- re-emit with the ORIGINAL markup so every getElementById resolves ----
const styleOpen = src.indexOf('<style>');
const styleClose = src.lastIndexOf('</style>');
const style = src.slice(styleOpen, styleClose + '</style>'.length);
const bodyOpen = src.indexOf('<body>');
const bodyClose = src.lastIndexOf('</body>');
let markup = src.slice(bodyOpen + '<body>'.length, bodyClose).replace(/<script>[\s\S]*<\/script>/, '');

const errHook = `window.onerror=function(m,s,l){var d=document.getElementById('errbox')||document.createElement('pre');
  d.id='errbox';d.style.cssText='position:fixed;left:0;top:0;z-index:99999;background:#900;color:#fff;font:12px monospace;padding:6px';
  d.textContent='JSERROR: '+m+' @line '+l;document.body.appendChild(d);};`;

const driver = `<script>${errHook}
function __dump(){
  var r = window.__PROBE_RESULT;
  if (!r) { return; }
  var f = r.final;
  var lines = [];
  lines.push('MODE=' + r.mode + '  steps=' + r.steps + '  seconds=' + r.seconds);
  lines.push('pocketedObjectBalls=' + f.pocketedObjectBalls + '  cueHist=' + f.cueHist
             + '  allFinite=' + f.allFinite);
  for (var i = 0; i < f.balls.length; i++) {
    var b = f.balls[i];
    lines.push('  ball ' + (b.id < 10 ? ' ' : '') + b.id
      + '  x=' + String(b.x).padStart(8) + '  y=' + String(b.y).padStart(8)
      + '  v=' + String(b.v).padStart(8) + '  pocketed=' + (b.pocketed ? 'YES' : 'no')
      + (b.finite ? '' : '   <<< NON-FINITE'));
  }
  var d = document.createElement('pre');
  d.id = 'probedump';
  d.style.cssText = 'position:fixed;left:0;top:0;z-index:99999;background:#001b12;color:#7CFC9A;'
    + 'font:11px/1.35 monospace;padding:8px;margin:0;white-space:pre;border:1px solid #2a6';
  d.textContent = lines.join('\\n');
  document.body.appendChild(d);
}
(function(){ var tries=0;
  function go(){
    if (globalThis.__PROBE_READY) {
      try { window.__PROBE_RESULT = globalThis.__PROBE(); window.__PROBE_DONE = true; __dump(); }
      catch(e){ var d=document.createElement('pre'); d.id='probeerr';
        d.style.cssText='position:fixed;left:0;top:140px;z-index:99999;background:#06c;color:#fff;font:12px monospace;padding:6px';
        d.textContent='PROBE ERROR: '+e.message+'\\n'+e.stack; document.body.appendChild(d);
        window.__PROBE_DONE = true; }
    } else if (++tries < 800) { setTimeout(go, 25); }
    else { var d=document.createElement('pre'); d.id='probeerr';
      d.style.cssText='position:fixed;left:0;top:140px;z-index:99999;background:#06c;color:#fff;font:12px monospace;padding:6px';
      d.textContent='PROBE TIMEOUT: __PROBE_READY never became true'; document.body.appendChild(d); }
  }
  go(); })();
</script>`;

const out = '<!doctype html><html><head><meta charset="utf-8"><title>REWIND probe: ' + MODE + '</title>'
  + style + '</head><body>' + markup + '<script>' + body + '</script>' + driver + '</body></html>';

fs.writeFileSync(path.join(DIR, 'probe.html'), out);
console.log(`probe.html written: mode=${MODE} settle=${SETTLE_SECONDS}s chars=${out.length}`);
