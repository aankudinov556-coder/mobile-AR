/* Маленький безопасный рендерер Markdown (сначала экранирует HTML). */
(function () {
  'use strict';

  function esc(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function inline(s) {
    const codes = [];
    s = s.replace(/`([^`\n]+)`/g, (_, c) => { codes.push(c); return `\u0000${codes.length - 1}\u0000`; });
    s = esc(s);
    s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, t, u) => `<a href="${u}" data-ext="1">${t}</a>`);
    s = s.replace(/(^|[\s(])(https?:\/\/[^\s<)]+[^\s<).,!?;:'"])/g, (_, p, u) => `${p}<a href="${u}" data-ext="1">${u}</a>`);
    s = s.replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>');
    s = s.replace(/__([^_\n]+)__/g, '<b>$1</b>');
    s = s.replace(/(^|[^*\w])\*([^*\n]+)\*(?!\w)/g, '$1<i>$2</i>');
    s = s.replace(/(^|[^_\w])_([^_\n]+)_(?!\w)/g, '$1<i>$2</i>');
    s = s.replace(/~~([^~\n]+)~~/g, '<s>$1</s>');
    s = s.replace(/\u0000(\d+)\u0000/g, (_, i) => `<code>${esc(codes[Number(i)])}</code>`);
    return s;
  }

  function table(lines) {
    const rows = lines.map((l) => l.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim()));
    const head = rows[0];
    const body = rows.slice(2);
    return '<table><thead><tr>' + head.map((c) => `<th>${inline(c)}</th>`).join('') + '</tr></thead><tbody>' +
      body.map((r) => '<tr>' + r.map((c) => `<td>${inline(c)}</td>`).join('') + '</tr>').join('') + '</tbody></table>';
  }

  function blocks(text) {
    const lines = text.split('\n');
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (/^\u0001\d+\u0001$/.test(line.trim())) { out.push(line.trim()); i++; continue; }
      if (!line.trim()) { i++; continue; }
      const h = line.match(/^(#{1,6})\s+(.*)$/);
      if (h) { const n = Math.min(4, h[1].length + 1); out.push(`<h${n}>${inline(h[2])}</h${n}>`); i++; continue; }
      if (/^\s*([-*_])\s*\1\s*\1[\s\-*_]*$/.test(line)) { out.push('<hr>'); i++; continue; }
      if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
        const tl = [];
        while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) tl.push(lines[i++]);
        out.push(table(tl));
        continue;
      }
      if (/^\s*>/.test(line)) {
        const q = [];
        while (i < lines.length && /^\s*>/.test(lines[i])) q.push(lines[i++].replace(/^\s*>\s?/, ''));
        out.push(`<blockquote>${q.map(inline).join('<br>')}</blockquote>`);
        continue;
      }
      if (/^\s*([-*•+])\s+/.test(line)) {
        const items = [];
        while (i < lines.length && /^\s*([-*•+])\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*([-*•+])\s+/, ''));
        out.push('<ul>' + items.map((x) => `<li>${inline(x)}</li>`).join('') + '</ul>');
        continue;
      }
      if (/^\s*\d+[.)]\s+/.test(line)) {
        const items = [];
        const start = parseInt(line, 10) || 1;
        while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*\d+[.)]\s+/, ''));
        out.push(`<ol start="${start}">` + items.map((x) => `<li>${inline(x)}</li>`).join('') + '</ol>');
        continue;
      }
      const para = [];
      while (i < lines.length && lines[i].trim() && !/^(#{1,6}\s|\s*[-*•+]\s+|\s*\d+[.)]\s+|\s*>|\s*\|)/.test(lines[i]) && !/^\u0001\d+\u0001$/.test(lines[i].trim())) {
        para.push(lines[i++]);
      }
      if (para.length) out.push(`<p>${para.map(inline).join('<br>')}</p>`);
      else { out.push(`<p>${inline(lines[i])}</p>`); i++; }
    }
    return out.join('');
  }

  function render(md) {
    const code = [];
    let text = String(md || '').replace(/\r\n/g, '\n');
    // незакрытый блок кода во время стриминга
    const fences = (text.match(/```/g) || []).length;
    if (fences % 2 === 1) text += '\n```';
    text = text.replace(/```([\w+#.-]*)[^\n]*\n?([\s\S]*?)```/g, (_, lang, body) => {
      code.push({ lang, body: body.replace(/\n$/, '') });
      return `\n\u0001${code.length - 1}\u0001\n`;
    });
    let html = blocks(text);
    html = html.replace(/\u0001(\d+)\u0001/g, (_, i) => {
      const c = code[Number(i)];
      return `<div class="codeblock">${c.lang ? `<span class="lang">${esc(c.lang)}</span>` : ''}` +
        `<button class="copy" data-copy="${i}">Копировать</button><pre><code>${esc(c.body)}</code></pre></div>`;
    });
    return html;
  }

  window.VeraMarkdown = { render };
})();
