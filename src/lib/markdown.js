'use strict';

/**
 * 零依赖 Markdown 解析器，针对 Obsidian 语法做了适配：
 * - 标题自动生成锚点 id
 * - 段落内换行按 Obsidian 默认行为渲染为 <br>
 * - 支持 ==高亮==、~~删除线~~、任务列表、表格、Callout
 * - [[双链]] / ![[嵌入]] 由预处理阶段换成 \u0001N\u0001 占位符，最后统一回填
 */

const RE_FENCE = /^ {0,3}(`{3,}|~{3,})[ \t]*([^`\s]*)[ \t]*$/;
const RE_HEADING = /^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$/;
const RE_HR = /^ {0,3}(?:(?:-[ \t]*){3,}|(?:\*[ \t]*){3,}|(?:_[ \t]*){3,})$/;
const RE_QUOTE = /^ {0,3}>[ \t]?/;
const RE_LIST = /^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+([\s\S]*)$/;
const RE_CALLOUT = /^\[!([\w-]+)\]([+-]?)[ \t]*(.*)$/;

const CALLOUT_META = {
  note: ['📝', '笔记'], info: ['ℹ️', '信息'], abstract: ['📄', '摘要'], summary: ['📄', '摘要'],
  tip: ['💡', '提示'], hint: ['💡', '提示'], important: ['❗', '重要'],
  success: ['✅', '完成'], check: ['✅', '完成'], done: ['✅', '完成'],
  question: ['❓', '疑问'], help: ['❓', '疑问'], faq: ['❓', '常见问题'],
  warning: ['⚠️', '注意'], caution: ['⚠️', '注意'], attention: ['⚠️', '注意'],
  failure: ['❌', '失败'], fail: ['❌', '失败'], missing: ['❌', '缺失'],
  danger: ['🔥', '危险'], error: ['⛔', '错误'], bug: ['🐛', '问题'],
  example: ['🧪', '示例'], quote: ['❝', '引用'], cite: ['❝', '引用'],
  todo: ['☑️', '待办'],
};

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** 去掉行内标记，用于生成锚点与标题纯文本 */
function stripMarks(s) {
  return String(s)
    .replace(/!?\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g, (m, a, b) => b || a)
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_~`=]/g, '')
    .trim();
}

function slugify(text) {
  const base = stripMarks(text)
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
  return base;
}

function uniqueId(set, base) {
  const root = base || 'section';
  let id = root;
  let n = 1;
  while (set.has(id)) {
    n += 1;
    id = `${root}-${n}`;
  }
  set.add(id);
  return id;
}

function isTableSep(line) {
  if (!line || !line.includes('|')) return false;
  return /^[ \t]*\|?[ \t]*:?-{1,}:?[ \t]*(\|[ \t]*:?-{1,}:?[ \t]*)*\|?[ \t]*$/.test(line);
}

function splitRow(line) {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|')) s = s.slice(0, -1);
  const cells = [];
  let cur = '';
  for (let i = 0; i < s.length; i += 1) {
    const ch = s[i];
    if (ch === '\\' && s[i + 1] === '|') { cur += '|'; i += 1; continue; }
    if (ch === '|') { cells.push(cur.trim()); cur = ''; continue; }
    cur += ch;
  }
  cells.push(cur.trim());
  return cells;
}

/* ------------------------------------------------------------------ inline */

function renderInline(text, ctx = {}) {
  const stash = [];
  const keep = (html) => {
    stash.push(html);
    return `\u0000${stash.length - 1}\u0000`;
  };

  let s = escapeHtml(text);

  // 行内代码
  s = s.replace(/(`+)([\s\S]*?)\1/g, (m, ticks, code) => keep(`<code>${code.trim()}</code>`));

  // 图片 ![alt](src)
  s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;([^&]*)&quot;)?\)/g, (m, alt, src, title) => {
    const href = (ctx.assetUrl ? ctx.assetUrl(src) : src);
    const t = title ? ` title="${title}"` : '';
    return keep(`<img src="${href}" alt="${alt}"${t} loading="lazy">`);
  });

  // 链接 [text](href)
  s = s.replace(/\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;([^&]*)&quot;)?\)/g, (m, label, href) => {
    let url = href;
    if (/\.md$/i.test(url) && ctx.resolveMdLink) {
      url = ctx.resolveMdLink(url);
    } else if (ctx.assetUrl) {
      url = ctx.assetUrl(url);
    }
    const external = /^[a-z]+:\/\//i.test(url);
    const attrs = external ? ' target="_blank" rel="noopener noreferrer"' : '';
    return keep(`<a href="${url}"${attrs}>${label}</a>`);
  });

  // 裸链接
  s = s.replace(/(^|[\s(（])(https?:\/\/[^\s<>"']+)/g, (m, pre, url) => {
    const trimmed = url.replace(/[),.!?:;，。！？]+$/, '');
    const tail = url.slice(trimmed.length);
    return `${pre}${keep(`<a href="${trimmed}" target="_blank" rel="noopener noreferrer">${trimmed}</a>`)}${tail}`;
  });

  // 粗斜体 / 加粗 / 斜体 / 删除线 / 高亮（三连星必须排在双星之前）
  s = s.replace(/\*\*\*(?=\S)([\s\S]*?\S)\*\*\*/g, (m, c) => keep(`<strong><em>${c}</em></strong>`));
  s = s.replace(/(\*\*|__)(?=\S)([\s\S]*?\S)\1/g, (m, t, c) => keep(`<strong>${c}</strong>`));
  s = s.replace(/(\*|_)(?=\S)([\s\S]*?\S)\1/g, (m, t, c) => keep(`<em>${c}</em>`));
  s = s.replace(/~~(?=\S)([\s\S]*?\S)~~/g, (m, c) => keep(`<del>${c}</del>`));
  s = s.replace(/==(?=\S)([\s\S]*?\S)==/g, (m, c) => keep(`<mark>${c}</mark>`));

  // 行内 #标签
  if (!ctx.noTags && ctx.tagUrl) {
    s = s.replace(/(^|[\s(（>])#([\p{L}\p{N}][\p{L}\p{N}\/_–-]*)/gu, (m, pre, tag) => {
      if (/^\d+$/.test(tag)) return m;
      return `${pre}${keep(`<a class="tag-inline" href="${ctx.tagUrl(tag)}">#${tag}</a>`)}`;
    });
  }

  // 回填（可能有嵌套，循环到稳定为止）
  for (let i = 0; i < 12 && s.includes('\u0000'); i += 1) {
    s = s.replace(/\u0000(\d+)\u0000/g, (m, idx) => (stash[Number(idx)] !== undefined ? stash[Number(idx)] : m));
  }
  return s;
}

