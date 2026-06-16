/* =====================================================================
   blog.js — Supabase連携のブログ / お知らせ
   閲覧: 全員 ／ 投稿・編集・削除: ログイン中の本人のみ（RLSで保護）
   ===================================================================== */
(function () {
  'use strict';

  const $  = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const cfg = window.NEKONA_CONFIG || {};
  const ready = cfg.SUPABASE_URL && cfg.SUPABASE_ANON_KEY && window.supabase;
  const SITE = 'https://nekona.jp';

  let supa = null;
  let user = null;
  let lastUserId = undefined; // 認証イベントの重複抑止
  let editing = null;         // 編集中の投稿 {table,id} or null

  const els = {
    list:    $('#blogList'),
    post:    $('#blogPost'),
    setup:   $('#setupNotice'),
    adminBar:$('#adminBar'),
    headTitle: $('#blogHeadTitle'),
    loginModal: $('#loginModal'),
    editor:  $('#editorModal'),
  };

  const fmtDate = (iso) => {
    try {
      const d = new Date(iso);
      return `${d.getFullYear()}.${String(d.getMonth() + 1).padStart(2, '0')}.${String(d.getDate()).padStart(2, '0')}`;
    } catch (e) { return ''; }
  };

  function toast(msg, ok = true) {
    let t = $('#toast');
    if (!t) {
      t = document.createElement('div'); t.id = 'toast';
      t.setAttribute('role', 'status'); t.setAttribute('aria-live', 'polite'); t.setAttribute('aria-atomic', 'true');
      document.body.appendChild(t);
    }
    t.setAttribute('aria-live', ok ? 'polite' : 'assertive');
    t.textContent = msg;
    t.className = ok ? 'toast ok show' : 'toast err show';
    clearTimeout(toast._t);
    toast._t = setTimeout(() => t.classList.remove('show'), 3200);
  }

  /* 簡易フォーカストラップ（モーダル共通） */
  function focusables(container) {
    return $$('a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])', container);
  }
  function trapTab(container, e) {
    if (e.key !== 'Tab') return;
    const f = focusables(container);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }

  /* ---------- セットアップ未完了 ---------- */
  if (!ready) {
    if (els.setup) els.setup.hidden = false;
    if (els.list) els.list.hidden = true;
    if (els.adminBar) els.adminBar.hidden = true;
    return;
  }

  supa = window.supabase.createClient(cfg.SUPABASE_URL, cfg.SUPABASE_ANON_KEY);

  /* ---------- ルーティング ---------- */
  function currentPostId() { return new URLSearchParams(location.search).get('p'); }
  function goList() { history.pushState({}, '', 'blog.html'); route(); }
  function goPost(id) { history.pushState({}, '', 'blog.html?p=' + encodeURIComponent(id)); route(); }
  window.addEventListener('popstate', route);

  function setCanonical(href) {
    let l = $('link[rel="canonical"]');
    if (!l) { l = document.createElement('link'); l.rel = 'canonical'; document.head.appendChild(l); }
    l.href = href;
  }

  async function route() {
    const id = currentPostId();
    if (id) {
      if (els.headTitle) els.headTitle.hidden = true;
      els.list.hidden = true; els.post.hidden = false;
      setCanonical(`${SITE}/blog.html?p=${encodeURIComponent(id)}`);
      await renderPost(id);
    } else {
      if (els.headTitle) els.headTitle.hidden = false;
      els.post.hidden = true; els.list.hidden = false;
      document.title = 'Blog — NekoNA';
      setCanonical(`${SITE}/blog.html`);
      await renderList();
    }
  }

  /* ---------- 一覧 ---------- */
  async function renderList() {
    els.list.innerHTML = `<div class="blog-loading mono">読み込み中…</div>`;
    const { data, error } = await supa
      .from('posts').select('id,title,body,created_at,published')
      .order('created_at', { ascending: false });
    if (error) { els.list.innerHTML = `<div class="blog-empty">記事を取得できませんでした。<br><span class="mono">${MD.escapeHtml(error.message)}</span></div>`; return; }
    if (!data || !data.length) {
      els.list.innerHTML = `<div class="blog-empty">まだ記事がありません。${user ? '右上の「＋ 新規投稿」から書いてみましょう。' : ''}</div>`;
      return;
    }
    els.list.innerHTML = data.map((p) => `
      <article class="post-card${p.published ? '' : ' is-draft'}">
        <a class="post-card-link" href="blog.html?p=${encodeURIComponent(p.id)}" data-id="${p.id}">
          <div class="post-card-meta mono"><time>${fmtDate(p.created_at)}</time>${p.published ? '' : ' <span class="draft-tag">下書き</span>'}</div>
          <h2 class="post-card-title">${MD.escapeHtml(p.title || '(無題)')}</h2>
          <p class="post-card-excerpt">${MD.escapeHtml(MD.excerpt(p.body))}</p>
          <span class="post-card-more">読む →</span>
        </a>
        ${adminButtons('posts', p)}
      </article>`).join('');
    bindCardNav();
  }

  function bindCardNav() {
    $$('.post-card-link').forEach((a) => a.addEventListener('click', (e) => {
      e.preventDefault(); goPost(a.dataset.id);
    }));
  }

  /* ---------- 単一記事 ---------- */
  async function renderPost(id) {
    els.post.innerHTML = `<div class="blog-loading mono">読み込み中…</div>`;
    const { data: p, error } = await supa.from('posts').select('*').eq('id', id).single();
    if (error || !p) {
      els.post.innerHTML = `<div class="blog-empty">記事が見つかりませんでした。<br><a href="blog.html" id="backToList">← 一覧へ</a></div>`;
      $('#backToList')?.addEventListener('click', (e) => { e.preventDefault(); goList(); });
      return;
    }
    document.title = `${p.title || '(無題)'} — NekoNA Blog`;
    els.post.innerHTML = `
      <a class="post-back" href="blog.html" id="backToList">← Blog 一覧</a>
      <header class="post-header">
        <div class="post-meta mono"><time>${fmtDate(p.created_at)}</time>${p.published ? '' : ' <span class="draft-tag">下書き</span>'}</div>
        <h1 class="post-title">${MD.escapeHtml(p.title || '(無題)')}</h1>
      </header>
      ${adminButtons('posts', p, true)}
      <div class="post-body md">${MD.render(p.body)}</div>`;
    $('#backToList')?.addEventListener('click', (e) => { e.preventDefault(); goList(); });
    window.scrollTo(0, 0);
  }

  /* ---------- 管理ボタン（編集/削除） ---------- */
  function adminButtons(table, row, inline) {
    if (!user) return '';
    return `<div class="admin-actions${inline ? ' admin-actions--post' : ''}">
      <button class="mini-btn" data-edit="${table}:${row.id}">編集</button>
      <button class="mini-btn mini-btn--danger" data-del="${table}:${row.id}">削除</button>
    </div>`;
  }

  document.addEventListener('click', async (e) => {
    const edit = e.target.closest('[data-edit]');
    const del = e.target.closest('[data-del]');
    if (edit) {
      const [table, id] = edit.dataset.edit.split(':');
      const { data } = await supa.from(table).select('*').eq('id', id).single();
      if (data) openEditor(table, data);
    }
    if (del) {
      const [table, id] = del.dataset.del.split(':');
      if (!confirm('この投稿を削除します。よろしいですか?')) return;
      const { error } = await supa.from(table).delete().eq('id', id);
      if (error) return toast('削除に失敗しました: ' + error.message, false);
      toast('削除しました');
      if (currentPostId() === id) goList(); else route();
    }
  });

  /* ---------- 認証 ---------- */
  async function refreshUser() {
    const { data } = await supa.auth.getSession();
    user = data.session ? data.session.user : null;
    lastUserId = user ? user.id : null;
    updateAdminBar();
  }
  // 認証イベントは「ユーザーが実際に変わった時だけ」反映し、モーダル表示中は再描画しない
  supa.auth.onAuthStateChange((_evt, session) => {
    const u = session ? session.user : null;
    const uid = u ? u.id : null;
    if (uid === lastUserId) return;
    lastUserId = uid; user = u;
    updateAdminBar();
    if (els.editor.hidden && els.loginModal.hidden) route();
  });

  function updateAdminBar() {
    if (!els.adminBar) return;
    els.adminBar.hidden = false;
    if (user) {
      els.adminBar.innerHTML = `
        <span class="admin-who mono">● ${MD.escapeHtml(user.email || 'logged in')}</span>
        <button class="mini-btn" id="newPostBtn">＋ 新規投稿</button>
        <button class="mini-btn mini-btn--ghost" id="logoutBtn">ログアウト</button>`;
      $('#newPostBtn').addEventListener('click', () => openEditor('posts', null));
      $('#logoutBtn').addEventListener('click', async () => { await supa.auth.signOut(); toast('ログアウトしました'); });
    } else {
      els.adminBar.innerHTML = `<button class="mini-btn mini-btn--ghost" id="loginBtn">管理者ログイン</button>`;
      $('#loginBtn').addEventListener('click', openLogin);
    }
  }

  /* ---------- ログインモーダル ---------- */
  let loginLastFocus = null;
  function loginKeydown(e) {
    if (e.key === 'Escape') { closeLogin(); return; }
    trapTab(els.loginModal, e);
  }
  function closeLogin() {
    els.loginModal.hidden = true; els.loginModal.innerHTML = '';
    document.removeEventListener('keydown', loginKeydown);
    if (loginLastFocus) loginLastFocus.focus();
  }
  function openLogin() {
    loginLastFocus = document.activeElement;
    els.loginModal.hidden = false;
    els.loginModal.innerHTML = `
      <div class="modal-card">
        <button class="modal-close" aria-label="閉じる" id="loginClose">×</button>
        <h2 class="modal-title">管理者ログイン</h2>
        <p class="modal-note">登録済みのメールアドレスとパスワードでログインしてください。</p>
        <form id="loginForm" class="form">
          <label class="field"><span>メールアドレス</span><input type="email" id="loginEmail" autocomplete="username" required></label>
          <label class="field"><span>パスワード</span><input type="password" id="loginPass" autocomplete="current-password" required></label>
          <button type="submit" class="btn btn-primary" id="loginSubmit">ログイン</button>
          <p class="form-err" id="loginErr" role="alert" hidden></p>
        </form>
      </div>`;
    $('#loginClose').addEventListener('click', closeLogin);
    els.loginModal.addEventListener('click', (e) => { if (e.target === els.loginModal) closeLogin(); });
    $('#loginForm').addEventListener('submit', async (e) => {
      e.preventDefault();
      const btn = $('#loginSubmit'); btn.disabled = true; btn.textContent = '認証中…';
      const { data, error } = await supa.auth.signInWithPassword({
        email: $('#loginEmail').value.trim(), password: $('#loginPass').value,
      });
      btn.disabled = false; btn.textContent = 'ログイン';
      if (error) { const er = $('#loginErr'); er.hidden = false; er.textContent = 'ログインに失敗しました: ' + error.message; return; }
      // 認証イベントとの競合を避け、結果から直接 user を確定（lastUserId も更新して二重描画を防止）
      user = (data && (data.user || (data.session && data.session.user))) || user;
      lastUserId = user ? user.id : null;
      updateAdminBar();
      closeLogin(); toast('ログインしました'); route(); // 管理ボタン表示のため再描画
    });
    document.addEventListener('keydown', loginKeydown);
    setTimeout(() => $('#loginEmail')?.focus(), 50);
  }

  /* ---------- エディタ ---------- */
  let editorLastFocus = null;
  function editorKeydown(e) {
    if (els.editor.hidden) return;
    if (e.key === 'Escape') { closeEditor(); return; }
    trapTab(els.editor, e);
  }
  function closeEditor() {
    els.editor.hidden = true; els.editor.innerHTML = '';
    document.body.style.overflow = ''; editing = null;
    document.removeEventListener('keydown', editorKeydown);
    if (editorLastFocus) editorLastFocus.focus();
  }
  function openEditor(table, row) {
    editorLastFocus = document.activeElement;
    editing = row ? { table, id: row.id } : null;
    els.editor.hidden = false;
    document.body.style.overflow = 'hidden';
    els.editor.innerHTML = `
      <div class="editor-card">
        <div class="editor-top">
          <h2 class="editor-heading">${row ? '投稿を編集' : '新規投稿'}</h2>
          <div class="editor-top-actions">
            <button class="mini-btn mini-btn--ghost" id="editorCancel">キャンセル</button>
            <button class="mini-btn" id="editorSave">${row ? '更新する' : '公開する'}</button>
          </div>
        </div>
        <div class="editor-controls">
          <label class="field field--inline"><span>投稿先</span>
            <select id="editorTarget" ${row ? 'disabled' : ''}>
              <option value="posts"${table === 'posts' ? ' selected' : ''}>ブログ</option>
              <option value="news"${table === 'news' ? ' selected' : ''}>お知らせ (Discord)</option>
            </select>
          </label>
          <label class="field field--inline check"><input type="checkbox" id="editorPub" ${row ? (row.published ? 'checked' : '') : 'checked'}><span>公開する</span></label>
        </div>
        <input type="text" id="editorTitle" class="editor-title-input" placeholder="タイトル" value="${row ? MD.escapeHtml(row.title || '') : ''}">
        <div class="editor-split">
          <div class="editor-pane">
            <div class="pane-label mono"># 見出し ・ ## 小見出し ・ **太字** ・ - リスト ・ &gt; 引用 ・ [リンク](url)</div>
            <textarea id="editorBody" class="editor-textarea" placeholder="# 見出し&#10;&#10;本文をMarkdownで書けます。&#10;&#10;## 小見出し&#10;- 箇条書き&#10;- リスト">${row ? MD.escapeHtml(row.body || '') : ''}</textarea>
          </div>
          <div class="editor-pane">
            <div class="pane-label mono">プレビュー</div>
            <div class="editor-preview md" id="editorPreview"></div>
          </div>
        </div>
      </div>`;
    const body = $('#editorBody'), preview = $('#editorPreview');
    const upd = () => preview.innerHTML = MD.render(body.value);
    body.addEventListener('input', upd); upd();
    $('#editorCancel').addEventListener('click', closeEditor);
    $('#editorSave').addEventListener('click', saveEditor);
    document.addEventListener('keydown', editorKeydown);
    setTimeout(() => $('#editorTitle')?.focus(), 50);
  }

  async function saveEditor() {
    const title = $('#editorTitle').value.trim();
    const bodyText = $('#editorBody').value;
    const published = $('#editorPub').checked;
    const table = editing ? editing.table : $('#editorTarget').value;
    if (!title) return toast('タイトルを入力してください', false);
    const btn = $('#editorSave'); btn.disabled = true; btn.textContent = '保存中…';

    let res;
    if (editing) {
      res = await supa.from(table).update({ title, body: bodyText, published, updated_at: new Date().toISOString() }).eq('id', editing.id);
    } else {
      res = await supa.from(table).insert({ title, body: bodyText, published });
    }
    btn.disabled = false; btn.textContent = editing ? '更新する' : '公開する';
    if (res.error) return toast('保存に失敗しました: ' + res.error.message, false);
    const isNews = (table === 'news');
    toast(isNews ? 'お知らせを保存しました（Discordページに反映）' : '記事を保存しました');
    closeEditor();
    if (!isNews) route(); // ブログのみ一覧/記事を再描画（お知らせはこのページに表示領域が無い）
  }

  /* ---------- 起動 ---------- */
  document.addEventListener('DOMContentLoaded', async () => {
    await refreshUser();
    route();
  });
})();
