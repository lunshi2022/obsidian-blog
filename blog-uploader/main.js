'use strict';

/*
 * Blog Uploader —— 把当前 Obsidian 笔记一键发布到本地博客仓库
 *
 * 流程：复制笔记（保持库内目录结构）→ 复制它引用的附件 → 运行 node src/build.js
 * 构建 → git add / commit / push，GitHub Actions 接手发布。
 *
 * 纯 JavaScript，无需 npm install 或构建步骤，直接放进 .obsidian/plugins/ 即可启用。
 */

const { Plugin, PluginSettingTab, Setting, Notice, TFile } = require('obsidian');
const fs = require('fs');
const path = require('path');
const cp = require('child_process');

const PLUGIN_ID = 'blog-uploader';

const DEFAULT_SETTINGS = {
  blogPath: '',
  contentDir: 'content',
  keepStructure: true,
  autoBuild: true,
  autoPush: true,
  previewPort: 4321,
  gitToken: '',
};

/** 用系统浏览器打开网址（Obsidian 桌面版优先走 electron.shell） */
function openExternal(url) {
  try {
    const electron = require('electron');
    if (electron && electron.shell && electron.shell.openExternal) {
      electron.shell.openExternal(url);
      return;
    }
  } catch (e) { /* 非桌面端，退回 window.open */ }
  window.open(url, '_blank');
}

/** 去掉链接里的 #锚点 与空白 */
function cleanLink(link) {
  return String(link || '').split('#')[0].trim();
}

/**
 * 库和博客仓库是同一个目录时（部署器默认就是这样），笔记已经位于 content/ 下，
 * 不能再套一层 content/，否则会变成 content/content/xxx.md
 */
function stripContentPrefix(relPath, contentDirName) {
  const parts = String(relPath).split(/[\\/]/);
  if (parts.length > 1 && parts[0].toLowerCase() === String(contentDirName).toLowerCase()) {
    return parts.slice(1).join(path.sep);
  }
  return relPath;
}

class BlogUploaderPlugin extends Plugin {
  async onload() {
    this.settings = Object.assign({}, DEFAULT_SETTINGS, await this.loadData());

    this.addRibbonIcon('upload', '上传当前笔记到博客', () => this.uploadActive());

    this.addCommand({
      id: 'upload-current-note',
      name: '上传当前笔记到博客',
      callback: () => this.uploadActive(),
    });

    this.addCommand({
      id: 'build-blog',
      name: '构建博客站点',
      callback: () => this.buildBlog(),
    });

    this.addCommand({
      id: 'open-blog-locally',
      name: '本地预览（构建并打开浏览器）',
      callback: () => this.startPreview(),
    });

    this.addCommand({
      id: 'stop-local-preview',
      name: '停止本地预览服务',
      callback: () => this.stopPreview(),
    });

    // 文件浏览器右键菜单
    this.registerEvent(this.app.workspace.on('file-menu', (menu, file) => {
      if (file instanceof TFile && file.extension === 'md') {
        menu.addItem((item) => item
          .setTitle('上传到博客')
          .setIcon('upload')
          .onClick(() => this.uploadFile(file)));
      }
    }));

    this.addSettingTab(new BlogUploaderSettingTab(this.app, this));
  }

  onunload() {
    // 卸载插件时别把预览服务器留成孤儿进程
    if (this.previewProc) {
      try { this.previewProc.kill(); } catch (e) { /* ignore */ }
      this.previewProc = null;
    }
  }

  async saveSettings() {
    await this.saveData(this.settings);
  }

  getVaultRoot() {
    const adapter = this.app.vault.adapter;
    if (adapter && typeof adapter.getBasePath === 'function') {
      return adapter.getBasePath();
    }
    throw new Error('只在桌面版 Obsidian 里可用');
  }

  uploadActive() {
    const file = this.app.workspace.getActiveFile();
    if (!file) {
      new Notice('当前没有打开的笔记');
      return;
    }
    this.uploadFile(file);
  }

