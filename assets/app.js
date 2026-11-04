/* 数字花园 · 前端交互（无依赖） */
(function () {
  'use strict';

  var SITE = window.__SITE__ || { root: '' };
  var ROOT = SITE.root || '';

  /* ------------------------------------------------------------ 主题 */

  var THEME_KEY = 'garden-theme';

  function applyTheme(t) {
    document.documentElement.setAttribute('data-theme', t);
  }

  (function initTheme() {
    var saved = null;
    try { saved = localStorage.getItem(THEME_KEY); } catch (e) { /* 忽略 */ }
    if (saved === 'light' || saved === 'dark') applyTheme(saved);
    else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
      applyTheme('dark');
    }
  })();

  document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      applyTheme(next);
      try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* 忽略 */ }
      if (typeof window.__redrawGraph === 'function') window.__redrawGraph();
    });
  });

  /* ------------------------------------------------------------ 移动端目录 */

  var sidebar = document.getElementById('sidebar');
  var scrim = document.querySelector('[data-menu-close]');

  function closeMenu() {
    if (!sidebar) return;
    sidebar.classList.remove('open');
    if (scrim) scrim.hidden = true;
  }

  document.querySelectorAll('[data-menu]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      if (!sidebar) return;
      var open = sidebar.classList.toggle('open');
      if (scrim) scrim.hidden = !open;
    });
  });
  if (scrim) scrim.addEventListener('click', closeMenu);
  if (sidebar) sidebar.addEventListener('click', function (e) {
    if (e.target.closest('a')) closeMenu();
  });

  /* ------------------------------------------------------------ 代码块复制 */

  document.querySelectorAll('[data-copy]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var block = btn.closest('.code-block');
      var code = block && block.querySelector('code');
      if (!code) return;
      var text = code.textContent;
      var done = function () {
        var old = btn.textContent;
        btn.textContent = '已复制';
        setTimeout(function () { btn.textContent = old; }, 1400);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () { /* 忽略 */ });
      } else {
        var ta = document.createElement('textarea');
        ta.value = text;
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); done(); } catch (e) { /* 忽略 */ }
        document.body.removeChild(ta);
      }
    });
  });

  /* ------------------------------------------------------------ 目录跟随 */

  (function initToc() {
    var toc = document.getElementById('toc');
    if (!toc) return;
    var links = Array.prototype.slice.call(toc.querySelectorAll('a'));
    var targets = links.map(function (a) {
      var id = decodeURIComponent(a.getAttribute('href').slice(1));
      return document.getElementById(id);
    }).filter(Boolean);
    if (!targets.length) return;

    var current = null;
    function update() {
      var offset = 120;
      var active = targets[0];
      for (var i = 0; i < targets.length; i += 1) {
        if (targets[i].getBoundingClientRect().top - offset <= 0) active = targets[i];
      }
      if (active === current) return;
      current = active;
      links.forEach(function (a) {
        a.classList.toggle('active', a.getAttribute('href').slice(1) === active.id);
      });
    }
    var ticking = false;
    window.addEventListener('scroll', function () {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(function () { update(); ticking = false; });
    }, { passive: true });
    update();
  })();

  /* ------------------------------------------------------------ 搜索 */

  (function initSearch() {
    var modal = document.getElementById('search-modal');
    var input = document.getElementById('search-input');
    var listEl = document.getElementById('search-results');
    var countEl = document.getElementById('search-count');
    if (!modal || !input || !listEl) return;

    var DATA = null;
    var dataLoading = false;
    var dataWaiting = null;
    var cursor = 0;
    var results = [];
    var opened = false;

    // 索引按需加载：首次打开搜索时才拉，避免每页都背一份全站文本
    function ensureData(cb) {
      if (DATA) { cb(DATA); return; }
      dataWaiting = cb;
      if (dataLoading) return;
      dataLoading = true;
      var s = document.createElement('script');
      s.src = ROOT + 'assets/search-data.js';
      s.onload = function () {
        DATA = window.__SEARCH__ || [];
        dataLoading = false;
        if (dataWaiting) dataWaiting(DATA);
        dataWaiting = null;
      };
      s.onerror = function () {
        DATA = [];
        dataLoading = false;
        if (dataWaiting) dataWaiting(DATA);
        dataWaiting = null;
      };
      document.head.appendChild(s);
    }

    function open() {
      modal.hidden = false;
      opened = true;
      input.value = '';
      document.body.style.overflow = 'hidden';
      setTimeout(function () { input.focus(); }, 20);
      if (DATA) render('');
      else {
        listEl.innerHTML = '<div class="search-empty">正在载入索引…</div>';
        if (countEl) countEl.textContent = '';
      }
      ensureData(function () { render(input.value); });
    }

    function close() {
      modal.hidden = true;
      opened = false;
      document.body.style.overflow = '';
    }

    function score(item, q) {
      var s = 0;
      var title = (item.t || '').toLowerCase();
      if (title === q) s += 400;
      else if (title.indexOf(q) >= 0) s += 200;
      if ((item.g || []).join(' ').toLowerCase().indexOf(q) >= 0) s += 90;
      if ((item.x || '').toLowerCase().indexOf(q) >= 0) s += 40;
      if ((item.c || '').toLowerCase().indexOf(q) >= 0) s += 12;
      return s;
    }

    function snippet(item, q) {
      var src = item.x || item.c || '';
      var idx = src.toLowerCase().indexOf(q);
      var text = idx > 40 ? '…' + src.slice(idx - 40, idx + 110) : src.slice(0, 140);
      if (!text) return '';
      return escapeHtml(text).replace(new RegExp('(' + escapeReg(q) + ')', 'ig'), '<em>$1</em>');
    }

    function escapeHtml(s) {
      return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }
    function escapeReg(s) {
      return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    function render(q) {
      if (!DATA) { return; }
      q = q.trim().toLowerCase();
      if (!q) {
        results = DATA.slice(0, 8);
      } else {
        results = DATA
          .map(function (item) { return { item: item, s: score(item, q) }; })
          .filter(function (r) { return r.s > 0; })
          .sort(function (a, b) { return b.s - a.s; })
          .slice(0, 12)
          .map(function (r) { return r.item; });
      }
      cursor = 0;

      if (!results.length) {
        listEl.innerHTML = '<div class="search-empty">没有找到匹配「' + escapeHtml(q) + '」的内容</div>';
        if (countEl) countEl.textContent = '';
        return;
      }

      listEl.innerHTML = results.map(function (item, i) {
        var ex = q ? snippet(item, q) : escapeHtml(item.x || '');
        var tags = (item.g || []).length ? '<div class="sr-tags">' + item.g.map(function (g) {
          return '#' + escapeHtml(g);
        }).join(' ') + '</div>' : '';
        return '<li data-i="' + i + '"' + (i === 0 ? ' class="active"' : '') + '>'
          + '<a href="' + ROOT + item.u + '">'
          + '<div class="sr-title"><span>' + escapeHtml(item.t) + '</span>'
          + (item.d ? '<time>' + escapeHtml(item.d) + '</time>' : '') + '</div>'
          + (ex ? '<div class="sr-excerpt">' + ex + '</div>' : '')
          + tags
          + '</a></li>';
      }).join('');

      if (countEl) countEl.textContent = results.length + ' 条结果';
    }

    function move(delta) {
      if (!results.length) return;
      var items = listEl.querySelectorAll('li');
      cursor = (cursor + delta + items.length) % items.length;
      items.forEach(function (li, i) { li.classList.toggle('active', i === cursor); });
      var active = items[cursor];
      if (active) {
        active.scrollIntoView({ block: 'nearest' });
      }
    }

    input.addEventListener('input', function () { render(input.value); });

    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
      else if (e.key === 'Enter') {
        var items = listEl.querySelectorAll('li a');
        if (items[cursor]) window.location.href = items[cursor].getAttribute('href');
      } else if (e.key === 'Escape') { close(); }
    });

    document.querySelectorAll('[data-search-open]').forEach(function (b) {
      b.addEventListener('click', open);
    });
    document.querySelectorAll('[data-search-close]').forEach(function (b) {
      b.addEventListener('click', close);
    });

    document.addEventListener('keydown', function (e) {
      var typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName)
        || document.activeElement.isContentEditable;
      if (e.key === '/' && !typing) { e.preventDefault(); open(); }
      else if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); open(); }
      else if (e.key === 'Escape' && opened) { close(); }
    });
  })();

  /* ------------------------------------------------------------ 关系图 */

  (function initGraph() {
    var canvases = document.querySelectorAll('[data-graph]');
    if (!canvases.length) return;
    var data = window.__GRAPH__;
    if (!data || !data.nodes || !data.nodes.length) return;

    var nodes = data.nodes.map(function (n, i) {
      return { id: n.id, title: n.t, url: n.u, x: 0, y: 0, vx: 0, vy: 0, deg: 0, i: i };
    });
    var index = {};
    nodes.forEach(function (n, i) { index[n.id] = i; n.x = Math.cos(i) * 80 + (Math.random() - 0.5) * 40; n.y = Math.sin(i) * 80 + (Math.random() - 0.5) * 40; });

    var links = [];
    (data.links || []).forEach(function (l) {
      var a = index[l.s], b = index[l.t];
      if (a === undefined || b === undefined || a === b) return;
      links.push({ a: a, b: b });
      nodes[a].deg += 1;
      nodes[b].deg += 1;
    });

    canvases.forEach(function (canvas) { setup(canvas, nodes, links); });
  })();

  function setup(canvas, nodes, links) {
    var ctx = canvas.getContext('2d');
    var cssH = Number(canvas.getAttribute('height')) || 320;
    var W = 0, H = cssH, dpr = Math.min(window.devicePixelRatio || 1, 2);
    var temp = 40;
    var K = 80;
    var hovered = -1;
    var dragging = -1;
    var rafId = null;
    var running = false;

    function palette() {
      var cs = getComputedStyle(document.documentElement);
      return {
        accent: cs.getPropertyValue('--accent').trim() || '#17685a',
        muted: cs.getPropertyValue('--muted').trim() || '#8b9199',
        border: cs.getPropertyValue('--border').trim() || '#e9e5dd',
        text: cs.getPropertyValue('--text').trim() || '#1b1f24',
        bg: cs.getPropertyValue('--bg-elev').trim() || '#ffffff',
      };
    }
    var P = palette();

    function resize() {
      W = canvas.clientWidth || (canvas.parentNode && canvas.parentNode.clientWidth) || 320;
      canvas.width = Math.floor(W * dpr);
      canvas.height = Math.floor(H * dpr);
      canvas.style.height = H + 'px';
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      // 理想间距：按画布面积与节点数均摊，再夹在合理区间内
      var ideal = Math.sqrt((W * H) / Math.max(nodes.length, 2)) * 0.8;
      K = Math.max(46, Math.min(ideal, Math.min(W, H) * 0.5));
    }

    function seed() {
      var R = Math.min(W, H) * 0.3;
      nodes.forEach(function (n, i) {
        var a = (i / nodes.length) * Math.PI * 2;
        n.x = Math.cos(a) * R;
        n.y = Math.sin(a) * R;
        n.vx = 0;
        n.vy = 0;
      });
      temp = Math.min(W, H) * 0.3;
    }

    function tick() {
      var n = nodes.length;
      var disp = [];
      for (var i = 0; i < n; i += 1) disp.push({ x: 0, y: 0 });

      // 斥力：所有节点两两相斥
      for (var i = 0; i < n; i += 1) {
        for (var j = i + 1; j < n; j += 1) {
          var dx = nodes[i].x - nodes[j].x;
          var dy = nodes[i].y - nodes[j].y;
          var d = Math.sqrt(dx * dx + dy * dy);
          if (d < 0.05) {
            dx = (Math.random() - 0.5) * 2;
            dy = (Math.random() - 0.5) * 2;
            d = 0.05;
          }
          var f = (K * K) / d;
          var ux = (dx / d) * f, uy = (dy / d) * f;
          disp[i].x += ux; disp[i].y += uy;
          disp[j].x -= ux; disp[j].y -= uy;
        }
      }

      // 引力：有边相连的相互吸引
      for (var k = 0; k < links.length; k += 1) {
        var a = nodes[links[k].a], b = nodes[links[k].b];
        var ddx = a.x - b.x, ddy = a.y - b.y;
        var dist = Math.sqrt(ddx * ddx + ddy * ddy) || 0.05;
        var af = (dist * dist) / K;
        var aux = (ddx / dist) * af, auy = (ddy / dist) * af;
        disp[links[k].a].x -= aux; disp[links[k].a].y -= auy;
        disp[links[k].b].x += aux; disp[links[k].b].y += auy;
      }

      // 向心力，避免整体飘出画面
      for (var m = 0; m < n; m += 1) {
        disp[m].x -= nodes[m].x * 0.035;
        disp[m].y -= nodes[m].y * 0.035;
      }

      var limit = Math.max(0.5, temp);
      var padX = W / 2 - 44;
      var padY = H / 2 - 34;

      for (var p = 0; p < n; p += 1) {
        if (p === dragging) continue;
        var dd = Math.sqrt(disp[p].x * disp[p].x + disp[p].y * disp[p].y) || 1;
        var step = Math.min(dd, limit);
        nodes[p].x += (disp[p].x / dd) * step;
        nodes[p].y += (disp[p].y / dd) * step;
        nodes[p].x = Math.max(-padX, Math.min(padX, nodes[p].x));
        nodes[p].y = Math.max(-padY, Math.min(padY, nodes[p].y));
      }

      temp *= 0.955;
    }

    function radius(node) {
      return 4 + Math.min(node.deg, 10) * 0.85;
    }

    function draw() {
      ctx.clearRect(0, 0, W, H);
      ctx.save();
      ctx.translate(W / 2, H / 2);

      for (var k = 0; k < links.length; k += 1) {
        var l = links[k];
        var a = nodes[l.a], b = nodes[l.b];
        var hot = hovered === l.a || hovered === l.b;
        ctx.strokeStyle = hot ? P.accent : P.border;
        ctx.globalAlpha = hot ? 0.9 : 0.75;
        ctx.lineWidth = hot ? 1.6 : 1;
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
      }

      var showAllLabels = nodes.length <= 22;
      var boxes = [];

      for (var i = 0; i < nodes.length; i += 1) {
        var n = nodes[i];
        var r = radius(n);
        var isHover = i === hovered;

        if (isHover) {
          ctx.globalAlpha = 0.14;
          ctx.fillStyle = P.accent;
          ctx.beginPath();
          ctx.arc(n.x, n.y, r + 9, 0, Math.PI * 2);
          ctx.fill();
        }

        ctx.globalAlpha = isHover ? 1 : (n.deg === 0 ? 0.4 : 0.9);
        ctx.fillStyle = P.accent;
        ctx.beginPath();
        ctx.arc(n.x, n.y, r, 0, Math.PI * 2);
        ctx.fill();

        if (!showAllLabels && !isHover) continue;

        ctx.font = (isHover ? '600 12px ' : '11.5px ')
          + '-apple-system, "PingFang SC", "Microsoft YaHei", sans-serif';
        var label = n.title.length > 11 ? n.title.slice(0, 10) + '…' : n.title;
        var w = ctx.measureText(label).width;
        var box = { x: n.x - w / 2 - 2, y: n.y + r + 3, w: w + 4, h: 15 };

        // 简单避让：和已画的标签重叠就跳过，避免糊成一团
        if (!isHover) {
          var clash = boxes.some(function (b2) {
            return box.x < b2.x + b2.w && box.x + box.w > b2.x
              && box.y < b2.y + b2.h && box.y + box.h > b2.y;
          });
          if (clash) continue;
        }
        boxes.push(box);

        ctx.textAlign = 'center';
        ctx.textBaseline = 'top';
        ctx.globalAlpha = isHover ? 1 : 0.78;
        ctx.lineWidth = 3;
        ctx.strokeStyle = P.bg;
        ctx.strokeText(label, n.x, n.y + r + 4);
        ctx.fillStyle = isHover ? P.text : P.muted;
        ctx.fillText(label, n.x, n.y + r + 4);
      }
      ctx.restore();
    }

    function loop() {
      tick();
      draw();
      if (temp > 0.6) { rafId = requestAnimationFrame(loop); }
      else { running = false; rafId = null; draw(); }
    }

    function kick() {
      if (!running) {
        running = true;
        temp = Math.max(temp, 6);
        rafId = requestAnimationFrame(loop);
      }
    }

    function pick(mx, my) {
      var cx = mx - W / 2, cy = my - H / 2;
      var best = -1, bestD = 24 * 24;
      for (var i = 0; i < nodes.length; i += 1) {
        var dx = nodes[i].x - cx, dy = nodes[i].y - cy;
        var d2 = dx * dx + dy * dy;
        if (d2 < bestD) { bestD = d2; best = i; }
      }
      return best;
    }

    function localPos(e) {
      var rect = canvas.getBoundingClientRect();
      return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    }

    var downAt = null;

    canvas.addEventListener('pointermove', function (e) {
      var pos = localPos(e);
      if (dragging >= 0) {
        nodes[dragging].x = pos.x - W / 2;
        nodes[dragging].y = pos.y - H / 2;
        running = true;
        temp = Math.max(temp, 8);
        if (rafId === null) rafId = requestAnimationFrame(loop);
        return;
      }
      var hit = pick(pos.x, pos.y);
      if (hit !== hovered) {
        hovered = hit;
        canvas.style.cursor = hit >= 0 ? 'pointer' : 'grab';
        draw();
      }
    });

    canvas.addEventListener('pointerdown', function (e) {
      var pos = localPos(e);
      var hit = pick(pos.x, pos.y);
      if (hit >= 0) {
        dragging = hit;
        downAt = { x: pos.x, y: pos.y, i: hit };
        try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* 忽略 */ }
      }
    });

    canvas.addEventListener('pointerup', function (e) {
      if (downAt) {
        var pos = localPos(e);
        var moved = Math.abs(pos.x - downAt.x) + Math.abs(pos.y - downAt.y);
        if (moved < 6) {
          var n = nodes[downAt.i];
          if (n && n.url) window.location.href = ROOT + n.url;
        }
      }
      dragging = -1;
      downAt = null;
      kick();
    });

    canvas.addEventListener('pointerleave', function () {
      if (hovered >= 0) { hovered = -1; draw(); }
    });

    var rt = null;
    window.addEventListener('resize', function () {
      clearTimeout(rt);
      rt = setTimeout(function () { resize(); draw(); }, 140);
    });

    window.__redrawGraph = function () { P = palette(); draw(); };

    resize();
    seed();
    // 预解算：首帧之前把力学模拟跑到收敛（只算不画），
    // 否则打开页面会看到节点抖动一两秒才稳定下来
    for (var warm = 0; warm < 600 && temp > 0.8; warm += 1) tick();
    draw();
  }
})();