/* ------------------------------------------------------------------ blocks */

function indentOf(line) {
  return line.replace(/\t/g, '    ').match(/^ */)[0].length;
}

function startsBlock(line, lines, i) {
  return (
    RE_FENCE.test(line) ||
    RE_HEADING.test(line) ||
    RE_HR.test(line) ||
    RE_QUOTE.test(line) ||
    RE_LIST.test(line) ||
    isTableSep(lines[i + 1] || '') && line.includes('|')
  );
}

function renderCallout(type, open, title, bodyLines, ctx) {
  const key = String(type).toLowerCase();
  const meta = CALLOUT_META[key] || CALLOUT_ICONS_FALLBACK(key);
  const heading = title && title.trim()
    ? renderInline(title.trim(), { ...ctx, noTags: true })
    : meta[1];
  const body = renderBlocks(bodyLines, ctx);
  const collapsible = open !== '';
  const expanded = open !== '-';
  if (collapsible) {
    return `<details class="callout callout-${key}"${expanded ? ' open' : ''}>`
      + `<summary class="callout-title"><span class="callout-icon">${meta[0]}</span><span>${heading}</span></summary>`
      + `<div class="callout-body">${body || '<p></p>'}</div></details>`;
  }
  return `<div class="callout callout-${key}">`
    + `<div class="callout-title"><span class="callout-icon">${meta[0]}</span><span>${heading}</span></div>`
    + `<div class="callout-body">${body || '<p></p>'}</div></div>`;
}

function CALLOUT_ICONS_FALLBACK(key) {
  return ['📌', key.replace(/-/g, ' ')];
}

function renderHeading(level, rawText, ctx) {
  const text = rawText.trim();
  const id = uniqueId(ctx.ids, slugify(text));
  if (level <= 4 && ctx.headings) {
    ctx.headings.push({ level, id, text: stripMarks(text) });
  }
  const inner = renderInline(text, { ...ctx, noTags: true });
  return `<h${level} id="${id}"><a class="anchor" href="#${id}" aria-hidden="true">#</a>${inner}</h${level}>`;
}

function buildListTree(items) {
  const root = { indent: -1, children: [] };
  const stack = [root];
  for (const it of items) {
    while (stack.length > 1 && it.indent <= stack[stack.length - 1].indent) stack.pop();
    const node = { ...it, children: [] };
    stack[stack.length - 1].children.push(node);
    stack.push(node);
  }
  return root.children;
}

function renderListNodes(nodes, ctx, ordered, depth) {
  const tag = ordered ? 'ol' : 'ul';
  const startAttr = ordered && nodes.length && nodes[0].num && nodes[0].num !== 1
    ? ` start="${nodes[0].num}"` : '';
  const body = nodes.map((node) => {
    let text = node.text.join('\n');
    let checkbox = '';
    const tm = text.match(/^\[([ xX])\][ \t]+([\s\S]*)$/);
    if (tm) {
      checkbox = `<input type="checkbox" disabled${tm[1].toLowerCase() === 'x' ? ' checked' : ''}> `;
      text = tm[2];
    }
    let inner;
    if (node.text.length > 1) {
      inner = checkbox + renderBlocks(node.text, ctx);
    } else {
      inner = checkbox + renderInline(text, ctx);
    }
    const sub = node.children.length
      ? renderListNodes(node.children, ctx, node.children[0].ordered, depth + 1)
      : '';
    const cls = tm ? ' class="task-item"' : '';
    return `<li${cls}>${inner}${sub}</li>`;
  }).join('');
  return `<${tag}${startAttr}>${body}</${tag}>`;
}

