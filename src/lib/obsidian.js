'use strict';

const IMAGE_EXT = new Set(['png', 'jpg', 'jpeg', 'gif', 'svg', 'webp', 'avif', 'bmp', 'ico']);
const VIDEO_EXT = new Set(['mp4', 'webm', 'ogv', 'mov']);
const AUDIO_EXT = new Set(['mp3', 'wav', 'ogg', 'm4a', 'flac']);
const FILE_EXT = new Set([...IMAGE_EXT, ...VIDEO_EXT, ...AUDIO_EXT, 'pdf', 'zip', 'canvas', 'excalidraw']);

/**
 * 极简 YAML frontmatter 解析：只处理标量、行内数组 [a, b] 和 - 列表，
 * 足够覆盖 Obsidian 的日常用法（title / date / tags / aliases / draft / slug）。
 */
function parseFrontmatter(raw) {
  const text = String(raw).replace(/^\uFEFF/, '');
  const lines = text.split('\n');
  if (!/^---[ \t]*$/.test(lines[0] || '')) return { data: {}, body: text };

  let end = -1;
  for (let i = 1; i < lines.length; i += 1) {
    if (/^---[ \t]*$/.test(lines[i])) { end = i; break; }
  }
  if (end === -1) return { data: {}, body: text };

  const data = {};
  let currentKey = null;

  for (let i = 1; i < end; i += 1) {
    const line = lines[i];
    if (!line.trim() || /^\s*#/.test(line)) continue;

    const listItem = line.match(/^\s*-\s+(.*)$/);
    if (listItem && currentKey) {
      if (!Array.isArray(data[currentKey])) data[currentKey] = [];
      data[currentKey].push(unquote(listItem[1]));
      continue;
    }

    const kv = line.match(/^([A-Za-z0-9_\u4e00-\u9fa5-]+)\s*:\s*(.*)$/);
    if (!kv) continue;

    const key = kv[1];
    const value = kv[2].trim();
    currentKey = key;

    if (value === '') {
      data[key] = [];
      continue;
    }
    if (value.startsWith('[') && value.endsWith(']')) {
      data[key] = value.slice(1, -1).split(',').map((v) => unquote(v.trim())).filter(Boolean);
      continue;
    }
    data[key] = unquote(value);
  }

  return { data, body: lines.slice(end + 1).join('\n') };
}

function unquote(v) {
  let s = String(v).trim();
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    s = s.slice(1, -1);
  }
  return s.replace(/^\[\[(.*)\]\]$/, '$1');
}

/**
 * 把 [[双链]] / ![[嵌入]] 换成占位符 \u0001N\u0001，避免被后续 Markdown 规则误伤。
 * 围栏代码块与行内代码里的内容原样保留。
 */
function preprocess(raw) {
  const tokens = [];
  const out = [];
  let inFence = false;
  let fenceChar = '';

  const convert = (segment) => segment.replace(/!?\[\[([^\[\]]+)\]\]/g, (whole, inner) => {
    const embed = whole.startsWith('!');
    // 表格里必须写成 [[笔记\|别名]]，这里先把转义竖线换成占位符再分割
    const cleaned = inner.replace(/\\\|/g, '\u0002');
    let target = cleaned;
    let alias = null;
    const pipe = cleaned.indexOf('|');
    if (pipe >= 0) {
      target = cleaned.slice(0, pipe);
      alias = cleaned.slice(pipe + 1).trim();
    }
    target = target.replace(/\u0002/g, '|');
    if (alias) alias = alias.replace(/\u0002/g, '|');
    let heading = null;
    const hash = target.indexOf('#');
    if (hash >= 0) {
      heading = target.slice(hash + 1).trim();
      target = target.slice(0, hash);
    }
    tokens.push({
      embed,
      target: target.trim(),
      heading,
      alias: alias || null,
      raw: whole,
    });
    return `\u0001${tokens.length - 1}\u0001`;
  });

  // 逐行处理，跳过行内代码片段
  const processLine = (line) => {
    const parts = [];
    let last = 0;
    const re = /(`+)[^`]*?\1/g;
    let m;
    while ((m = re.exec(line)) !== null) {
      if (m.index > last) parts.push(convert(line.slice(last, m.index)));
      parts.push(m[0]);
      last = m.index + m[0].length;
    }
    if (last < line.length) parts.push(convert(line.slice(last)));
    return parts.join('');
  };

  for (const line of String(raw).replace(/\r\n?/g, '\n').split('\n')) {
    const fence = line.match(/^ {0,3}(`{3,}|~{3,})/);
    if (fence) {
      if (!inFence) { inFence = true; fenceChar = fence[1][0]; }
      else if (fence[1][0] === fenceChar) { inFence = false; }
      out.push(line);
      continue;
    }
    if (inFence) { out.push(line); continue; }

    out.push(processLine(line));
  }

  return { text: out.join('\n'), tokens };
}

/** 收集正文里出现的行内 #标签（跳过标题行与代码块） */
function collectInlineTags(body) {
  const tags = new Set();
  let inFence = false;
  for (const line of String(body).split('\n')) {
    if (/^ {0,3}(`{3,}|~{3,})/.test(line)) { inFence = !inFence; continue; }
    if (inFence || /^ {0,3}#{1,6}\s/.test(line)) continue;
    const cleaned = line.replace(/`[^`]*`/g, '').replace(/\]\([^)]*\)/g, ']');
    const re = /(?:^|[ \t])#([\p{L}\p{N}][\p{L}\p{N}\/_–-]*)/gu;
    let m;
    while ((m = re.exec(cleaned)) !== null) {
      if (/^\d+$/.test(m[1])) continue;
      tags.add(m[1]);
    }
  }
  return [...tags];
}

function extOf(name) {
  const m = String(name).match(/\.([A-Za-z0-9]+)$/);
  return m ? m[1].toLowerCase() : '';
}

function normalizeTags(value) {
  if (!value) return [];
  const arr = Array.isArray(value) ? value : String(value).split(/[,，]/);
  const out = [];
  for (const t of arr) {
    const clean = String(t).trim().replace(/^#/, '');
    if (clean && !out.includes(clean)) out.push(clean);
  }
  return out;
}

function encodePath(p) {
  return String(p).split('/').map((seg) => encodeURIComponent(seg)).join('/');
}

function tagSlug(tag) {
  return String(tag).replace(/^#/, '').replace(/\//g, '—').replace(/[\\:*?"<>|]/g, '-').trim();
}

module.exports = {
  parseFrontmatter,
  preprocess,
  collectInlineTags,
  normalizeTags,
  extOf,
  encodePath,
  tagSlug,
  IMAGE_EXT,
  VIDEO_EXT,
  AUDIO_EXT,
  FILE_EXT,
};
