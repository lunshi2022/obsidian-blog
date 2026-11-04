'use strict';

const { escapeHtml: esc } = require('./markdown');

const ICON_SEARCH = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.6-3.6"/></svg>';
const ICON_THEME = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2.6v2M12 19.4v2M2.6 12h2M19.4 12h2M5.4 5.4l1.4 1.4M17.2 17.2l1.4 1.4M18.6 5.4l-1.4 1.4M6.8 17.2l-1.4 1.4"/></svg>';
const ICON_MENU = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M3 6h18M3 12h18M3 18h18"/></svg>';
const ICON_GRAPH = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.6"><circle cx="6" cy="7" r="2.4"/><circle cx="18" cy="9" r="2.4"/><circle cx="11" cy="17" r="2.4"/><path d="M8.2 8.2 15.7 8.9M7.2 9.2l2.7 5.7M16.2 11.2l-3.6 4"/></svg>';

/* ------------------------------------------------------------------ 侧边目录 */

function renderTree(nodes, root, activeSlug, depth) {
  const pad = depth * 12;
  let html = '';
  for (const node of nodes) {
    if (node.type === 'folder') {
      const hasActive = node.active;
      html += `<details class="tree-folder"${hasActive ? ' open' : ''}>`
        + `<summary style="padding-left:${pad}px">${esc(node.name)}</summary>`
        + `<div class="tree-children">${renderTree(node.children, root, activeSlug, depth + 1)}</div>`
        + '</details>';
    } else {
      const active = node.slug === activeSlug ? ' class="active"' : '';
      html += `<a class="tree-note" style="padding-left:${pad + 10}px" href="${root}${node.url}"${active}>${esc(node.title)}</a>`;
    }
  }
  return html;
}

function sidebar(tree, root, activeSlug, site) {
  const nav = [
    ['index.html', '首页', 'home'],
    ['notes.html', '全部笔记', 'notes'],
    ['tags.html', '标签', 'tags'],
    ['graph.html', '关系图', 'graph'],
  ].map(([href, label, key]) => {
    const active = key === activeSlug ? ' class="active"' : '';
    return `<a class="nav-link"${active} href="${root}${href}">${label}</a>`;
  }).join('');

  return `<div class="sidebar-inner">
  <nav class="nav">${nav}</nav>
  <div class="tree">
    <div class="tree-title">笔记 <span class="count">${countNotes(tree)}</span></div>
    ${renderTree(tree, root, activeSlug, 0) || '<p class="empty-hint">content/ 里还没有笔记</p>'}
  </div>
</div>`;
}

function countNotes(nodes) {
  let n = 0;
  for (const node of nodes) {
    if (node.type === 'folder') n += countNotes(node.children);
    else n += 1;
  }
  return n;
}

/* ------------------------------------------------------------------ 页面外壳 */

