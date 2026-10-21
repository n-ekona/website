/* =====================================================================
   NekoNA — petals.js(桜の花びら)
   ---------------------------------------------------------------------
   カーソル(ポインタ)の位置から、桜の花びらが舞い落ちるアニメーション。
   使い方: ページ末尾に <script src="assets/js/petals.js"></script> を置くだけ。
   (d / d-bot の各ページからは ../assets/js/petals.js 等で読み込む)

   ※ 花びらは prefers-reduced-motion 時も舞わせる(既存サイトの方針を継承)
   ===================================================================== */
(function () {
  'use strict';

  /* ── 花びらの形: 先端に切り込みのある桜の花びら ── */
  var PETAL_PATH =
    'M12 23C6 19 3 13 5 7C6.5 3 10 2.5 11.4 6.2C11.8 7.3 12.2 7.3 12.6 6.2' +
    'C14 2.5 17.5 3 19 7C21 13 18 19 12 23Z';

  /* ── 桜の配色(ライト/ダークどちらの背景でも浮かぶ濃度) ── */
  var COLORS = [
    '#ffb7c9', '#ff9fb8', '#ffc9d9',
    '#f98fb0', '#ffd9e3', '#ff8fae'
  ];

  var MIN_INTERVAL = 65;   /* 花びらを出す最小間隔(ms) */
  var MAX_ACTIVE = 70;     /* 同時に存在する花びらの上限 */

  var active = 0;
  var last = 0;
  var pointX = -999;
  var pointY = -999;

  /* ── スタイル注入(CSSを増やさず1ファイルで完結させるため) ── */
  function injectStyle() {
    var css = [
      '.petal{',
      'position:fixed;left:0;top:0;pointer-events:none;z-index:9998;',
      'background-repeat:no-repeat;background-size:100% 100%;',
      'filter:drop-shadow(0 1px 2px rgba(200,110,145,.35));',
      'will-change:transform,opacity;',
      'animation:sakura-fall linear forwards;',
      '}',
      '@keyframes sakura-fall{',
      '0%{transform:translate3d(0,0,0) rotate(0deg) scale(.7);opacity:0}',
      '14%{opacity:.92}',
      '100%{transform:translate3d(var(--dx),var(--dy),0) rotate(var(--dr)) scale(1);opacity:0}',
      '}'
    ].join('');
    var el = document.createElement('style');
    el.setAttribute('data-petals', 'sakura');
    el.appendChild(document.createTextNode(css));
    (document.head || document.documentElement).appendChild(el);
  }

  function petalImage(color) {
    var svg =
      "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
      "<path d='" + PETAL_PATH + "' fill='" + color + "'/></svg>";
    return 'url("data:image/svg+xml,' + encodeURIComponent(svg) + '")';
  }

  function spawn(x, y) {
    if (active >= MAX_ACTIVE) return;

    var now = Date.now();
    if (now - last < MIN_INTERVAL) return;
    last = now;

    var el = document.createElement('div');
    el.className = 'petal';
    el.setAttribute('aria-hidden', 'true');

    var size = 8 + Math.random() * 9;
    var color = COLORS[(Math.random() * COLORS.length) | 0];
    var dur = 950 + Math.random() * 850;
    var dx = (Math.random() - 0.5) * 90;
    var dy = 70 + Math.random() * 90;
    var dr = ((Math.random() - 0.5) * 720).toFixed(0) + 'deg';

    el.style.left = (x + (Math.random() - 0.5) * 10) + 'px';
    el.style.top = (y + (Math.random() - 0.5) * 10) + 'px';
    el.style.width = size + 'px';
    el.style.height = size * 1.15 + 'px';
    el.style.margin = (-size / 2) + 'px 0 0 ' + (-size / 2) + 'px';
    el.style.backgroundImage = petalImage(color);
    el.style.setProperty('--dx', dx.toFixed(1) + 'px');
    el.style.setProperty('--dy', dy.toFixed(1) + 'px');
    el.style.setProperty('--dr', dr);
    el.style.animationDuration = dur.toFixed(0) + 'ms';

    active++;
    var finished = false;
    var done = function () {
      if (finished) return;      /* 二重カウント防止 */
      finished = true;
      active--;
      if (el.parentNode) el.parentNode.removeChild(el);
    };
    el.addEventListener('animationend', done);
    /* 万一 animationend が来ない場合の保険 */
    setTimeout(done, dur + 600);

    (document.body || document.documentElement).appendChild(el);
  }

  function move(x, y) {
    pointX = x;
    pointY = y;
    spawn(x, y);
  }

  function init() {
    if (!document.body) { setTimeout(init, 60); return; }
    injectStyle();

    document.addEventListener('mousemove', function (e) {
      move(e.clientX, e.clientY);
    }, { passive: true });

    document.addEventListener('touchmove', function (e) {
      var t = e.touches && e.touches[0];
      if (t) move(t.clientX, t.clientY);
    }, { passive: true });
  }

  init();
})();