  /** 检查博客路径，无效时提示并打开设置页 */
  ensureBlogPath() {
    const root = String(this.settings.blogPath || '').trim().replace(/[\\/]+$/, '');
    if (!root) {
      new Notice('请先在设置里填写博客仓库路径');
      this.openSettings();
      return null;
    }
    if (!fs.existsSync(root)) {
      new Notice('博客仓库路径不存在：' + root);
      this.openSettings();
      return null;
    }
    return root;
  }

  openSettings() {
    try {
      this.app.setting.open();
      this.app.setting.openTabById(PLUGIN_ID);
    } catch (e) { /* 忽略 */ }
  }

  async uploadFile(file) {
    const root = this.ensureBlogPath();
    if (!root) return;

    const contentDir = path.join(root, this.settings.contentDir || 'content');

    // draft 笔记不发布
    const cache = this.app.metadataCache.getFileCache(file);
    const fm = cache && cache.frontmatter;
    if (fm && (fm.draft === true || fm.draft === 'true')) {
      new Notice('该笔记标记了 draft: true，已跳过上传');
      return;
    }

    let vaultRoot;
    try {
      vaultRoot = this.getVaultRoot();
    } catch (e) {
      new Notice(e.message);
      return;
    }

    const abs = path.join(vaultRoot, file.path);
    if (!fs.existsSync(abs)) {
      new Notice('找不到源文件：' + file.path);
      return;
    }

    // 1. 笔记本体（同名旧版会被覆盖，即“重新上传就是更新”）
    // 注意：Obsidian 的 TFile.path 已经是“相对笔记库根目录”的路径（如 content/foo.md），
    // 不要再用 path.relative(vaultRoot, file.path)——那会把 file.path 当成相对“进程当前目录”
    // 解析（Obsidian 的 CWD 是安装目录），跨盘符时直接吐出绝对路径，导致目标被拼坏。
    const contentName = this.settings.contentDir || 'content';
    const noteRel = stripContentPrefix(file.path, contentName);
    const noteTarget = this.settings.keepStructure
      ? path.join(contentDir, noteRel)
      : path.join(contentDir, file.basename + '.' + file.extension);

    if (!this.copyTo(abs, noteTarget)) return;

    // 2. 附件：笔记里嵌入或链接到的所有非 md 文件
    let attachmentCount = 0;
    const seen = new Set();
    const refs = [];
    if (cache) {
      (cache.embeds || []).forEach((e) => refs.push(e.link));
      (cache.links || []).forEach((l) => refs.push(l.link));
    }

    for (const raw of refs) {
      const link = cleanLink(raw);
      if (!link || seen.has(link)) continue;
      seen.add(link);

      let dest = null;
      try {
        dest = this.app.metadataCache.getFirstLinkpathDest(link, file.path);
      } catch (e) { /* 解析失败就跳过 */ }
      if (!dest) continue;
      if (['md', 'canvas'].includes(dest.extension)) continue;

      const srcAbs = path.join(vaultRoot, dest.path);
      const target = this.settings.keepStructure
        ? path.join(contentDir, stripContentPrefix(dest.path, contentName))
        : path.join(contentDir, 'attachments', dest.name);

      if (this.copyTo(srcAbs, target)) attachmentCount += 1;
    }

    const where = path.relative(root, noteTarget);
    new Notice(`已上传《${file.basename}》→ ${where}`
      + (attachmentCount ? `（含 ${attachmentCount} 个附件）` : ''), 5000);

    if (this.settings.autoBuild) {
      // buildBlog 返回 false 才是真失败（需要中止，避免推一个坏掉的站点上去）；
      // 返回 'skip'（如未装 Node）表示继续推送，交给云端构建
      if (this.buildBlog() === false) return;
    }
    if (this.settings.autoPush) {
      this.pushBlog(file.basename);
    }
  }

  copyTo(srcAbs, targetAbs) {
    try {
      // 源和目标是同一个文件（库就在博客仓库里）时直接跳过，Windows 不允许自我复制
      const a = path.resolve(srcAbs).toLowerCase();
      const b = path.resolve(targetAbs).toLowerCase();
      if (a === b) return true;

      fs.mkdirSync(path.dirname(targetAbs), { recursive: true });
      fs.copyFileSync(srcAbs, targetAbs);
      return true;
    } catch (e) {
      new Notice('复制失败：' + (e.message || e));
      console.error('[blog-uploader] copy failed', srcAbs, '->', targetAbs, e);
      return false;
    }
  }

