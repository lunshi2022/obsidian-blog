'use strict';
// 生成 src/app.ico（多尺寸 PNG 压缩的 ICO），无第三方依赖。
// 用法：node tools/make-icon.js

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const OUT = path.join(__dirname, '..', 'src', 'app.ico');
const S = 256;        // 主尺寸
const SS = 4;         // 超采样倍数

/* ---------------- CRC32 ---------------- */
const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

function encodePng(width, height, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;   // bit depth
  ihdr[9] = 6;   // RGBA
  ihdr[10] = 0;  // deflate
  ihdr[11] = 0;  // filter
  ihdr[12] = 0;  // no interlace

  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }

  return Buffer.concat([
    sig,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/* ---------------- 图形 ---------------- */

function hexPoints(cx, cy, r, rot) {
  const pts = [];
  for (let i = 0; i < 6; i++) {
    const a = rot + (i * Math.PI) / 3;
    pts.push([cx + Math.cos(a) * r, cy + Math.sin(a) * r]);
  }
  return pts;
}

function pointInPoly(px, py, pts) {
  let inside = false;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const xi = pts[i][0], yi = pts[i][1];
    const xj = pts[j][0], yj = pts[j][1];
    if ((yi > py) !== (yj > py) && px < ((xj - xi) * (py - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function distToPoly(px, py, pts) {
  let best = Infinity;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const x1 = pts[j][0], y1 = pts[j][1], x2 = pts[i][0], y2 = pts[i][1];
    const dx = x2 - x1, dy = y2 - y1;
    const len2 = dx * dx + dy * dy || 1;
    let t = ((px - x1) * dx + (py - y1) * dy) / len2;
    t = Math.max(0, Math.min(1, t));
    const qx = x1 + t * dx, qy = y1 + t * dy;
    const d = Math.hypot(px - qx, py - qy);
    if (d < best) best = d;
  }
  return best;
}

// 深色圆角底 + 青色六边形（与界面 logo 一致）
const BG = [0x0b, 0x12, 0x20, 255];
const RING = [0x00, 0xe5, 0xff, 255];
const CORE = [0x17, 0x68, 0x5a, 255];

function renderMaster(target) {
  const W = target * SS;
  const buf = Buffer.alloc(W * W * 4);
  const radius = W * 0.22;
  const outer = hexPoints(W / 2, W / 2, W * 0.31, -Math.PI / 2);
  const inner = hexPoints(W / 2, W / 2, W * 0.17, -Math.PI / 2);
  // 关键：环宽按目标像素给下限，否则 16px 图标里环只有零点几像素，糊成一团
  const ringW = Math.max(W * 0.028, SS * 1.15);

  for (let y = 0; y < W; y++) {
    for (let x = 0; x < W; x++) {
      const i = (y * W + x) * 4;

      // 圆角矩形遮罩
      const dx = Math.max(radius - x, 0, x - (W - radius));
      const dy = Math.max(radius - y, 0, y - (W - radius));
      const outside = Math.hypot(dx, dy) > radius;
      if (outside) { buf[i + 3] = 0; continue; }

      let color = BG;

      if (pointInPoly(x, y, outer) && distToPoly(x, y, outer) > ringW) {
        // 环内
        if (!pointInPoly(x, y, inner)) color = BG;
        else color = CORE;
      } else if (distToPoly(x, y, outer) <= ringW) {
        color = RING;
      }

      buf[i] = color[0];
      buf[i + 1] = color[1];
      buf[i + 2] = color[2];
      buf[i + 3] = 255;
    }
  }
  return { buf, W };
}

function downsample(master, size) {
  const { buf, W } = master;
  const out = Buffer.alloc(size * size * 4);
  const k = W / size;
  const n = Math.round(k * k);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < k; sy++) {
        for (let sx = 0; sx < k; sx++) {
          const i = ((y * k + sy) * W + (x * k + sx)) * 4;
          const alpha = buf[i + 3];
          r += buf[i] * alpha;
          g += buf[i + 1] * alpha;
          b += buf[i + 2] * alpha;
          a += alpha;
        }
      }
      const o = (y * size + x) * 4;
      if (a === 0) { out[o + 3] = 0; continue; }
      out[o] = Math.round(r / a);
      out[o + 1] = Math.round(g / a);
      out[o + 2] = Math.round(b / a);
      out[o + 3] = Math.round(a / n);
    }
  }
  return out;
}

/* ---------------- 组装 ICO ---------------- */

// 小尺寸必须用未压缩的 DIB（BMP），用 PNG 压缩会导致资源管理器/任务栏读不出图标
// （Windows 只对 >=256 的图标帧支持 PNG 压缩）
function encodeDib(size, rgba) {
  const headerSize = 40;
  const xorSize = size * size * 4;
  const maskRowBytes = Math.ceil(size / 32) * 4;
  const maskSize = maskRowBytes * size;
  const buf = Buffer.alloc(headerSize + xorSize + maskSize);

  buf.writeUInt32LE(40, 0);                    // biSize
  buf.writeInt32LE(size, 4);                   // biWidth
  buf.writeInt32LE(size * 2, 8);               // biHeight = 高 * 2（XOR + AND 两张图）
  buf.writeUInt16LE(1, 12);                    // biPlanes
  buf.writeUInt16LE(32, 14);                   // biBitCount
  buf.writeUInt32LE(0, 16);                    // biCompression = BI_RGB
  buf.writeUInt32LE(xorSize + maskSize, 20);   // biSizeImage

  // 像素数据：自下而上，BGRA 顺序
  for (let y = 0; y < size; y++) {
    const srcRow = (size - 1 - y) * size * 4;
    const dstRow = headerSize + y * size * 4;
    for (let x = 0; x < size; x++) {
      const s = srcRow + x * 4;
      const d = dstRow + x * 4;
      buf[d] = rgba[s + 2];
      buf[d + 1] = rgba[s + 1];
      buf[d + 2] = rgba[s];
      buf[d + 3] = rgba[s + 3];
    }
  }
  // AND 掩码保持全 0：透明度交给 alpha 通道
  return buf;
}

// 每个尺寸单独渲染，保证小图标里线条也够粗
function frameRgba(target) {
  return downsample(renderMaster(target), target);
}

// 16/32/48/64 用 DIB（任务栏、Alt+Tab、资源管理器都用这些小尺寸）
// 128/256 用 PNG（体积小，且 Windows 支持）
const frames = [
  { size: 16, data: encodeDib(16, frameRgba(16)) },
  { size: 32, data: encodeDib(32, frameRgba(32)) },
  { size: 48, data: encodeDib(48, frameRgba(48)) },
  { size: 64, data: encodeDib(64, frameRgba(64)) },
  { size: 128, data: encodePng(128, 128, frameRgba(128)) },
  { size: 256, data: encodePng(256, 256, frameRgba(256)) },
];

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);            // reserved
header.writeUInt16LE(1, 2);            // type = icon
header.writeUInt16LE(frames.length, 4);

const entries = [];
let offset = 6 + frames.length * 16;

for (const f of frames) {
  const e = Buffer.alloc(16);
  const s = f.size;
  e[0] = s >= 256 ? 0 : s;             // width（0 表示 256）
  e[1] = s >= 256 ? 0 : s;             // height
  e[2] = 0;                            // 调色板数量
  e[3] = 0;                            // reserved
  e.writeUInt16LE(1, 4);               // planes
  e.writeUInt16LE(32, 6);              // bpp
  e.writeUInt32LE(f.data.length, 8);
  e.writeUInt32LE(offset, 12);
  offset += f.data.length;
  entries.push(e);
}

const ico = Buffer.concat([header, ...entries, ...frames.map((f) => f.data)]);
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, ico);
console.log('已生成 ' + OUT + '  (' + ico.length + ' 字节)');
console.log('  帧: ' + frames.map((f) => f.size + (f.size <= 64 ? ' DIB' : ' PNG')).join(', '));

