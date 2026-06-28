/* =====================================================================
   NekoNA Portfolio — main.js
   ===================================================================== */
(function () {
  'use strict';

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const finePointer = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
  const $  = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  /* ---------- カテゴリ別モチーフ（プレースホルダ用 SVG） ---------- */
  function motif(cat, color) {
    // ジャンル別のラインアイコン（制作中プレースホルダ用）
    const wrap = (inner) => `<svg class="ph-motif" viewBox="0 0 48 48" fill="none" stroke="${color}" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${inner}</svg>`;
    switch (cat) {
      case 'video': // 映像：クラップボード＋再生
        return wrap(`<rect x="7" y="14" width="34" height="22" rx="2.5"/><path d="M7 20.5h34"/><path d="M13 14.5l3.4 6M21 14.5l3.4 6M29 14.5l3.4 6" opacity=".65"/><path d="M21.5 25l6.5 3.8-6.5 3.8z" fill="${color}" stroke="none"/>`);
      case 'illust': // イラスト：ペン先
        return wrap(`<path d="M30 8l10 10-19 19-10 3 3-10z"/><path d="M27.5 10.5l10 10" opacity=".55"/><path d="M14 37l4-1"/>`);
      case 'cg': // 3DCG：立方体（アイソメ）
        return wrap(`<path d="M24 7l15 8.5v17L24 41 9 32.5v-17z"/><path d="M24 24l15-8.5M24 24L9 15.5M24 24v17" opacity=".6"/>`);
      case 'electronics': // 電子工作：IC チップ
        return wrap(`<rect x="15" y="15" width="18" height="18" rx="2"/><circle cx="20" cy="20" r="1.5" fill="${color}" stroke="none"/><path d="M20 15v-5M28 15v-5M20 33v5M28 33v5M15 20h-5M15 28h-5M33 20h5M33 28h5"/>`);
      case 'music': // 作曲：音符
        return wrap(`<path d="M19 31.5V12l17-3.2v19.7"/><path d="M19 17.5l17-3.2" opacity=".55"/><ellipse cx="15" cy="32" rx="4.2" ry="3.4" fill="${color}" stroke="none"/><ellipse cx="32" cy="29" rx="4.2" ry="3.4" fill="${color}" stroke="none"/>`);
      default: // 汎用（グラフィック/デザイン）
        return wrap(`<rect x="10" y="10" width="28" height="28" rx="3"/><circle cx="24" cy="24" r="6.5"/>`);
    }
  }

  /* ---------- Works レンダリング ---------- */
  function renderWorks() {
    const grid = $('#worksGrid');
    if (!grid || typeof WORKS === 'undefined') return;
    grid.innerHTML = WORKS.map((w) => {
      const cat = CATEGORIES[w.category] || { label: w.category, color: '#6E7BF0' };
      const isWip = w.wip || !w.thumbnail;
      const badge = `<span class="work-badge" style="background:${cat.color}">${esc(cat.label)}</span>`;
      const wip = isWip ? `<span class="work-wip">制作中</span>` : '';

      let thumb;
      if (isWip) {
        thumb = `<div class="work-thumb placeholder"><div class="ph-grid"></div>${motif(w.category, cat.color)}</div>`;
      } else if (w.category === 'video') {
        thumb = `<div class="work-thumb"><img src="${esc(w.thumbnail)}" alt="${esc(w.title)}" loading="lazy">
          <div class="work-play"><span aria-hidden="true">▶</span></div></div>`;
      } else {
        thumb = `<div class="work-thumb"><img src="${esc(w.thumbnail)}" alt="${esc(w.title)}" loading="lazy"></div>`;
      }

      const tools = (w.tools || []).map((t) => `<span class="tool">${esc(t)}</span>`).join('');
      const titleEn = w.titleEn ? `<div class="work-title-en">${esc(w.titleEn)}</div>` : '';
      const desc = w.desc ? `<p class="work-desc">${esc(w.desc)}</p>` : '';
      const foot = `<div class="work-foot"><span>${esc(w.specs || '')}</span><span>${esc(w.year || '')}</span></div>`;

      // リンク/挙動: 外部リンクがあれば a、なければ div（デザインはライトボックス対象）
      const tag = w.link && !isWip ? 'a' : 'div';
      const attrs = [
        `class="work-card cat-${esc(w.category)}"`,
        `data-cat="${esc(w.category)}"`,
        w.link && !isWip ? `href="${esc(w.link)}" target="_blank" rel="noopener noreferrer"` : '',
        !isWip && w.category === 'design' ? `data-lightbox="${esc(w.thumbnail)}" data-cap="${esc(w.title)}"` : '',
      ].filter(Boolean).join(' ');

      return `<${tag} ${attrs}>${badge}${wip}${thumb}
        <div class="work-body">
          <div class="work-title">${esc(w.title)}</div>${titleEn}${desc}
          <div class="work-meta">${tools}</div>${foot}
        </div></${tag}>`;
    }).join('');
  }

  /* ---------- Skills レンダリング ---------- */
  function renderSkills() {
    const grid = $('#skillsGrid');
    if (!grid || typeof SKILLS === 'undefined') return;
    grid.innerHTML = SKILLS.map((s) => `
      <div class="skill-card">
        <div class="skill-cat"><span class="skill-dot" style="background:${esc(s.color)}"></span>
          <span class="skill-name">${esc(s.name)}</span></div>
        <ul class="skill-tools">${(s.tools || []).map((t) => `<li>${esc(t)}</li>`).join('')}</ul>
      </div>`).join('');
  }

  /* ---------- タブフィルタ ---------- */
  function initTabs() {
    const tabs = $$('#worksTabs .tab');
    const grid = $('#worksGrid');
    if (!tabs.length) return;
    const activate = (tab) => {
      tabs.forEach((t) => {
        const on = t === tab;
        t.classList.toggle('is-active', on);
        t.setAttribute('aria-selected', String(on));
        t.tabIndex = on ? 0 : -1;
      });
      if (grid && tab.id) grid.setAttribute('aria-labelledby', tab.id);
      const f = tab.dataset.filter;
      $$('#worksGrid .work-card').forEach((card) => {
        card.classList.toggle('is-hidden', !(f === 'all' || card.dataset.cat === f));
      });
    };
    tabs.forEach((tab, idx) => {
      tab.addEventListener('click', () => activate(tab));
      tab.addEventListener('keydown', (e) => {
        let ni = null;
        if (e.key === 'ArrowRight' || e.key === 'ArrowDown') ni = (idx + 1) % tabs.length;
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') ni = (idx - 1 + tabs.length) % tabs.length;
        else if (e.key === 'Home') ni = 0;
        else if (e.key === 'End') ni = tabs.length - 1;
        if (ni === null) return;
        e.preventDefault(); tabs[ni].focus(); activate(tabs[ni]);
      });
    });
    activate(tabs.find((t) => t.classList.contains('is-active')) || tabs[0]);
  }

  /* ---------- ライトボックス ---------- */
  function initLightbox() {
    const lb = $('#lightbox'), img = $('#lightboxImg'), cap = $('#lightboxCap'), closeBtn = $('#lightboxClose');
    if (!lb) return;
    let lastFocus = null;
    const open = (src, caption) => {
      img.src = src; img.alt = caption || ''; cap.textContent = caption || '';
      lb.hidden = false; lastFocus = document.activeElement; closeBtn.focus();
      document.addEventListener('keydown', onKey);
    };
    const close = () => {
      lb.hidden = true; img.src = ''; document.removeEventListener('keydown', onKey);
      if (lastFocus) lastFocus.focus();
    };
    const onKey = (e) => {
      if (e.key === 'Escape') close();
      if (e.key === 'Tab') { e.preventDefault(); closeBtn.focus(); } // 単純フォーカストラップ
    };
    document.addEventListener('click', (e) => {
      const card = e.target.closest('[data-lightbox]');
      if (card) { e.preventDefault(); open(card.dataset.lightbox, card.dataset.cap); }
    });
    closeBtn.addEventListener('click', close);
    lb.addEventListener('click', (e) => { if (e.target === lb) close(); });
  }

  /* ---------- 出現アニメ ---------- */
  function initReveal() {
    const items = $$('.reveal');
    if (reduceMotion || !('IntersectionObserver' in window)) {
      items.forEach((el) => el.classList.add('is-in')); return;
    }
    const io = new IntersectionObserver((entries) => {
      entries.forEach((en) => { if (en.isIntersecting) { en.target.classList.add('is-in'); io.unobserve(en.target); } });
    }, { threshold: 0.12, rootMargin: '0px 0px -8% 0px' });
    items.forEach((el) => io.observe(el));
  }

  /* ---------- ナビ: 現在地ハイライト ---------- */
  function initNavSpy() {
    const links = $$('.nav a[href^="#"]');
    const map = new Map();
    links.forEach((a) => { const id = a.getAttribute('href').slice(1); const sec = document.getElementById(id); if (sec) map.set(sec, a); });
    if (!map.size || !('IntersectionObserver' in window)) return;
    const io = new IntersectionObserver((entries) => {
      entries.forEach((en) => {
        const a = map.get(en.target);
        if (!a) return;
        if (en.isIntersecting) { links.forEach((l) => l.classList.remove('is-current')); a.classList.add('is-current'); }
      });
    }, { threshold: 0.5 });
    map.forEach((_, sec) => io.observe(sec));
  }

  /* ---------- モバイルメニュー ---------- */
  function initMobileNav() {
    const toggle = $('#navToggle'), bar = $('#topbar'), nav = $('#topbar .nav');
    if (!toggle || !bar) return;
    const close = (focusToggle) => {
      if (!bar.classList.contains('menu-open')) return;
      bar.classList.remove('menu-open'); toggle.setAttribute('aria-expanded', 'false');
      if (focusToggle) toggle.focus();
    };
    toggle.addEventListener('click', () => {
      const open = bar.classList.toggle('menu-open');
      toggle.setAttribute('aria-expanded', String(open));
      if (open && nav) { const first = nav.querySelector('a'); if (first) first.focus(); }
    });
    $$('#topbar .nav a').forEach((a) => a.addEventListener('click', () => close(false)));
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(true); });
  }

  /* ---------- YouTube IFrame API のローダ ---------- */
  let _ytLoading = false; const _ytQueue = [];
  function loadYouTubeAPI(cb) {
    if (window.YT && window.YT.Player) { cb(); return; }
    _ytQueue.push(cb);
    if (_ytLoading) return;
    _ytLoading = true;
    const prev = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = function () {
      if (typeof prev === 'function') { try { prev(); } catch (e) {} }
      _ytQueue.splice(0).forEach((fn) => { try { fn(); } catch (e) {} });
    };
    const s = document.createElement('script');
    s.src = 'https://www.youtube.com/iframe_api';
    document.head.appendChild(s);
  }

  /* ---------- ヒーロー背景メディア（YouTube/画像・ぼかし無音・7秒ランダム切替） ---------- */
  function initHeroBg() {
    const heroBg = $('#heroBg');
    const wrap = $('#heroBgMedia');
    const hero = $('#hero');
    if (!heroBg || !wrap || !hero) return;
    if (typeof HERO_MEDIA === 'undefined' || !Array.isArray(HERO_MEDIA)) return;
    const media = HERO_MEDIA.filter((m) => m && (m.type === 'youtube' ? m.id : m.src));
    if (!media.length) return;

    // サムネ層（常時表示）＋ プレーヤー層（再生時にフェードイン）
    const thumb = document.createElement('div'); thumb.className = 'hero-bg-thumb';
    const stage = document.createElement('div'); stage.className = 'hero-bg-player';
    wrap.appendChild(thumb); wrap.appendChild(stage);

    let player = null;
    const clearPlayer = () => {
      stage.classList.remove('is-playing');
      if (player) { try { player.destroy(); } catch (e) {} player = null; }
      stage.innerHTML = '';
    };

    const showYouTube = (m) => {
      const id = m.id;
      // サムネは maxres → 無ければ hq（背景の重ね順でフォールバック）
      thumb.style.backgroundImage =
        "url('https://i.ytimg.com/vi/" + id + "/maxresdefault.jpg'), url('https://i.ytimg.com/vi/" + id + "/hqdefault.jpg')";
      clearPlayer();
      const host = document.createElement('div');
      stage.appendChild(host);
      loadYouTubeAPI(function () {
        if (!host.isConnected) return; // 既に切り替わっていたら何もしない
        player = new YT.Player(host, {
          videoId: id,
          playerVars: {
            autoplay: 1, mute: 1, controls: 0, loop: 1, playlist: id,
            start: (parseInt(m.start, 10) || 0),
            modestbranding: 1, playsinline: 1, rel: 0, disablekb: 1, fs: 0, iv_load_policy: 3,
          },
          events: {
            onReady: (e) => { try { e.target.mute(); e.target.playVideo(); } catch (err) {} },
            onStateChange: (e) => { if (e.data === 1) stage.classList.add('is-playing'); }, // 1=PLAYING
          },
        });
      });
    };

    const showImage = (m) => { clearPlayer(); thumb.style.backgroundImage = "url('" + m.src + "')"; };

    let cur = -1;
    const show = (i) => { const m = media[i]; if (m.type === 'youtube') showYouTube(m); else showImage(m); cur = i; };
    const next = () => {
      if (media.length === 1) { if (cur !== 0) show(0); return; }
      let n; do { n = (Math.random() * media.length) | 0; } while (n === cur);
      show(n);
    };
    next();
    if (media.length > 1) setInterval(next, 7000);

    // 表示ON（イントロでふわっと）＋ 下にスクロールするほど動画を暗くする
    heroBg.classList.add('is-active');
    const dark = $('#heroBgDark');
    let ticking = false;
    const onScroll = () => {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(() => {
        const p = Math.min(1, Math.max(0, window.scrollY / (window.innerHeight * 0.9)));
        if (dark) dark.style.opacity = p.toFixed(3);
        ticking = false;
      });
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ---------- 固定ヘッダー: スクロールで締める ---------- */
  function initStickyHeader() {
    const bar = $('#topbar');
    if (!bar) return;
    let on = null;
    const onScroll = () => {
      const next = window.scrollY > 10;
      if (next !== on) { on = next; bar.classList.toggle('is-scrolled', next); }
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ---------- スクロール進捗レール ---------- */
  function initScrollRail() {
    const head = $('.scroll-head');
    if (!head) return;
    const onScroll = () => {
      const h = document.documentElement;
      const max = h.scrollHeight - h.clientHeight;
      const p = max > 0 ? h.scrollTop / max : 0;
      head.style.top = (p * 100) + '%';
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ---------- カスタムカーソル + 背景グロー ---------- */
  function initCursor() {
    if (!finePointer) return;
    const cur = $('#cursor');
    if (!cur) return;
    document.body.classList.add('has-cursor');
    cur.style.display = 'block';
    const glow = $('.bg-glow');
    let raf = null, x = 0, y = 0;
    window.addEventListener('mousemove', (e) => {
      x = e.clientX; y = e.clientY;
      cur.style.opacity = '1';
      if (raf) return;
      raf = requestAnimationFrame(() => {
        cur.style.transform = `translate(${x}px,${y}px) translate(-50%,-50%)`;
        if (!reduceMotion && glow) {
          glow.style.setProperty('--mx', (x / window.innerWidth * 100) + '%');
          glow.style.setProperty('--my', (y / window.innerHeight * 100) + '%');
        }
        raf = null;
      });
    });
    document.addEventListener('mouseover', (e) => {
      cur.classList.toggle('is-hover', !!e.target.closest('a,button,.tab,.work-card,.contact-card,[data-lightbox]'));
    });
    document.addEventListener('mouseout', (e) => { if (!e.relatedTarget) cur.style.opacity = '0'; });
  }

  /* ---------- 桜の花びら（カーソル追従 + 上から舞い散る） ---------- */
  function initPetals() {
    // ※ ユーザー要望により reduced-motion でも常に表示する
    // 花びらの色（桜のピンク）
    const PETALS = [
      'rgba(255,170,200,0.86)', 'rgba(247,148,190,0.82)', 'rgba(255,200,218,0.82)',
      'rgba(244,138,182,0.84)', 'rgba(255,186,206,0.80)', 'rgba(236,160,192,0.82)',
    ];
    const rnd = (a, b) => a + Math.random() * (b - a);
    const pick = (a) => a[(Math.random() * a.length) | 0];
    let count = 0;
    const MAX = 40;

    function petal(x, y) {
      if (count >= MAX) return;
      count++;
      const el = document.createElement('div');
      el.className = 'petal';
      el.setAttribute('aria-hidden', 'true');
      const size = rnd(7, 15);
      const dx = rnd(-40, 40);
      const dy = rnd(60, 120);
      const dur = rnd(900, 1600);
      const dr = ((Math.random() - 0.5) * 360 | 0) + 'deg';
      el.style.left = x + 'px';
      el.style.top = y + 'px';
      el.style.width = size.toFixed(1) + 'px';
      el.style.height = (size * 0.65).toFixed(1) + 'px';
      el.style.background = pick(PETALS);
      el.style.filter = 'blur(.4px)';
      el.style.setProperty('--dx', dx.toFixed(0) + 'px');
      el.style.setProperty('--dy', dy.toFixed(0) + 'px');
      el.style.setProperty('--dr', dr);
      el.style.setProperty('--pdur', dur.toFixed(0) + 'ms');
      document.body.appendChild(el);
      // animationend を取りこぼしても必ずカウントを戻す（保険）
      let done = false;
      const cleanup = () => { if (done) return; done = true; el.remove(); count--; };
      el.addEventListener('animationend', cleanup);
      setTimeout(cleanup, dur * 1.5 + 200);
    }

    // カーソル追従で花びらが舞う（DiscordWeb /d と同じ：pointer 種別で制限しない）
    let lastT = 0;
    window.addEventListener('mousemove', (e) => {
      const now = performance.now();
      if (now - lastT < 60) return;
      lastT = now;
      petal(e.clientX, e.clientY);
    }, { passive: true });
  }

  /* ---------- 初期化 ---------- */
  /* ---------- ローディング画面 ---------- */
  function initLoader() {
    const l = document.getElementById('loader');
    if (!l) return;
    let hidden = false;
    const hide = () => {
      if (hidden) return;
      hidden = true;
      l.classList.add('done');
      setTimeout(() => l.remove(), 900);
    };
    window.addEventListener('load', () => setTimeout(hide, 250));
    setTimeout(hide, 4000); // フォールバック
  }

  document.addEventListener('DOMContentLoaded', () => {
    initLoader();
    renderWorks();
    renderSkills();
    initTabs();
    initLightbox();
    initReveal();
    initNavSpy();
    initMobileNav();
    initStickyHeader();
    initScrollRail();
    initCursor();
    initPetals();
    initHeroBg();
  });
})();