  buildBlog() {
    const root = this.ensureBlogPath();
    if (!root) return false;

    const script = path.join(root, 'src', 'build.js');
    if (!fs.existsSync(script)) {
      new Notice(`未找到 ${this.settings.contentDir}/../src/build.js，请确认路径指向博客仓库根目录`);
      return false;
    }

    try {
      const res = cp.spawnSync('node', ['src/build.js'], {
        cwd: root,
        encoding: 'utf8',
        windowsHide: true,
        timeout: 120000,
      });

      // 没装 Node（或 PATH 里找不到）不算失败：云端 Actions 会用 Node 20 重新构建，
      // 这里跳过本地构建、继续推送即可。
      if (res.error && res.error.code === 'ENOENT') {
        new Notice('未检测到 Node，已跳过本地构建；推送后 GitHub 会自动构建发布', 6000);
        return 'skip';
      }

      const out = String(res.stdout || '') + String(res.stderr || '');
      if (res.status !== 0) {
        new Notice('构建失败，详情见开发者控制台（Ctrl+Shift+I）', 6000);
        console.error('[blog-uploader] 构建失败：\n' + out);
        return false;
      }
      const m = out.match(/构建完成：([^\n]+)/);
      new Notice(m ? '构建完成：' + m[1].trim() : '构建完成', 4000);
      return true;
    } catch (e) {
      new Notice('构建出错：' + (e.message || e), 6000);
      console.error('[blog-uploader] build error', e);
      return false;
    }
  }

  // ---------------------------------------------------------------- 本地预览

  previewUrl() {
    return 'http://localhost:' + (Number(this.settings.previewPort) || 4321);
  }

  /**
   * 启动本地预览：spawn `node src/build.js --serve --port=N`
   * 之前这里只是 window.open 一个地址，但没人启动服务器，点开就是死页面。
   */
  startPreview() {
    const root = this.ensureBlogPath();
    if (!root) return;

    const url = this.previewUrl();

    // 已经在跑：直接开浏览器，不重复起进程
    if (this.previewProc && !this.previewProc.killed) {
      openExternal(url);
      return;
    }

    const script = path.join(root, 'src', 'build.js');
    if (!fs.existsSync(script)) {
      new Notice('未找到 src/build.js，请确认「博客仓库路径」指向仓库根目录');
      return;
    }

    const port = Number(this.settings.previewPort) || 4321;
    let proc;
    try {
      proc = cp.spawn('node', ['src/build.js', '--serve', '--port=' + port], {
        cwd: root,
        windowsHide: true,
      });
    } catch (e) {
      new Notice('启动预览失败：' + (e.message || e), 8000);
      return;
    }

    proc.on('error', (e) => {
      if (e && e.code === 'ENOENT') {
        new Notice('未检测到 Node.js，无法本地预览（不影响线上发布）', 8000);
      } else {
        new Notice('预览进程出错：' + (e && e.message ? e.message : e), 8000);
      }
      this.previewProc = null;
    });

    proc.stderr.on('data', (d) => {
      const s = String(d);
      if (/EADDRINUSE/.test(s)) {
        new Notice('端口 ' + port + ' 已被占用：桌面可能已有一个预览在跑，已直接打开浏览器', 6000);
      } else {
        console.error('[blog-uploader] preview stderr:', s);
      }
    });

    proc.on('close', () => { this.previewProc = null; });

    this.previewProc = proc;
    new Notice('正在构建并启动本地预览…', 3000);
    // 构建 + 起服务通常 1 秒内完成，留点余量再开浏览器
    window.setTimeout(() => openExternal(url), 1200);
  }

  stopPreview() {
    if (!this.previewProc) {
      new Notice('本地预览没有在运行');
      return;
    }
    try {
      this.previewProc.kill();
      new Notice('已停止本地预览');
    } catch (e) {
      new Notice('停止失败：' + (e.message || e), 6000);
    }
    this.previewProc = null;
  }

