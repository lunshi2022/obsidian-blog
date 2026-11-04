#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');

const { renderMarkdown, renderInline, escapeHtml: esc, slugify } = require('./lib/markdown');
const O = require('./lib/obsidian');
const T = require('./lib/templates');

const ROOT = path.resolve(__dirname, '..');
const CONTENT = path.join(ROOT, 'content');
const OUT = path.join(ROOT, 'site');
const ASSETS = path.join(ROOT, 'assets');

const IGNORED_DIRS = new Set(['.obsidian', '.trash', '.git', 'node_modules', '.smart-env']);

/* ------------------------------------------------------------------ 工具 */

function loadConfig() {
  const file = path.join(ROOT, 'site.config.json');
  const fallback = {
    title: '数字花园',
    description: '用 Obsidian 写作，发布为静态站点。',
    author: '',
    siteUrl: '',
    lang: 'zh-CN',
  };
  if (!fs.existsSync(file)) return fallback;
  try {
    return { ...fallback, ...JSON.parse(fs.readFileSync(file, 'utf8')) };
  } catch (err) {
    console.warn('[warn] site.config.json 解析失败，使用默认配置：', err.message);
    return fallback;
  }
}

function walk(dir, base = '') {
  const result = [];
  if (!fs.existsSync(dir)) return result;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith('.') && entry.isDirectory()) continue;
    if (IGNORED_DIRS.has(entry.name)) continue;
    const abs = path.join(dir, entry.name);
    const rel = base ? `${base}/${entry.name}` : entry.name;
    if (entry.isDirectory()) result.push(...walk(abs, rel));
    else result.push({ abs, rel });
  }
  return result;
}

function rmrf(dir) {
  if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
}

function write(relPath, content) {
  const abs = path.join(OUT, relPath);
  fs.mkdirSync(path.dirname(abs), { recursive: true });
  fs.writeFileSync(abs, content, 'utf8');
}