function renderBlocks(lines, ctx) {
  let out = '';
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (!line.trim()) { i += 1; continue; }

    // 围栏代码块
    const fm = line.match(RE_FENCE);
    if (fm) {
      const fence = fm[1][0];
      const len = fm[1].length;
      const lang = fm[2] || '';
      const buf = [];
      i += 1;
      while (i < lines.length) {
        const close = lines[i].match(/^ {0,3}(`{3,}|~{3,})[ \t]*$/);
        if (close && close[1][0] === fence && close[1].length >= len) { i += 1; break; }
        buf.push(lines[i]);
        i += 1;
      }
      const langAttr = lang ? ` class="language-${escapeHtml(lang)}"` : '';
      const label = lang ? `<span class="code-lang">${escapeHtml(lang)}</span>` : '';
      out += `<div class="code-block"><button class="copy-btn" data-copy>复制</button>${label}`
        + `<pre><code${langAttr}>${escapeHtml(buf.join('\n'))}</code></pre></div>`;
      continue;
    }

    // 标题
    const hm = line.match(RE_HEADING);
    if (hm) {
      out += renderHeading(hm[1].length, hm[2], ctx);
      i += 1;
      continue;
    }

    // 分隔线
    if (RE_HR.test(line)) { out += '<hr>'; i += 1; continue; }

    // 引用 / Callout
    if (RE_QUOTE.test(line)) {
      const buf = [];
      while (i < lines.length) {
        if (RE_QUOTE.test(lines[i])) { buf.push(lines[i].replace(RE_QUOTE, '')); i += 1; continue; }
        if (!lines[i].trim() && i + 1 < lines.length && RE_QUOTE.test(lines[i + 1])) { buf.push(''); i += 1; continue; }
        break;
      }
      const cm = buf[0] !== undefined ? buf[0].match(RE_CALLOUT) : null;
      if (cm) {
        out += renderCallout(cm[1], cm[2], cm[3], buf.slice(1), ctx);
      } else {
        out += `<blockquote>${renderBlocks(buf, ctx)}</blockquote>`;
      }
      continue;
    }

    // 表格
    if (line.includes('|') && isTableSep(lines[i + 1] || '')) {
      const head = splitRow(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].trim() && lines[i].includes('|')) {
        rows.push(splitRow(lines[i]));
        i += 1;
      }
      const th = head.map((c) => `<th>${renderInline(c, ctx)}</th>`).join('');
      const tb = rows.map((r) => {
        const cells = [];
        for (let c = 0; c < head.length; c += 1) cells.push(`<td>${renderInline(r[c] || '', ctx)}</td>`);
        return `<tr>${cells.join('')}</tr>`;
      }).join('');
      out += `<div class="table-wrap"><table><thead><tr>${th}</tr></thead><tbody>${tb}</tbody></table></div>`;
      continue;
    }

    // 列表
    if (RE_LIST.test(line)) {
      const items = [];
      const baseIndent = indentOf(line);
      while (i < lines.length) {
        const cur = lines[i];
        if (!cur.trim()) {
          let j = i + 1;
          while (j < lines.length && !lines[j].trim()) j += 1;
          if (j < lines.length && RE_LIST.test(lines[j]) && indentOf(lines[j]) >= baseIndent) { i = j; continue; }
          break;
        }
        const lm = cur.match(RE_LIST);
        if (lm) {
          const marker = lm[2];
          items.push({
            indent: lm[1].replace(/\t/g, '    ').length,
            ordered: /^\d/.test(marker),
            num: /^\d/.test(marker) ? parseInt(marker, 10) : null,
            text: [lm[3]],
          });
          i += 1;
          continue;
        }
        if (items.length) {
          const ind = indentOf(cur);
          if (ind > baseIndent && !RE_FENCE.test(cur) && !RE_HEADING.test(cur)) {
            items[items.length - 1].text.push(cur.replace(/^[ \t]{0,8}/, ''));
            i += 1;
            continue;
          }
        }
        break;
      }
      const tree = buildListTree(items);
      out += renderListNodes(tree, ctx, tree.length ? tree[0].ordered : false, 0);
      continue;
    }

    // 段落
    const para = [];
    while (i < lines.length && lines[i].trim()) {
      if (para.length && startsBlock(lines[i], lines, i)) break;
      para.push(lines[i]);
      i += 1;
    }
    out += `<p>${para.map((l) => renderInline(l.trim(), ctx)).join('<br>')}</p>`;
  }

  return out;
}

function renderMarkdown(src, ctx = {}) {
  ctx.headings = ctx.headings || [];
  ctx.ids = ctx.ids || new Set();
  const lines = String(src).replace(/\r\n?/g, '\n').split('\n');
  const html = renderBlocks(lines, ctx);
  return { html, headings: ctx.headings };
}

module.exports = { renderMarkdown, renderInline, escapeHtml, slugify, stripMarks };
