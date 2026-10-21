/* =====================================================================
   NekoNA Portfolio — bglines.js
   縦線ノイズのパララックス背景(2D Canvas)
   ---------------------------------------------------------------------
   ・太さバラバラの縦線 + ノイズ(アルファの揺らぎ)を描画
   ・常に少しずつ下から上へ流れ続ける(時間ドリフト 14px/秒)
   ・スクロール時はさらに大きく上へ移動(スクロール量の0.65倍。
     コンテンツ(1.0倍)より少なめの移動量)
   ・スクロール追従は指数減衰イージングで滑らかに追従(慣性)。
   ・テーマ(ライト/ダーク)のCSS変数 --noise-stroke に連動
   ・タブ非表示でループ停止
   ===================================================================== */
(function () {
  'use strict';

  const SCROLL_FACTOR = 0.65;   // スクロール追従係数(コンテンツより少なめ=0.65倍)
  const DRIFT_SPEED = 14;       // 常時ドリフト速度(px/秒・下→上)
  const EASE = 3.2;             // 追従イージングの速さ(指数減衰・/sec。大きいほど素直に追いつく)
  const DEBUG = (window.__bglines = {
    ok: false, lines: 0,
    factor: SCROLL_FACTOR, driftSpeed: DRIFT_SPEED, ease: EASE,
    up: 0, scrollOffset: 0, scrollTarget: 0, driftOffset: 0,
  });

  const canvas = document.createElement('canvas');
  canvas.className = 'bglines-canvas';
  canvas.setAttribute('aria-hidden', 'true');
  document.body.appendChild(canvas);

  const ctx = canvas.getContext('2d');
  if (!ctx) { canvas.remove(); DEBUG.error = 'no-2d-context'; return; }

  /* 乱数シード(xorshift) — 行を安定させる */
  function makeRng(seed) {
    let s = seed >>> 0 || 1;
    return function () {
      s ^= s << 13; s >>>= 0;
      s ^= s >> 17;
      s ^= s << 5; s >>>= 0;
      return s / 4294967296;
    };
  }

  let dpr = 1;
  let lines = [];
  let tileH = 0;      // タイル高さ(CSS px)
  let vw = 0, vh = 0;

  function strokeColor() {
    const v = getComputedStyle(document.documentElement)
      .getPropertyValue('--noise-stroke').trim();
    return v || 'rgba(20, 36, 45, .16)';
  }

  /* 線の定義を生成: x位置・太さ・ノイズ強度 */
  function buildLines() {
    const rng = makeRng(0x9e3779b9);
    lines = [];
    const count = Math.max(28, Math.round(vw / 26));
    let x = rng() * 24;
    for (let i = 0; i < count; i++) {
      const w = 0.5 + Math.pow(rng(), 2.2) * 5.5;   // 細い線が中心・太い線は少なめ
      lines.push({
        x,
        w,
        freq: 0.008 + rng() * 0.03,                  // ノイズ周期(1/px)
        phase: rng() * Math.PI * 2,
        amp: 0.35 + rng() * 0.65,                    // ノイズ幅
        base: 0.25 + rng() * 0.75,                   // 基礎アルファ倍率
        gapFrom: rng() < 0.18 ? 40 + rng() * 220 : 0, // 一部の線は途切れる
        gapLen: rng() < 0.18 ? 60 + rng() * 300 : 0,
        gapPhase: rng() * Math.PI * 2,
      });
      x += 8 + Math.pow(rng(), 1.6) * 46;
      if (x > vw + 30) break;
    }
    DEBUG.lines = lines.length;
  }

  /* タイルを1枚描いて返す(高さ tileH、幅 vw) */
  function drawTile(color) {
    const off = document.createElement('canvas');
    off.width = Math.max(1, Math.floor(vw * dpr));
    off.height = Math.max(1, Math.floor(tileH * dpr));
    const c = off.getContext('2d');
    c.scale(dpr, dpr);

    for (const L of lines) {
      const step = 8;
      for (let y = 0; y < tileH; y += step) {
        if (L.gapLen > 0) {
          const gy = ((y + L.gapPhase * 40) % (tileH * 0.5));
          if (gy < L.gapLen * 0.35) continue;
        }
        const n = Math.sin((y + L.phase * 100) * L.freq) * 0.5 + 0.5;
        const a = L.base * (0.25 + n * L.amp);
        if (a < 0.02) continue;
        c.globalAlpha = Math.min(a, 1);
        c.fillStyle = color;
        c.fillRect(L.x, y, L.w, step + 1);
      }
    }
    return off;
  }

  let tile = null, tileColor = '';

  function regenerate() {
    buildLines();
    tileColor = strokeColor();
    tile = drawTile(tileColor);
  }

  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    vw = window.innerWidth;
    vh = window.innerHeight;
    canvas.width = Math.max(1, Math.floor(vw * dpr));
    canvas.height = Math.max(1, Math.floor(vh * dpr));
    canvas.style.width = vw + 'px';
    canvas.style.height = vh + 'px';
    tileH = vh + 240;          // タイルはビューポート高さ+余白でループ
    regenerate();
    needsDraw = true;
  }

  /* ───────── 描画: 上方向への累計移動量 up でタイルをずらす ───────── */
  const t0 = Date.now();
  let needsDraw = true;
  let lastT = t0;
  let scrollEased = 0;   // イージング済みスクロールオフセット

  /* 指数減衰スムージング(フレームレート非依存)。
     dt分だけ target へ exp で近づける = ストップ後にゆるやかに減速する慣性挙動 */
  function easeToward(value, target, dt) {
    const k = 1 - Math.exp(-dt * EASE);
    const next = value + (target - value) * k;
    // ほぼ追いついたらスナップ(微小な振れ残りを残さない)
    return Math.abs(target - next) < 0.1 ? target : next;
  }

  function computeUp(nowMs) {
    const dt = Math.min((nowMs - lastT) / 1000, 0.1);   // タブ復帰等の巨大dtをクランプ
    lastT = nowMs;
    const scrollY = window.scrollY || 0;
    const drift = ((nowMs - t0) / 1000) * DRIFT_SPEED;   // 常時下→上(等速)
    const target = scrollY * SCROLL_FACTOR;              // スクロール追従の目標値
    scrollEased = easeToward(scrollEased, target, dt);   // イージングで滑らかに追従
    DEBUG.driftOffset = drift;
    DEBUG.scrollTarget = target;
    DEBUG.scrollOffset = scrollEased;
    return drift + scrollEased;
  }

  function draw() {
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!tile) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const up = computeUp(Date.now());
    DEBUG.up = up;
    // 上へ動かす = 描画yを負方向へ(modでループ)
    let oy = -up % tileH;
    if (oy < 0) oy += tileH;

    // ループ無縫のため2枚分を描画
    ctx.drawImage(tile, 0, oy - tileH);
    ctx.drawImage(tile, 0, oy);
    DEBUG.ok = true;
  }

  /* ───────── アニメーションループ ───────── */
  let raf = 0, running = false;

  function frame() {
    draw();
    if (running) raf = requestAnimationFrame(frame);
  }

  function start() {
    if (running) return;
    running = true;
    raf = requestAnimationFrame(frame);
  }

  function stop() {
    running = false;
    if (raf) cancelAnimationFrame(raf), raf = 0;
  }

  document.addEventListener('visibilitychange', () => {
    document.hidden ? stop() : start();
  });

  window.addEventListener('resize', resize, { passive: true });

  /* テーマ変更で色を差し替え */
  document.addEventListener('themechange', () => {
    const c = strokeColor();
    if (c !== tileColor) { tileColor = c; tile = drawTile(c); needsDraw = true; draw(); }
  });

  resize();
  draw();     // 初回描画
  start();    // 常時ドリフト開始
})();