  pushBlog(title) {
    const root = this.ensureBlogPath();
    if (!root) return;

    if (!fs.existsSync(path.join(root, '.git'))) {
      new Notice('博客目录不是 git 仓库，已跳过推送');
      return;
    }

    const git = (args) => cp.spawnSync('git', args, {
      cwd: root,
      encoding: 'utf8',
      windowsHide: true,
      timeout: 120000,
    });

    try {
      const staged = git(['add', '-A']);
      // 没装 Git 时下面的 status 会返回空，会被误报成「内容没有变化」，所以先判一次
      if (staged.error && staged.error.code === 'ENOENT') {
        new Notice('未检测到 Git，无法推送。请安装 Git 后重试（笔记已在本地复制完成）', 8000);
        return;
      }

      const status = git(['status', '--porcelain']);
      const nothingToCommit = !String(status.stdout || '').trim();
      // 注意：没有新改动 ≠ 没有未推送的提交（例如上次推送失败后重试），
      // 所以这里不提前返回，继续走推送流程

      if (!nothingToCommit) {
        const commit = git(['commit', '-m', `upload: ${title}`]);
        if (commit.status !== 0) {
          const msg = String(commit.stderr || commit.stdout || '').trim().split('\n')[0];
          new Notice('提交失败：' + msg, 6000);
          console.error('[blog-uploader] commit failed\n' + (commit.stderr || commit.stdout));
          return;
        }
      }

      // 确定当前分支与远程：部署器直推 URL 时未建立上游跟踪，
      // 这里显式带 -u 推一次，之后 plain `git push` 也能用。
      const branch = String(git(['rev-parse', '--abbrev-ref', 'HEAD']).stdout || '').trim() || 'main';
      const remotes = String(git(['remote']).stdout || '')
        .split('\n').map((s) => s.trim()).filter(Boolean);
      const remote = remotes.includes('origin') ? 'origin' : (remotes[0] || '');
      if (!remote) {
        new Notice('未检测到 git 远程仓库（origin），无法推送。请在该目录执行：git remote add origin <你的仓库地址>', 8000);
        return;
      }

      // 若设置了访问令牌，把它嵌进远程 URL 直接推送，彻底绕开 Git Credential Manager
      // 的交互弹窗（否则 GCM 会卡在「选择账户」页面）。
      let pushArgs = ['push', '-u', remote, branch];
      const token = String(this.settings.gitToken || '').trim();
      if (token) {
        const urlOut = git(['remote', 'get-url', remote]);
        const url = String(urlOut.stdout || '').trim();
        // 仅对 https 远程注入令牌；ssh 形式（git@github.com:...）保持原样走密钥/弹窗
        if (/^https:\/\//i.test(url)) {
          const authed = url.replace(/^https:\/\//i, 'https://x-access-token:' + token + '@');
          pushArgs = ['push', '-u', authed, branch];
        }
      }

      const push = git(pushArgs);
      if (push.status !== 0) {
        // git 推送失败时最后一行固定是通用的 "failed to push some refs"，
        // 真实原因（认证失败/非快进/权限不足）在前面的 remote:/fatal:/! 行里
        const lines = String(push.stderr || push.stdout || '')
          .split('\n').map((s) => s.trim()).filter(Boolean);
        const real = lines.find((l) => /^(remote:|fatal:|! |hint:)/i.test(l))
          || lines[0] || '';
        new Notice('推送失败：' + real, 8000);
        console.error('[blog-uploader] push failed\n' + (push.stderr || push.stdout));
        return;
      }
      const out = String(push.stdout || '');
      if (/Everything up-to-date/i.test(out)) {
        new Notice('本地与远端一致，没有需要推送的内容', 4000);
      } else {
        new Notice('已推送，GitHub Actions 正在构建，几分钟后站点会自动更新', 5000);
      }
    } catch (e) {
      new Notice('推送出错：' + (e.message || e), 6000);
      console.error('[blog-uploader] push error', e);
    }
  }
}

class BlogUploaderSettingTab extends PluginSettingTab {
  constructor(app, plugin) {
    super(app, plugin);
    this.plugin = plugin;
  }

