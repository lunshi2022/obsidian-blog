# 我的数字花园

这个文件夹就是你的**博客仓库**，同时也是 Obsidian 笔记库——两边是同一个目录，改笔记、发布、看历史都在这里。

## 日常怎么用

- **写笔记**：直接在 `content/` 里新建 `.md`，想分目录就建子文件夹（目录会变成侧边栏树）
- **发布**：点左侧边栏的 ↑ 图标，或右键笔记 →「上传到博客」，约 1–3 分钟后网站自动更新
- **改完再发**：再点一次上传即可，同名覆盖
- **不想发布某篇**：frontmatter 里写 `draft: true`

> [!tip] 上传前先保存
> 插件读的是磁盘上的文件，Obsidian 里没保存的改动不会被上传——按一次 `Ctrl+S`。

## 常改的几处

| 想改什么 | 改哪里 |
| --- | --- |
| 首页开场白 | `content/index.md` |
| 站名、图标、副标题、署名 | `site.config.json` 的 `title` / `mark` / `description` / `author` |
| 配色主题 | `site.config.json` 的 `theme`：`garden` 青绿 / `ink` 墨蓝 / `dawn` 暖阳 |
| 浏览器小图标 | 替换 `assets/favicon.svg` |

目录里其他东西一般不用动：`src/` 是构建脚本，`assets/` 是样式与前端脚本，`site/` 是本地构建产物（不会上传）。

## 写作语法

`[[双链]]`、`![[图片.png]]`、`#标签`、Callout、表格、任务列表、数学公式都支持。语法速查见 `content/使用指南/`，那几篇看完可以直接删。

## 几点提醒

- `.obsidian/` 已被 `.gitignore` 排除，插件里的访问令牌不会上传到 GitHub
- 网站地址和构建进度在 GitHub 仓库的 **Actions** 里，正常需要几分钟
- 部署器里可以「检查更新」升级工具本身，也能一键「换主题」

---

项目源码与文档：<https://github.com/lunshi2022/obsidian-blog>