function copyDir(src, dest) {
  if (!fs.existsSync(src)) return;
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, entry.name);
    const d = path.join(dest, entry.name);
    if (entry.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

function stripHtml(html) {
  return html
    .replace(/<div class="code-block">[\s\S]*?<\/div>/g, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/\s+/g, ' ')
    .trim();
}

function fmtDate(d) {
  if (!d) return '';
  const date = d instanceof Date ? d : new Date(d);
  if (Number.isNaN(date.getTime())) return String(d);
  const p = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}`;
}

/** 粗略判断正文里有没有数学公式（先剔除代码块与行内代码） */
function hasMath(body) {
  const stripped = String(body)
    .replace(/^ {0,3}(`{3,}|~{3,})[\s\S]*?^ {0,3}\1[ \t]*$/gm, '')
    .replace(/`[^`\n]*`/g, '');
  return /\$\$[\s\S]+?\$\$|\$[^$\n]{1,240}\$/.test(stripped);
}

/* ------------------------------------------------------------------ 读取笔记 */

function buildSite(opts = {}) {
  const config = loadConfig();
  const t0 = Date.now();

  rmrf(OUT);
  fs.mkdirSync(OUT, { recursive: true });

  const files = walk(CONTENT);
  const mdFiles = files.filter((f) => /\.md$/i.test(f.rel));
  const assetFiles = files.filter((f) => !/\.md$/i.test(f.rel));

  const notes = [];
  const attachmentMap = new Map();

  for (const a of assetFiles) {
    attachmentMap.set(a.rel.toLowerCase(), a.rel);
    attachmentMap.set(path.basename(a.rel).toLowerCase(), a.rel);
  }

  for (const f of mdFiles) {
    if (f.rel === 'index.md') continue;
    const raw = fs.readFileSync(f.abs, 'utf8');
    const { data, body } = O.parseFrontmatter(raw);
    if (data.draft === true || data.draft === 'true') continue;

    const baseName = path.basename(f.rel, '.md');
    const folder = path.dirname(f.rel) === '.' ? '' : path.dirname(f.rel);
    let slug = folder ? `${folder}/${baseName}` : baseName;
    if (data.slug) slug = String(data.slug).replace(/^\/|\/$/g, '');

    const titleFromBody = (body.match(/^ {0,3}#[ \t]+(.+)$/m) || [])[1];
    const title = String(data.title || titleFromBody || baseName).replace(/[*_`]/g, '').trim();

    const stat = fs.statSync(f.abs);
    const tags = O.normalizeTags(data.tags);
    const inline = O.collectInlineTags(body);
    for (const t of inline) if (!tags.includes(t)) tags.push(t);

    notes.push({
      abs: f.abs,
      rel: f.rel,
      slug,
      url: `${O.encodePath(slug)}.html`,
      outPath: `${slug}.html`,
      depth: slug.split('/').length - 1,
      title,
      date: fmtDate(data.date || stat.mtime),
      rawDate: data.date,
      mtime: stat.mtimeMs,
      tags,
      aliases: [].concat(data.aliases || data.alias || []).map((a) => String(a).trim()).filter(Boolean),
      description: data.description ? String(data.description) : '',
      body,
      source: raw,
      ext: data.ext || '',
    });
  }

  if (!notes.length) {
    console.warn('[warn] content/ 目录下没有找到任何 .md 笔记');
  }

  /* ------------------------------------------------ 索引与链接解析 */

  const byKey = new Map();
  const byAlias = new Map();
  const register = (key, note) => {
    const k = String(key).toLowerCase().trim();
    if (!k || byKey.has(k)) return;
    byKey.set(k, note);
  };
  for (const n of notes) {
    register(path.basename(n.rel, '.md'), n);
    register(n.slug, n);
    register(n.rel.replace(/\.md$/i, ''), n);
    for (const a of n.aliases) {
      const k = a.toLowerCase();
      if (!byAlias.has(k)) byAlias.set(k, n);
    }
  }

  const findNote = (target) => {
    const k = String(target || '').toLowerCase().trim().replace(/\.md$/i, '');
    return byKey.get(k) || byAlias.get(k) || null;
  };
  const findAsset = (target) => attachmentMap.get(String(target).toLowerCase().replace(/^\.?\//, '')) || null;
  const tagUrlOf = (tag, root) => `${root}tags/${encodeURIComponent(O.tagSlug(tag))}.html`;

  /* ------------------------------------------------ 渲染正文 */

  const incoming = new Map();
  const outgoing = new Map();

  for (const note of notes) {
    const root = '../'.repeat(note.depth);
    note.root = root;

    let body = note.body;
    const h1 = body.match(/^ {0,3}#[ \t]+(.+)$/m);
    if (h1 && h1.index !== undefined && /^ {0,3}#[ \t]/.test(body.slice(0, h1.index).trim() || '#')) {
      const same = h1[1].replace(/[*_`]/g, '').trim() === note.title;
      if (same) body = body.replace(/^ {0,3}#[ \t]+.+\n?/, '');
    }

    const { text, tokens } = O.preprocess(body);

    const ctx = {
      root,
      note,
      ids: new Set(),
      headings: [],
      tagUrl: (tag) => tagUrlOf(tag, root),
      assetUrl: (src) => {
        if (/^(?:[a-z]+:)?\/\//i.test(src) || src.startsWith('data:')) return src;
        const hit = findAsset(src.replace(/^\.\//, ''));
        return hit ? `${root}${O.encodePath(hit)}` : src;
      },
      resolveMdLink: (href) => {
        const target = href.replace(/\.md$/i, '');
        const n = findNote(target);
        return n ? `${root}${n.url}` : href;
      },
    };

    const out = renderMarkdown(text, ctx);

    // 回填双链 / 嵌入
    let html = out.html;
    html = html.replace(/\u0001(\d+)\u0001/g, (m, idx) => {
      const tk = tokens[Number(idx)];
      if (!tk) return m;
      return renderToken(tk, note, root, findNote, findAsset);
    });

    note.html = html;
    note.headings = out.headings;

    // 出链（用于反向链接与关系图）
    const targets = new Set();
    for (const tk of tokens) {
      const n = tk.target ? findNote(tk.target) : null;
      if (n && n.slug !== note.slug) targets.add(n.slug);
    }
    outgoing.set(note.slug, [...targets]);
    for (const s of targets) {
      if (!incoming.has(s)) incoming.set(s, new Set());
      incoming.get(s).add(note.slug);
    }

    // 纯文本 / 字数 / 摘要
    note.plain = stripHtml(html);
    note.words = (note.plain.match(/[\u4e00-\u9fa5]/g) || []).length
      + (note.plain.match(/[A-Za-z0-9]+/g) || []).length;
    note.readingTime = Math.max(1, Math.round(note.words / 350));
    const firstPara = (html.match(/<p>([\s\S]*?)<\/p>/) || [])[1];
    note.excerpt = note.description || (firstPara ? truncate(stripHtml(firstPara), 90) : '');
  }

  /* ------------------------------------------------ 排序与树 */

  notes.sort((a, b) => (b.date || '').localeCompare(a.date || '') || b.mtime - a.mtime);

  const byDate = [...notes];
  const activeSlugs = new Set(notes.map((n) => n.slug));

  function buildTree(list, prefix = '') {
    const folders = new Map();
    const leaves = [];
    for (const n of list) {
      const rel = n.slug;
      const rest = prefix ? rel.slice(prefix.length + 1) : rel;
      const slash = rest.indexOf('/');
      if (slash === -1) leaves.push(n);
      else {
        const name = rest.slice(0, slash);
        if (!folders.has(name)) folders.set(name, []);
        folders.get(name).push(n);
      }
    }
    const out = [];
    for (const name of [...folders.keys()].sort((a, b) => a.localeCompare(b, 'zh'))) {
      const children = buildTree(folders.get(name), prefix ? `${prefix}/${name}` : name);
      out.push({
        type: 'folder',
        name,
        children,
        active: hasActive(children),
      });
    }
    for (const n of leaves.sort((a, b) => a.title.localeCompare(b.title, 'zh'))) {
      out.push({ type: 'note', title: n.title, slug: n.slug, url: n.url });
    }
    return out;
  }

  function hasActive(nodes) {
    for (const node of nodes) {
      if (node.type === 'folder' && (node.active || hasActive(node.children))) return true;
      if (node.type === 'note' && activeSlugs.has(node.slug)) return true;
    }
    return false;
  }

  const tree = buildTree(notes);

  /* ------------------------------------------------ 标签汇总 */

  const tagCount = new Map();
  for (const n of notes) for (const t of n.tags) tagCount.set(t, (tagCount.get(t) || 0) + 1);
  const tagList = [...tagCount.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0], 'zh'));

  const tagLink = (tag) => ({ name: tag, url: encodeURIComponent(O.tagSlug(tag)) });
  for (const n of notes) n.tagLinks = n.tags.map(tagLink);

  /* ------------------------------------------------ 写笔记页 */

  const backlinkOf = (slug) => {
    const set = incoming.get(slug);
    if (!set) return [];
    return [...set].map((s) => notes.find((n) => n.slug === s)).filter(Boolean)
      .map((n) => ({ title: n.title, url: n.url, context: '' }));
  };

  for (const note of notes) {
    const rail = T.toc(note.headings) + renderRailMeta(note, notes, incoming, backlinkOf);
    const body = `<article class="note">
  ${T.noteHeader({ ...note, tags: note.tagLinks })}
  <div class="prose">${note.html}</div>
  ${T.backlinks(backlinkOf(note.slug), note.root)}
  ${siblings(note, notes, note.root)}
</article>`;

    const page = T.shell({
      site: config,
      root: note.root,
      title: note.title,
      description: note.excerpt,
      pageType: 'note',
      body,
      activeSlug: note.slug,
      tree,
      rail,
      math: hasMath(note.body),
    });
    write(note.outPath, page);
  }

  /* ------------------------------------------------ 首页 */

  let introHtml = '';
  const indexPath = path.join(CONTENT, 'index.md');
  if (fs.existsSync(indexPath)) {
    const raw = fs.readFileSync(indexPath, 'utf8');
    const { body } = O.parseFrontmatter(raw);
    const { text, tokens } = O.preprocess(body);
    const ctx = {
      root: '',
      note: { slug: 'index', title: config.title },
      ids: new Set(),
      headings: [],
      tagUrl: (tag) => tagUrlOf(tag, ''),
      assetUrl: (src) => {
        const hit = findAsset(src.replace(/^\.\//, ''));
        return hit ? O.encodePath(hit) : src;
      },
      resolveMdLink: (href) => {
        const n = findNote(href.replace(/\.md$/i, ''));
        return n ? n.url : href;
      },
    };
    const out = renderMarkdown(text, ctx);
    introHtml = out.html.replace(/\u0001(\d+)\u0001/g, (m, idx) => {
      const tk = tokens[Number(idx)];
      return tk ? renderToken(tk, ctx.note, '', findNote, findAsset) : m;
    });
  }

  const recent = byDate.slice(0, 8);
  const linkCount = [...outgoing.values()].reduce((s, arr) => s + arr.length, 0);

  const homeBody = `<section class="hero">
  <h1 class="hero-title">${esc(config.title)}</h1>
  ${config.description ? `<p class="hero-sub">${esc(config.description)}</p>` : ''}
  <div class="hero-stats">
    <span><b>${notes.length}</b> 篇笔记</span>
    <span><b>${tagList.length}</b> 个标签</span>
    <span><b>${linkCount}</b> 条双链</span>
  </div>
</section>
${introHtml ? `<div class="prose home-intro">${introHtml}</div>` : ''}
${notes.length && linkCount ? `<div class="home-graph">${T.graphBlock('', 300)}</div>` : ''}
<div class="home-grid">
  <div class="home-main">
    <section>
      <h2 class="section-title">最近更新</h2>
      <div class="note-cards">
        ${recent.map((n) => T.noteCard({ ...n, url: n.url, tags: n.tagLinks })).join('') || '<p class="empty-hint">还没有笔记，往 content/ 里丢几个 .md 文件试试。</p>'}
      </div>
      <a class="more-link" href="notes.html">查看全部 ${notes.length} 篇 →</a>
    </section>
  </div>
  <div class="home-side">
    ${tagList.length ? `<div class="rail-block"><div class="rail-title">标签</div>
      <div class="tag-cloud">${tagList.slice(0, 24).map(([t, c]) => `<a class="tag" href="tags/${encodeURIComponent(O.tagSlug(t))}.html">#${esc(t)}<span class="tag-n">${c}</span></a>`).join('')}</div>
    </div>` : ''}
  </div>
</div>`;

  write('index.html', T.shell({
    site: config, root: '', title: '', description: config.description,
    pageType: 'home', body: homeBody, activeSlug: 'home', tree,
    graph: notes.length > 0 && linkCount > 0,
  }));

  /* ------------------------------------------------ 全部笔记 */

  const grouped = new Map();
  for (const n of byDate) {
    const folder = path.dirname(n.slug) === '.' ? '根目录' : path.dirname(n.slug);
    if (!grouped.has(folder)) grouped.set(folder, []);
    grouped.get(folder).push(n);
  }
  const notesBody = `<header class="page-header">
  <h1>全部笔记</h1>
  <p class="page-sub">共 ${notes.length} 篇</p>
</header>
${[...grouped.entries()].map(([folder, list]) => `<section class="group">
  <h2 class="section-title">${esc(folder)} <span class="count">${list.length}</span></h2>
  <ul class="note-list">${list.map((n) => `<li>
    <a href="${n.url}">${esc(n.title)}</a>
    <time>${esc(n.date)}</time>
  </li>`).join('')}</ul>
</section>`).join('')}`;

  write('notes.html', T.shell({
    site: config, root: '', title: '全部笔记', description: `共 ${notes.length} 篇笔记`,
    pageType: 'list', body: notesBody, activeSlug: 'notes', tree,
  }));

  /* ------------------------------------------------ 标签页 */

  const tagsBody = `<header class="page-header">
  <h1>标签</h1>
  <p class="page-sub">共 ${tagList.length} 个</p>
</header>
<div class="tag-cloud large">${tagList.map(([t, c]) => `<a class="tag" href="tags/${encodeURIComponent(O.tagSlug(t))}.html">#${esc(t)}<span class="tag-n">${c}</span></a>`).join('') || '<p class="empty-hint">还没有标签</p>'}</div>`;

  write('tags.html', T.shell({
    site: config, root: '', title: '标签', pageType: 'list', body: tagsBody, activeSlug: 'tags', tree,
  }));

  for (const [tag, count] of tagList) {
    const list = byDate.filter((n) => n.tags.includes(tag));
    const body = `<header class="page-header">
  <p class="crumb"><a href="../tags.html">标签</a> /</p>
  <h1>#${esc(tag)}</h1>
  <p class="page-sub">${count} 篇笔记</p>
</header>
<ul class="note-list">${list.map((n) => `<li>
  <a href="../${n.url}">${esc(n.title)}</a>
  <time>${esc(n.date)}</time>
</li>`).join('')}</ul>`;
    write(`tags/${O.tagSlug(tag)}.html`, T.shell({
      site: config, root: '../', title: `#${tag}`, pageType: 'list',
      body, activeSlug: 'tags', tree,
    }));
  }

  /* ------------------------------------------------ 关系图页 */

  const graphBody = `<header class="page-header">
  <h1>关系图</h1>
  <p class="page-sub">${notes.length} 个节点 · ${linkCount} 条连线</p>
</header>
${T.graphBlock('', 560)}`;

  write('graph.html', T.shell({
    site: config, root: '', title: '关系图', pageType: 'graph',
    body: graphBody, activeSlug: 'graph', tree, graph: true,
  }));

  /* ------------------------------------------------ 404 */

  write('404.html', T.shell({
    site: config, root: '/', title: '页面不存在', pageType: 'list',
    body: `<div class="notfound">
  <h1>404</h1>
  <p>这一页还没有被创建，或者链接已经失效。</p>
  <p><a class="btn" href="/index.html">回到首页</a></p>
</div>`,
    activeSlug: 'home', tree,
  }));

  /* ------------------------------------------------ 搜索索引 / 图数据 */

  const searchData = byDate.map((n) => ({
    t: n.title,
    u: n.url,
    g: n.tags,
    d: n.date,
    x: n.excerpt,
    c: n.plain.slice(0, 5000),
  }));

  const graphNodes = byDate.map((n) => ({
    id: n.slug,
    t: n.title,
    u: n.url,
  }));
  const graphLinks = [];
  for (const [src, targets] of outgoing) {
    for (const t of targets) graphLinks.push({ s: src, t });
  }

  write('assets/search-data.js', `window.__SEARCH__=${JSON.stringify(searchData)};`);
  write('assets/graph-data.js', `window.__GRAPH__=${JSON.stringify({ nodes: graphNodes, links: graphLinks })};`);

  /* ------------------------------------------------ Feed */

  const siteUrl = (config.siteUrl || '').replace(/\/$/, '');
  if (siteUrl) {
    const items = byDate.slice(0, 20).map((n) => `    <item>
      <title>${esc(n.title)}</title>
      <link>${siteUrl}/${n.url}</link>
      <guid>${siteUrl}/${n.url}</guid>
      <pubDate>${new Date(n.date).toUTCString()}</pubDate>
      <description>${esc(n.excerpt)}</description>
    </item>`).join('\n');
    write('feed.xml', `<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0"><channel>
    <title>${esc(config.title)}</title>
    <link>${siteUrl}/</link>
    <description>${esc(config.description)}</description>
    <language>zh-cn</language>
${items}
</channel></rss>`);
  } else {
    write('feed.xml', `<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0"><channel>
    <title>${esc(config.title)}</title>
    <description>${esc(config.description)}</description>
    <language>zh-cn</language>
</channel></rss>`);
    console.log('[hint] 未配置 siteUrl，RSS 中没有绝对链接。在 site.config.json 里填上站点地址即可。');
  }

  write('.nojekyll', '');

  /* ------------------------------------------------ 静态资源与附件 */

  copyDir(ASSETS, path.join(OUT, 'assets'));
  for (const a of assetFiles) {
    const dest = path.join(OUT, a.rel);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.copyFileSync(a.abs, dest);
  }

  const ms = Date.now() - t0;
  console.log(`✓ 构建完成：${notes.length} 篇笔记 / ${tagList.length} 个标签 / ${linkCount} 条双链，用时 ${ms}ms`);
  console.log(`  输出目录：${path.relative(ROOT, OUT)}`);
  return { notes, config };
}

/* ------------------------------------------------------------------ 片段渲染 */

function truncate(s, n) {
  const str = String(s || '');
  return str.length > n ? `${str.slice(0, n)}…` : str;
}

function renderToken(tk, note, root, findNote, findAsset) {
  const target = tk.target || '';
  const ext = O.extOf(target);

  if (tk.embed) {
    const label = tk.alias || target;
    if (!target) {
      return `<a class="wikilink" href="${root}${note.url || ''}#${slugify(tk.heading || '')}">${esc(tk.heading || '本页')}</a>`;
    }
    if (O.IMAGE_EXT.has(ext)) {
      const hit = findAsset(target);
      if (!hit) return brokenRef(tk.raw);
      const cap = tk.alias ? `<figcaption>${esc(tk.alias)}</figcaption>` : '';
      return `<figure class="embed-image"><img src="${root}${O.encodePath(hit)}" alt="${esc(label)}" loading="lazy">${cap}</figure>`;
    }
    if (O.VIDEO_EXT.has(ext)) {
      const hit = findAsset(target);
      if (!hit) return brokenRef(tk.raw);
      return `<figure class="embed-video"><video controls preload="metadata" src="${root}${O.encodePath(hit)}"></video></figure>`;
    }
    if (O.AUDIO_EXT.has(ext)) {
      const hit = findAsset(target);
      if (!hit) return brokenRef(tk.raw);
      return `<div class="embed-audio"><audio controls preload="metadata" src="${root}${O.encodePath(hit)}"></audio></div>`;
    }
    if (ext === 'pdf') {
      const hit = findAsset(target);
      if (!hit) return brokenRef(tk.raw);
      return `<div class="embed-pdf"><iframe src="${root}${O.encodePath(hit)}" loading="lazy" title="${esc(label)}"></iframe></div>`;
    }
    const n = findNote(target);
    if (n) {
      const excerpt = n.excerpt ? `<span class="embed-excerpt">${esc(n.excerpt)}</span>` : '';
      return `<a class="embed-note" href="${root}${n.url}">
        <span class="embed-note-label">嵌入笔记</span>
        <span class="embed-note-title">${esc(n.title)}</span>
        ${excerpt}
      </a>`;
    }
    return brokenRef(tk.raw);
  }

  if (ext && O.FILE_EXT.has(ext) && !target.toLowerCase().endsWith('.md')) {
    const hit = findAsset(target);
    if (!hit) return brokenRef(tk.raw);
    return `<a class="wikilink file" href="${root}${O.encodePath(hit)}" download>${esc(tk.alias || target)}</a>`;
  }

  if (!target) {
    const anchor = tk.heading ? `#${slugify(tk.heading)}` : '';
    return `<a class="wikilink" href="${root}${note.url || ''}${anchor}">${esc(tk.alias || tk.heading || '本页')}</a>`;
  }

  const n = findNote(target);
  if (n) {
    const anchor = tk.heading ? `#${slugify(tk.heading)}` : '';
    const label = tk.alias || (tk.heading ? `${n.title} › ${tk.heading}` : n.title);
    return `<a class="wikilink" href="${root}${n.url}${anchor}">${esc(label)}</a>`;
  }

  const label = tk.alias || target;
  return `<span class="wikilink broken" title="这篇笔记还没有创建">${esc(label)}</span>`;
}

function brokenRef(raw) {
  return `<span class="wikilink broken" title="找不到目标文件">${esc(raw)}</span>`;
}

function renderRailMeta(note, notes, incoming, backlinkOf) {
  const links = (incoming.get(note.slug) ? [...incoming.get(note.slug)].length : 0);
  return `<div class="rail-block rail-meta">
    <div class="rail-title">这篇笔记</div>
    <div class="meta-row"><span>入链</span><b>${links}</b></div>
    <div class="meta-row"><span>字数</span><b>${note.words}</b></div>
  </div>`;
}

function siblings(note, notes, root) {
  const sameFolder = notes.filter((n) => n.slug !== note.slug
    && path.dirname(n.slug) === path.dirname(note.slug));
  if (sameFolder.length < 1) return '';
  const idx = sameFolder.findIndex((n) => n.mtime < note.mtime);
  const pick = (sameFolder.slice(Math.max(0, idx - 1), idx + 2)).filter(Boolean).slice(0, 2);
  if (!pick.length) return '';
  return `<nav class="related">
    <h2 class="section-title">同目录</h2>
    <div class="related-list">${pick.map((n) => `<a class="related-card" href="${root}${n.url}">
      <span>${esc(n.title)}</span>
      <small>${esc(n.excerpt || '')}</small>
    </a>`).join('')}</div>
  </nav>`;
}

/* ------------------------------------------------------------------ 开发服务器 */

function serve(port) {
  const server = http.createServer((req, res) => {
    let urlPath = decodeURIComponent(req.url.split('?')[0]);
    if (urlPath === '/') urlPath = '/index.html';
    let file = path.join(OUT, urlPath);
    if (fs.existsSync(file) && fs.statSync(file).isDirectory()) file = path.join(file, 'index.html');
    if (!fs.existsSync(file)) {
      const fallback = path.join(OUT, '404.html');
      res.writeHead(404, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(fs.existsSync(fallback) ? fs.readFileSync(fallback) : 'Not Found');
      return;
    }
    const types = {
      '.html': 'text/html; charset=utf-8',
      '.css': 'text/css; charset=utf-8',
      '.js': 'text/javascript; charset=utf-8',
      '.json': 'application/json; charset=utf-8',
      '.svg': 'image/svg+xml',
      '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg',
      '.gif': 'image/gif', '.webp': 'image/webp',
      '.xml': 'application/xml; charset=utf-8',
      '.woff2': 'font/woff2',
    };
    res.writeHead(200, { 'Content-Type': types[path.extname(file).toLowerCase()] || 'application/octet-stream' });
    res.end(fs.readFileSync(file));
  });
  server.on('error', (e) => {
    if (e && e.code === 'EADDRINUSE') {
      console.error(`\n  端口 ${port} 已被占用。换一个端口试试：node src/build.js --serve --port=${port + 1}\n`);
      process.exit(1);
    }
    console.error('\n  预览服务出错：', e && e.message ? e.message : e, '\n');
    process.exit(1);
  });

  server.listen(port, () => {
    console.log(`\n  本地预览： http://localhost:${port}\n  按 Ctrl+C 停止\n`);
  });
}

/* ------------------------------------------------------------------ 入口 */

if (require.main === module) {
  const args = process.argv.slice(2);
  const portArg = args.find((a) => a.startsWith('--port='));
  const port = portArg ? Number(portArg.split('=')[1]) : 4321;
  buildSite();
  if (args.includes('--serve')) {
    serve(port);
  } else if (!args.includes('--no-watch')) {
    // 无 --serve 时仅构建
  }
}

module.exports = { buildSite };