  display() {
    const { containerEl } = this;
    containerEl.empty();

    containerEl.createEl('h2', { text: 'Blog Uploader 设置' });

    new Setting(containerEl)
      .setName('本地预览')
      .setDesc('构建站点并在浏览器打开 http://localhost:' + (Number(this.plugin.settings.previewPort) || 4321)
        + '。需要本机装 Node.js；不装也不影响发布。')
      .addButton((b) => b
        .setButtonText('开始预览')
        .setCta()
        .onClick(() => this.plugin.startPreview()))
      .addButton((b) => b
        .setButtonText('停止')
        .onClick(() => this.plugin.stopPreview()));

    new Setting(containerEl)
      .setName('预览端口')
      .setDesc('默认 4321。被占用时改一个，例如 4322')
      .addText((text) => text
        .setPlaceholder('4321')
        .setValue(String(this.plugin.settings.previewPort || 4321))
        .onChange(async (v) => {
          const n = Number(String(v).trim());
          this.plugin.settings.previewPort = (n > 0 && n < 65536) ? n : 4321;
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName('博客仓库路径')
      .setDesc('本地博客仓库的根目录（包含 src/ 和 content/ 的那一层），例如 D:\\blog\\obsidian-blog')
      .addText((text) => text
        .setPlaceholder('D:\\blog\\obsidian-blog')
        .setValue(this.plugin.settings.blogPath)
        .onChange(async (v) => {
          this.plugin.settings.blogPath = v.trim();
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName('GitHub 访问令牌（免登录推送）')
      .setDesc('填入与部署器相同的 GitHub PAT。留空则推送时会弹出 GitHub 登录窗口；'
        + '填好后插件把令牌直接嵌进推送地址，不再弹窗。仅在本地存储，不会上传。')
      .addText((text) => {
        text.inputEl.type = 'password';
        text.setPlaceholder('ghp_xxx 或 github_pat_xxx')
          .setValue(this.plugin.settings.gitToken)
          .onChange(async (v) => {
            this.plugin.settings.gitToken = v.trim();
            await this.plugin.saveSettings();
          });
      });

    new Setting(containerEl)
      .setName('笔记目录名')
      .setDesc('笔记在博客仓库里存放的目录名，默认 content')
      .addText((text) => text
        .setPlaceholder('content')
        .setValue(this.plugin.settings.contentDir)
        .onChange(async (v) => {
          this.plugin.settings.contentDir = v.trim() || 'content';
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName('保留库内目录结构')
      .setDesc('开启后，笔记和附件按 Obsidian 库里的相对路径放进博客；关闭则全部平铺到笔记目录下（平铺时 Markdown 相对路径链接可能失效）')
      .addToggle((t) => t
        .setValue(this.plugin.settings.keepStructure)
        .onChange(async (v) => {
          this.plugin.settings.keepStructure = v;
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName('上传后自动构建')
      .setDesc('运行 node src/build.js 生成静态站点')
      .addToggle((t) => t
        .setValue(this.plugin.settings.autoBuild)
        .onChange(async (v) => {
          this.plugin.settings.autoBuild = v;
          await this.plugin.saveSettings();
        }));

    new Setting(containerEl)
      .setName('构建后自动推送')
      .setDesc('git add / commit / push，推送后由 GitHub Actions 自动发布')
      .addToggle((t) => t
        .setValue(this.plugin.settings.autoPush)
        .onChange(async (v) => {
          this.plugin.settings.autoPush = v;
          await this.plugin.saveSettings();
        }));

    containerEl.createEl('hr');
    const tip = containerEl.createEl('p');
    tip.innerHTML = '使用提示：上传同名笔记会覆盖旧版本，所以「修改后再点一次」就是更新；'
      + '不想发布的笔记在 frontmatter 里写 <code>draft: true</code>。';
  }
}

module.exports = BlogUploaderPlugin;
