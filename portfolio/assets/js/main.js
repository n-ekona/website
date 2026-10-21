/* =====================================================================
   NekoNA Portfolio — main.js (Simple)
   リスト(個人開発/実績)の描画・検索/タグ絞り込み・出現アニメ・ヘッダー挙動
   ===================================================================== */
(function () {
  'use strict';

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const $  = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const esc = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  /* ---------- プロジェクトアイテムHTML ---------- */
  function projectItemHTML(p) {
    const tags = (p.tags || []).map((t) => `<span>${esc(t)}</span>`).join('');
    const title = p.link
      ? `<a href="${esc(p.link)}" target="_blank" rel="noopener noreferrer">${esc(p.name)}</a>`
      : esc(p.name);
    const desc = p.desc ? `<p>${esc(p.desc)}</p>` : '';
    const meta = p.meta ? `<p class="work-caption">${esc(p.meta)}</p>` : '';
    const link = p.link
      ? `<a class="work-link" href="${esc(p.link)}" target="_blank" rel="noopener noreferrer">${esc(p.linkLabel || 'GitHub')}</a>`
      : '';

    return `<article class="work-item">
      <div>
        <p class="item-tags">${tags}</p>
        <h2 class="work-name">${title}</h2>
      </div>
      <div class="work-body">${desc}${meta}${link}</div>
    </article>`;
  }

  /* ---------- リストページ共用: 全件描画 + 検索/タグ絞り込み ---------- */
  /*  個人開発(works.html / PROJECTS)と実績(creations.html / CREATIONS)で使用。
      ページごとに読み込んだデータファイルのグローバルを自動判別する。 */
  function initListPage() {
    const list = $('#worksList');
    if (!list) return;

    const hasCreations = typeof CREATIONS !== 'undefined';
    const data = hasCreations ? CREATIONS
      : (typeof PROJECTS !== 'undefined' ? PROJECTS : null);
    if (!data) return;
    // 実績ページはCREATION_TAGSの4タグを固定表示(0件でもボタンを出す)
    const fixedTags = (hasCreations && typeof CREATION_TAGS !== 'undefined')
      ? CREATION_TAGS.slice() : null;

    list.innerHTML = data.map(projectItemHTML).join('');

    const searchInput = $('#worksSearch');
    const clearBtn = $('#worksSearchClear');
    const countEl = $('#worksCount');
    const tagsWrap = $('#worksFilterTags');
    const emptyEl = $('#worksEmpty');
    const resetBtn = $('#worksReset');
    let activeTag = '';

    /* タグボタン生成(固定タグ or 全タグの重複除去) */
    if (tagsWrap) {
      const all = fixedTags ? fixedTags.slice() : [];
      if (!fixedTags) {
        data.forEach((p) => (p.tags || []).forEach((t) => { if (!all.includes(t)) all.push(t); }));
      }
      tagsWrap.innerHTML = all.map((t) =>
        `<button type="button" data-tag="${esc(t)}" aria-pressed="false">${esc(t)}</button>`
      ).join('');
    }

    const apply = () => {
      const q = (searchInput && searchInput.value || '').trim().toLowerCase();
      let shown = 0;

      $$('.work-item', list).forEach((el, i) => {
        const p = data[i];
        const hay = [p.name, p.desc, p.meta, ...(p.tags || [])].join(' ').toLowerCase();
        const okTag = !activeTag || (p.tags || []).includes(activeTag);
        const okText = !q || hay.includes(q);
        const on = okTag && okText;
        el.hidden = !on;
        if (on) shown++;
      });

      if (countEl) countEl.textContent = `${shown} / ${data.length} 件`;
      if (emptyEl) emptyEl.hidden = shown !== 0;
      if (clearBtn) clearBtn.hidden = !q;
    };

    if (searchInput) searchInput.addEventListener('input', apply);
    if (clearBtn) clearBtn.addEventListener('click', () => {
      searchInput.value = ''; searchInput.focus(); apply();
    });

    if (tagsWrap) {
      tagsWrap.addEventListener('click', (e) => {
        const btn = e.target.closest('button[data-tag]');
        if (!btn) return;
        activeTag = (activeTag === btn.dataset.tag) ? '' : btn.dataset.tag;
        $$('button[data-tag]', tagsWrap).forEach((b) => {
          b.setAttribute('aria-pressed', String(b.dataset.tag === activeTag));
        });
        apply();
      });
    }

    if (resetBtn) resetBtn.addEventListener('click', () => {
      activeTag = '';
      if (searchInput) searchInput.value = '';
      $$('button[data-tag]', tagsWrap || document).forEach((b) => b.setAttribute('aria-pressed', 'false'));
      apply();
    });

    apply();
  }

  /* ---------- 出現アニメ ---------- */
  function initReveal() {
    const items = $$('.reveal');
    if (reduceMotion || !('IntersectionObserver' in window)) {
      items.forEach((el) => el.classList.add('is-in'));
      return;
    }
    const io = new IntersectionObserver((entries) => {
      entries.forEach((en) => {
        if (en.isIntersecting) { en.target.classList.add('is-in'); io.unobserve(en.target); }
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -6% 0px' });
    items.forEach((el) => io.observe(el));
  }

  /* ---------- ヘッダー: スクロールで罫線を出す ---------- */
  function initHeader() {
    const bar = $('.site-header');
    if (!bar) return;
    let on = null;
    const onScroll = () => {
      const next = window.scrollY > 8;
      if (next !== on) { on = next; bar.classList.toggle('is-scrolled', next); }
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ---------- フッターの年 ---------- */
  function initYear() {
    const el = $('#year');
    if (el) el.textContent = new Date().getFullYear();
  }

  /* ---------- ダーク/ライトテーマ切替 ---------- */
  const THEME_KEY = 'theme';

  function systemTheme() {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  function applyTheme(mode) {
    document.documentElement.setAttribute('data-theme', mode);
    const meta = $('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', mode === 'dark' ? '#0e1620' : '#fafdfe');
    // 背景モジュールへ通知(bg3d.js / bglines.js が監視)
    document.dispatchEvent(new CustomEvent('themechange', { detail: { mode } }));
    return mode;
  }

  function storedTheme() {
    try { return localStorage.getItem(THEME_KEY); } catch (e) { return null; }
  }

  function storeTheme(mode) {
    try { localStorage.setItem(THEME_KEY, mode); } catch (e) { /* 無視 */ }
  }

  function initTheme() {
    // head内の初期適用スクリプトで data-theme は既に設定済み。ボタンだけ整える
    const btn = $('.theme-toggle');
    if (!btn) return;
    const syncLabel = () => {
      const dark = document.documentElement.getAttribute('data-theme') === 'dark';
      btn.setAttribute('aria-pressed', String(dark));
      const name = btn.querySelector('.theme-toggle-name');
      if (name) name.textContent = dark ? 'ライト' : 'ダーク';
      btn.setAttribute('aria-label', dark ? 'ライトモードに切り替える' : 'ダークモードに切り替える');
    };
    syncLabel();

    btn.addEventListener('click', () => {
      const next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      applyTheme(next);
      storeTheme(next);
      syncLabel();
    });

    // OSの設定変更に追従(ユーザーが明示的に選択した場合は従わない)
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const onMq = () => { if (!storedTheme()) { applyTheme(systemTheme()); syncLabel(); } };
    if (mq.addEventListener) mq.addEventListener('change', onMq);
    else if (mq.addListener) mq.addListener(onMq);
  }

  document.addEventListener('DOMContentLoaded', () => {
    initListPage();
    initReveal();
    initHeader();
    initYear();
    initTheme();
  });
})();
