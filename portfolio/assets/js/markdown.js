/* =====================================================================
   markdown.js — 軽量Markdownレンダラ（依存なし・XSS対策込み）
   対応: # 見出し / ## 小見出し / ### / **太字** / *斜体* / `コード`
         ```コードブロック``` / > 引用 / - ・1. リスト / --- 区切り線
         [リンク](url) / ![画像](url) / 段落・改行
   ＊ テキストは出力時に必ずHTMLエスケープするため、生HTMLは無効化されます。
   ===================================================================== */
(function () {
  'use strict';

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // 安全なURLのみ許可（javascript: 等を弾く）
  function safeUrl(url) {
    const u = String(url).trim();
    if (/^(https?:|mailto:|\/|#|\.\/|\.\.\/)/i.test(u)) return u;
    return '#';
  }

  // インライン要素：先にエスケープしてから装飾タグを付与する
  function inline(raw) {
    let text = escapeHtml(raw);
    // 画像 ![alt](url)
    text = text.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (m, alt, url) =>
      `<img src="${safeUrl(url)}" alt="${alt}" loading="lazy">`);
    // リンク [text](url)
    text = text.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, t, url) =>
      `<a href="${safeUrl(url)}" target="_blank" rel="noopener noreferrer">${t}</a>`);
    // 太字 **text**
    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    // 斜体 *text*
    text = text.replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>');
    // インラインコード `code`
    text = text.replace(/`([^`]+)`/g, '<code>$1</code>');
    return text;
  }

  const BLOCK_START = /^(#{1,4}\s|>\s?|```|\d+\.\s|[-*+]\s)/;
  const HR = /^(\s*[-*_]){3,}\s*$/;

  function render(md) {
    if (!md) return '';
    const lines = String(md).replace(/\r\n?/g, '\n').split('\n');
    let html = '';
    let i = 0;
    let listType = null; // 'ul' | 'ol'
    const closeList = () => { if (listType) { html += `</${listType}>`; listType = null; } };

    while (i < lines.length) {
      const line = lines[i];

      // コードブロック ```
      if (/^```/.test(line)) {
        closeList();
        i++;
        let code = '';
        while (i < lines.length && !/^```/.test(lines[i])) { code += lines[i] + '\n'; i++; }
        i++; // 閉じ ```
        html += `<pre><code>${escapeHtml(code.replace(/\n$/, ''))}</code></pre>`;
        continue;
      }

      // 見出し（# → h2, ## → h3, ### → h4）
      const m = line.match(/^(#{1,4})\s+(.*)$/);
      if (m) {
        closeList();
        const level = Math.min(m[1].length + 1, 5);
        html += `<h${level}>${inline(m[2].trim())}</h${level}>`;
        i++; continue;
      }

      // 区切り線
      if (HR.test(line)) { closeList(); html += '<hr>'; i++; continue; }

      // 引用 >
      if (/^>\s?/.test(line)) {
        closeList();
        let quote = '';
        while (i < lines.length && /^>\s?/.test(lines[i])) { quote += lines[i].replace(/^>\s?/, '') + '\n'; i++; }
        html += `<blockquote>${render(quote)}</blockquote>`;
        continue;
      }

      // 番号付きリスト
      if (/^\d+\.\s+/.test(line)) {
        if (listType !== 'ol') { closeList(); html += '<ol>'; listType = 'ol'; }
        html += `<li>${inline(line.replace(/^\d+\.\s+/, ''))}</li>`;
        i++; continue;
      }
      // 箇条書きリスト
      if (/^[-*+]\s+/.test(line)) {
        if (listType !== 'ul') { closeList(); html += '<ul>'; listType = 'ul'; }
        html += `<li>${inline(line.replace(/^[-*+]\s+/, ''))}</li>`;
        i++; continue;
      }

      // 空行
      if (/^\s*$/.test(line)) { closeList(); i++; continue; }

      // 段落（連続する非空行をまとめる）
      closeList();
      let para = line;
      i++;
      while (i < lines.length && !/^\s*$/.test(lines[i]) && !BLOCK_START.test(lines[i]) && !HR.test(lines[i])) {
        para += '\n' + lines[i]; i++;
      }
      html += `<p>${inline(para).replace(/\n/g, '<br>')}</p>`;
    }
    closeList();
    return html;
  }

  // プレーンテキスト抜粋（一覧の要約用）
  function excerpt(md, len = 110) {
    const text = String(md || '')
      .replace(/```[\s\S]*?```/g, ' ')
      .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
      .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
      .replace(/^\s*([-*_]\s*){3,}$/gm, ' ')   // 区切り線
      .replace(/^\s*\d+\.\s+/gm, '')           // 番号付きリスト
      .replace(/^\s*[-*+]\s+/gm, '')           // 箇条書き
      .replace(/^#{1,4}\s+/gm, '')             // 見出し記号(行頭のみ)
      .replace(/^>\s?/gm, '')                  // 引用記号(行頭のみ)
      .replace(/[*_`~]/g, '')                  // 強調・コード記号
      .replace(/\s+/g, ' ')
      .trim();
    return text.length > len ? text.slice(0, len) + '…' : text;
  }

  window.MD = { render, excerpt, escapeHtml };
})();