function shell(opts) {
  const { site, root, title, description, pageType, body, activeSlug, tree, rail, graph, math } = opts;
  const fullTitle = title ? `${esc(title)} · ${esc(site.title)}` : esc(site.title);
  const desc = description || site.description || '';
  const railHtml = rail
    ? `<aside class="rail" id="rail">${rail}</aside>`
    : '';
  const shellClass = rail ? 'shell has-rail' : 'shell';

  // 只有正文里真的出现了 $ 公式时才引入 KaTeX，普通笔记不加载任何外部资源
  const mathTags = math ? `
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css">
<script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js"></script>
<script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/contrib/auto-render.min.js"></script>
<script>
document.addEventListener('DOMContentLoaded', function () {
  if (!window.renderMathInElement) return;
  renderMathInElement(document.querySelector('.prose') || document.body, {
    delimiters: [{ left: '$$', right: '$$', display: true }, { left: '$', right: '$', display: false }],
    throwOnError: false,
    ignoredTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code']
  });
});
</script>` : '';

  return `<!DOCTYPE html>
<html lang="${site.lang || 'zh-CN'}" data-theme="light">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${fullTitle}</title>
<meta name="description" content="${esc(desc)}">
<meta property="og:title" content="${fullTitle}">
<meta property="og:description" content="${esc(desc)}">
<meta property="og:type" content="${pageType === 'note' ? 'article' : 'website'}">
<link rel="stylesheet" href="${root}assets/style.css">${site.themeCss || ''}
<link rel="icon" href="${root}assets/favicon.svg" type="image/svg+xml">${mathTags}
</head>
<body data-root="${root}" data-page="${pageType}">
<header class="topbar">
  <div class="topbar-inner">
    <button class="icon-btn only-mobile" data-menu aria-label="打开目录">${ICON_MENU}</button>
    <a class="brand" href="${root}index.html">
      <span class="brand-mark">${esc(site.mark || '◈')}</span>
      <span class="brand-text">${esc(site.title)}</span>
    </a>
    <div class="topbar-actions">
      <button class="search-trigger" data-search-open>${ICON_SEARCH}<span>搜索</span><kbd>/</kbd></button>
      <button class="icon-btn" data-theme-toggle aria-label="切换深浅色">${ICON_THEME}</button>
    </div>
  </div>
</header>
<div class="${shellClass}">
  <aside class="sidebar" id="sidebar">${sidebar(tree, root, activeSlug, site)}</aside>
  <div class="scrim" data-menu-close hidden></div>
  <main class="content" id="content">${body}</main>
  ${railHtml}
</div>
<footer class="site-footer">
  <span>© ${new Date().getFullYear()} ${esc(site.author || site.title)}</span>
  <span class="foot-sep">·</span>
  <span><a href="https://obsidian.md" target="_blank" rel="noopener">用 Obsidian 写作</a>${site.repo ? ' · <a href="' + esc(site.repo) + '" target="_blank" rel="noopener">GitHub</a>' : ''} · <a href="${root}feed.xml">RSS</a></span>
</footer>

<div class="search-modal" id="search-modal" hidden>
  <div class="search-backdrop" data-search-close></div>
  <div class="search-panel" role="dialog" aria-modal="true" aria-label="搜索">
    <div class="search-field">
      ${ICON_SEARCH}
      <input type="search" id="search-input" placeholder="搜索标题、正文、标签…" autocomplete="off" spellcheck="false">
      <kbd>ESC</kbd>
    </div>
    <ul class="search-results" id="search-results"></ul>
    <div class="search-hint"><span><kbd>↑</kbd><kbd>↓</kbd> 选择</span><span><kbd>↵</kbd> 打开</span><span id="search-count"></span></div>
  </div>
</div>

<script>window.__SITE__=${JSON.stringify({ root })};</script>
${graph ? `<script src="${root}assets/graph-data.js"></script>` : ''}
<script src="${root}assets/app.js" defer></script>
</body>
</html>`;
}

/* ------------------------------------------------------------------ 组件 */

function noteHeader(note) {
  const tags = note.tags.length
    ? `<div class="tag-row">${note.tags.map((t) => `<a class="tag" href="${note.root}tags/${t.url}.html">#${esc(t.name)}</a>`).join('')}</div>`
    : '';
  const meta = [];
  if (note.date) meta.push(`<time datetime="${esc(note.date)}">${esc(note.date)}</time>`);
  if (note.readingTime) meta.push(`<span>${note.readingTime} 分钟阅读</span>`);
  if (note.words) meta.push(`<span>${note.words} 字</span>`);
  return `<header class="note-header">
    <h1 class="note-title">${esc(note.title)}</h1>
    ${meta.length ? `<div class="note-meta">${meta.join('<span class="dot">·</span>')}</div>` : ''}
    ${tags}
  </header>`;
}

function backlinks(list, root) {
  if (!list.length) return '';
  const items = list.map((n) => `<li><a href="${root}${n.url}">${esc(n.title)}</a>`
    + (n.context ? `<span class="ctx">${esc(n.context)}</span>` : '') + '</li>').join('');
  return `<section class="backlinks">
    <h2 class="section-title">反向链接 <span class="count">${list.length}</span></h2>
    <ul class="backlink-list">${items}</ul>
  </section>`;
}

function toc(headings) {
  const items = headings.filter((h) => h.level >= 2 && h.level <= 3);
  if (items.length < 2) return '';
  const lis = items.map((h) => `<li class="toc-l${h.level}"><a href="#${h.id}">${esc(h.text)}</a></li>`).join('');
  return `<div class="rail-block"><div class="rail-title">目录</div><nav class="toc" id="toc">${lis}</nav></div>`;
}

function noteCard(n) {
  return `<a class="note-card" href="${n.url}">
    <div class="card-top">
      <span class="card-title">${esc(n.title)}</span>
      ${n.date ? `<time>${esc(n.date)}</time>` : ''}
    </div>
    ${n.excerpt ? `<p class="card-excerpt">${esc(n.excerpt)}</p>` : ''}
    ${n.tags.length ? `<div class="card-tags">${n.tags.map((t) => `#${esc(t.name)}`).join(' ')}</div>` : ''}
  </a>`;
}

function graphBlock(root, height) {
  return `<div class="graph-wrap">
    <div class="graph-head">
      <span class="graph-label">${ICON_GRAPH} 关系图</span>
      <span class="graph-hint">拖动节点 · 点击跳转</span>
    </div>
    <canvas class="graph-canvas" data-graph height="${height || 320}"></canvas>
  </div>`;
}

module.exports = {
  shell, sidebar, noteHeader, backlinks, toc, noteCard, graphBlock, ICON_GRAPH,
};
