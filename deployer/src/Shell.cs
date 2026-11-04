using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
// 注意：不能 using System.Windows.Shapes —— 它的 Path 会跟 System.IO.Path 二义（CS0104）。
// 只把需要的两个类型做成别名。
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace GardenDeployer
{
    /// <summary>把 XAML 窗口和部署引擎接起来：收集输入、显示进度、触发部署</summary>
    sealed class Shell
    {
        readonly Window W;
        readonly Dispatcher D;
        readonly Dictionary<string, object> map = new Dictionary<string, object>();

        bool running;
        bool completed;
        Engine job;
        string pagesUrl = "";
        string currentTarget = "";
        Tool lastNode;

        /// <summary>以管理员身份重启后带过来的参数，窗口显示出来后自动开始部署</summary>
        public Config Pending = null;

        public Shell(Window w)
        {
            W = w;
            D = w.Dispatcher;

            string[] names = new string[] {
                "HeaderBar", "StatusDot", "StatusText", "StatusPill", "BtnMin", "BtnClose",
                "TxtPath", "BtnBrowse", "TxtRepo", "TxtToken", "BtnToken",
                "EnvNodeDot", "EnvNodeVer", "EnvNodePath",
                "EnvGitDot", "EnvGitVer", "EnvGitPath",
                "EnvObsDot", "EnvObsVer", "EnvObsPath", "BtnRefresh",
                "LogHost", "LogScroll", "BtnClearLog",
                "BarFill", "BarGlow", "BarTrack", "PctText", "PhaseText",
                "BtnStart", "BtnOpen", "BtnUpdate", "BtnTheme", "BtnQuit", "HintText", "GaugeTarget", "RingRotate", "ChkNode",
                "HeroTitle", "TopScan",
                "BgGrid", "GridDrift", "BgScan", "ScanShift", "FlowA", "FlowB", "FlowShiftA", "FlowShiftB"
            };
            foreach (string n in names) map[n] = w.FindName(n);

            for (int i = 0; i < 6; i++)
            {
                map["S" + i + "Dot"] = w.FindName("S" + i + "Dot");
                map["S" + i + "Name"] = w.FindName("S" + i + "Name");
                map["S" + i + "Sub"] = w.FindName("S" + i + "Sub");
                map["S" + i + "Rail"] = w.FindName("S" + i + "Rail");
                map["S" + i + "Mark"] = w.FindName("S" + i + "Mark");
            }

            Wire();
            CheckControls();
            Init();

            // 小屏/高 DPI 缩放时窗口会比工作区大，右侧（按钮栏）会被屏幕边缘裁掉：
            // 启动时把窗口钳制到工作区内
            try
            {
                double maxW = SystemParameters.WorkArea.Width - 24;
                double maxH = SystemParameters.WorkArea.Height - 24;
                if (W.Width > maxW) W.Width = Math.Floor(maxW);
                if (W.Height > maxH) W.Height = Math.Floor(maxH);
            }
            catch { }

            // 窗口真正显示出来后再自动开始（提权重启后走这条路）
            W.Loaded += delegate
            {
                if (Pending == null) return;
                Config c = Pending;
                Pending = null;
                PrefillAndStart(c);
            };
        }

        /// <summary>把外部传入的参数填进界面并立即开始部署</summary>
        public void PrefillAndStart(Config cfg)
        {
            TextBox tp = G<TextBox>("TxtPath");
            TextBox tr = G<TextBox>("TxtRepo");
            PasswordBox tk = G<PasswordBox>("TxtToken");
            CheckBox ck = G<CheckBox>("ChkNode");

            if (tp != null && cfg.Target != null && cfg.Target.Length > 0) tp.Text = cfg.Target;
            if (tr != null && cfg.Repo != null && cfg.Repo.Length > 0) tr.Text = cfg.Repo;
            if (tk != null && cfg.Token != null) tk.Password = cfg.Token;
            if (ck != null && cfg.InstallNode) ck.IsChecked = true;

            AddLog("cmd", "已以管理员身份启动，自动继续部署…");
            Program.Trace("PrefillAndStart: " + cfg.Target);
            StartDeploy();
        }

        public static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        T G<T>(string name) where T : class
        {
            object o = null;
            if (!map.TryGetValue(name, out o)) return null;
            return o as T;
        }

        /// <summary>启动时校验控件类型，避免“取到 null 再赋值”这种只会崩在运行时的错</summary>
        void CheckControls()
        {
            Need<TextBox>("TxtPath");
            Need<TextBox>("TxtRepo");
            Need<PasswordBox>("TxtToken");
            Need<Button>("BtnStart");
            Need<Button>("BtnBrowse");
            Need<Button>("BtnToken");
            Need<CheckBox>("ChkNode");
            Need<Button>("BtnRefresh");
            Need<Button>("BtnOpen");
            Need<Button>("BtnUpdate");
            Need<StackPanel>("LogHost");
            Need<ScrollViewer>("LogScroll");
            Need<Grid>("BarTrack");
            Need<Border>("BarFill");
            Need<Rectangle>("BarGlow");
            Need<Ellipse>("S0Dot");
            Need<Rectangle>("S0Rail");
            Need<TextBlock>("PctText");
            Need<TextBlock>("PhaseText");
            Need<TextBlock>("StatusText");
            Need<Border>("HeaderBar");
            Need<Ellipse>("EnvNodeDot");
            Need<Ellipse>("EnvObsDot");
        }

        void Need<T>(string name) where T : class
        {
            object o = null;
            if (!map.TryGetValue(name, out o) || o == null)
            {
                Program.Trace("!! 缺少控件：" + name);
                return;
            }
            if (!(o is T))
            {
                Program.Trace("!! 控件类型不符：" + name + " 实际=" + o.GetType().Name + " 期望=" + typeof(T).Name);
            }
        }

        void Post(Action a)
        {
            if (a == null) return;
            if (D.CheckAccess()) a();
            else D.BeginInvoke(a);
        }

        // ---------------------------------------------------------------- 事件

        void Wire()
        {
            Border header = G<Border>("HeaderBar");
            if (header != null)
            {
                header.MouseLeftButtonDown += delegate(object s, System.Windows.Input.MouseButtonEventArgs e)
                {
                    try { W.DragMove(); } catch { }
                };
            }

            Button min = G<Button>("BtnMin");
            if (min != null) min.Click += delegate { W.WindowState = WindowState.Minimized; };

            Button close = G<Button>("BtnClose");
            if (close != null) close.Click += delegate { CloseSelf(); };

            Button quit = G<Button>("BtnQuit");
            if (quit != null) quit.Click += delegate { CloseSelf(); };

            Button update = G<Button>("BtnUpdate");
            if (update != null)
            {
                update.ToolTip = "当前版本 v" + Program.AppVersion + "，点击检查新版本";
                update.Click += delegate { CheckForUpdate(update); };
            }

            Button theme = G<Button>("BtnTheme");
            if (theme != null) theme.Click += delegate { CycleTheme(theme); };

            Button browse = G<Button>("BtnBrowse");
            if (browse != null) browse.Click += delegate { BrowseTarget(); };

            Button token = G<Button>("BtnToken");
            if (token != null)
            {
                token.Click += delegate
                {
                    try
                    {
                        Process.Start("https://github.com/settings/tokens/new?scopes=repo,workflow&description=Garden%20Deployer");
                        AddLog("cmd", "已在浏览器打开令牌创建页：勾选 repo 与 workflow，生成后粘贴回界面");
                    }
                    catch (Exception ex) { AddLog("err", "无法打开浏览器：" + ex.Message); }
                };
            }

            Button clear = G<Button>("BtnClearLog");
            if (clear != null)
            {
                clear.Click += delegate
                {
                    StackPanel host = G<StackPanel>("LogHost");
                    if (host != null) host.Children.Clear();
                };
            }

            Button refresh = G<Button>("BtnRefresh");
            if (refresh != null)
            {
                refresh.Click += delegate
                {
                    if (running) return;
                    AddLog("cmd", "重新检测环境…");
                    StartDetect();
                };
            }

            // 勾选「安装 Node.js」后，Node 就从可选变成必需：
            // 环境面板要立刻跟着改（去掉「可选」、变黄色呼吸），否则语义对不上
            CheckBox nodeBox = G<CheckBox>("ChkNode");
            if (nodeBox != null)
            {
                nodeBox.Checked += delegate { RefreshNodeRow(); SyncDepsStepLabel(); };
                nodeBox.Unchecked += delegate { RefreshNodeRow(); SyncDepsStepLabel(); };
            }

            Button start = G<Button>("BtnStart");
            if (start != null) start.Click += delegate { StartDeploy(); };

            Button open = G<Button>("BtnOpen");
            if (open != null)
            {
                open.Click += delegate
                {
                    string url = pagesUrl;
                    if (url.Length == 0)
                    {
                        TextBox tp = G<TextBox>("TxtPath");
                        string local = Path.Combine(tp.Text.Trim(), @"site\index.html");
                        if (File.Exists(local)) url = local;
                    }
                    if (url.Length > 0)
                    {
                        try { Process.Start(url); }
                        catch (Exception ex) { AddLog("err", "打不开：" + ex.Message); }
                    }
                    else AddLog("warn", "还没有可打开的地址");
                };
            }

            W.Closed += delegate
            {
                if (job != null) job.Cancel();
            };
        }

        void CloseSelf()
        {
            if (running && !completed)
            {
                MessageBoxResult r = MessageBox.Show("部署正在进行，确定要退出吗？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            if (job != null) job.Cancel();
            W.Close();
        }

        void BrowseTarget()
        {
            System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = "选择部署目录的上级位置，程序会在其中创建 GardenBlog 文件夹";
            dlg.ShowNewFolderButton = true;

            TextBox tp = G<TextBox>("TxtPath");
            if (tp.Text.Trim().Length > 0 && Directory.Exists(tp.Text.Trim()))
            {
                dlg.SelectedPath = tp.Text.Trim();
            }

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string chosen = Path.Combine(dlg.SelectedPath, "GardenBlog");
                tp.Text = chosen;
                AddLog("cmd", "目标目录已设置：" + chosen);
            }
        }

        // ---------------------------------------------------------------- 检查更新

        /// <summary>查询 GitHub Release 最新版本；有新版则下载并自动替换重启</summary>
        void CheckForUpdate(Button btn)
        {
            btn.IsEnabled = false;
            AddLog("cmd", "正在检查更新…（当前版本 v" + Program.AppVersion + "）");

            Thread t = new Thread(delegate()
            {
                string tag = "";
                string exeUrl = "";
                try
                {
                    using (TimeoutClient wc = new TimeoutClient())
                    {
                        wc.Headers["User-Agent"] = "GardenDeployer";
                        wc.Headers["Accept"] = "application/vnd.github+json";
                        wc.Encoding = new System.Text.UTF8Encoding(false);
                        string json = wc.DownloadString(
                            "https://api.github.com/repos/" + Program.UpdateRepo + "/releases/latest");
                        Match mt = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"(v[^\"]*)\"");
                        if (mt.Success) tag = mt.Groups[1].Value;
                        Match mu = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]*\\.exe)\"");
                        if (mu.Success) exeUrl = mu.Groups[1].Value;
                    }
                }
                catch (Exception ex)
                {
                    Post(delegate { AddLog("err", "检查更新失败：" + ex.Message); btn.IsEnabled = true; });
                    return;
                }

                string latest = tag.TrimStart('v');
                string cur = Program.AppVersion.TrimStart('v');
                bool newer;
                try { newer = new Version(latest).CompareTo(new Version(cur)) > 0; }
                catch { newer = latest.Length > 0 && latest != cur; }

                Post(delegate
                {
                    if (tag.Length == 0)
                    {
                        AddLog("err", "未读到版本信息（Release 页面可能还没有发布过）");
                        btn.IsEnabled = true;
                        return;
                    }
                    if (!newer)
                    {
                        AddLog("ok", "已是最新版本 v" + Program.AppVersion);
                        btn.IsEnabled = true;
                        return;
                    }
                    AddLog("cmd", "发现新版本 " + tag + "，开始下载…");
                    if (exeUrl.Length == 0)
                    {
                        AddLog("err", "Release 附件里没有 exe，请到 GitHub Releases 页面手动下载");
                        btn.IsEnabled = true;
                        return;
                    }
                    DownloadUpdate(tag, exeUrl, btn);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>下载新版 exe 到旁路文件，确认后由 cmd 在本进程退出后替换并重启</summary>
        void DownloadUpdate(string tag, string url, Button btn)
        {
            string exePath;
            try { exePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch { exePath = Path.Combine(Program.AppDir, "GardenDeployer.exe"); }
            int pid = Process.GetCurrentProcess().Id;
            string newFile = exePath + ".update.exe";

            Thread t = new Thread(delegate()
            {
                try
                {
                    using (TimeoutClient wc = new TimeoutClient())
                    {
                        wc.Headers["User-Agent"] = "GardenDeployer";
                        wc.DownloadFile(url, newFile);
                    }
                }
                catch (Exception ex)
                {
                    try { File.Delete(newFile); } catch { }
                    Post(delegate { AddLog("err", "下载失败：" + ex.Message); btn.IsEnabled = true; });
                    return;
                }

                Post(delegate
                {
                    if (MessageBox.Show("新版本 " + tag + " 已下载。\n点击「确定」自动替换并重启部署器。",
                            "检查更新", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    {
                        btn.IsEnabled = true;
                        return;
                    }
                    // exe 运行中无法覆盖自己：交给 cmd 等本进程退出后再替换、重启
                    string bat = Path.Combine(Path.GetTempPath(), "garden-update.bat");
                    File.WriteAllText(bat,
                        "@echo off\r\n" +
                        ":wait\r\n" +
                        "tasklist /FI \"PID eq " + pid + "\" | find \"" + pid + "\" >nul\r\n" +
                        "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
                        "move /y \"" + newFile + "\" \"" + exePath + "\"\r\n" +
                        "start \"\" \"" + exePath + "\"\r\n",
                        new System.Text.UTF8Encoding(false));
                    Process.Start(new ProcessStartInfo(bat)
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    CloseSelf();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------------------------------------------------------- 换主题

        static readonly string[] ThemeIds = { "garden", "ink", "dawn" };
        static readonly string[] ThemeNames = { "青绿", "墨蓝", "暖阳" };

        static string ThemeAfter(string id)
        {
            int i = Array.IndexOf(ThemeIds, id);
            if (i < 0) i = 0;
            return ThemeIds[(i + 1) % ThemeIds.Length];
        }

        static string ThemeNameOf(string id)
        {
            int i = Array.IndexOf(ThemeIds, id);
            return i >= 0 ? ThemeNames[i] : ThemeNames[0];
        }

        /// <summary>按笔记库当前配置刷新底栏主题按钮：按钮上显示的是「下一个」主题</summary>
        void RefreshThemeLabel()
        {
            Button btn = G<Button>("BtnTheme");
            if (btn == null) return;
            try
            {
                string target = currentTarget.Length > 0 ? currentTarget : G<TextBox>("TxtPath").Text.Trim();
                string cfgPath = Path.Combine(target, "site.config.json");
                if (!File.Exists(cfgPath)) return;
                Match cm = Regex.Match(File.ReadAllText(cfgPath), "\"theme\"\\s*:\\s*\"([^\"]*)\"");
                string cur = cm.Success ? cm.Groups[1].Value.ToLower() : "garden";
                string nxt = ThemeAfter(cur);
                btn.Content = "主题→" + ThemeNameOf(nxt);
                btn.ToolTip = "当前配色：" + ThemeNameOf(cur) + "。点击切换为「" + ThemeNameOf(nxt) + "」并自动推送";
            }
            catch { }
        }

        /// <summary>循环切换站点配色：改 site.config.json 后自动提交推送，Actions 自动重建</summary>
        void CycleTheme(Button btn)
        {
            if (running) { AddLog("warn", "正在部署中，等完成再换主题"); return; }

            string target = currentTarget.Length > 0 ? currentTarget : G<TextBox>("TxtPath").Text.Trim();
            string cfgPath = Path.Combine(target, "site.config.json");
            if (!File.Exists(cfgPath) || !Directory.Exists(Path.Combine(target, ".git")))
            {
                AddLog("warn", "还没有可用的部署目录（或不是 git 仓库），先完成一次部署再换主题");
                return;
            }

            string txt;
            try { txt = File.ReadAllText(cfgPath); }
            catch (Exception ex) { AddLog("err", "读取 site.config.json 失败：" + ex.Message); return; }

            Match cm = Regex.Match(txt, "\"theme\"\\s*:\\s*\"([^\"]*)\"");
            string cur = cm.Success ? cm.Groups[1].Value.ToLower() : "garden";
            int idx = Array.IndexOf(ThemeIds, cur);
            if (idx < 0) idx = 0;
            string newId = ThemeIds[(idx + 1) % ThemeIds.Length];
            string newName = ThemeNames[(idx + 1) % ThemeNames.Length];

            string newTxt = cm.Success
                ? txt.Substring(0, cm.Groups[1].Index) + newId + txt.Substring(cm.Groups[1].Index + cm.Groups[1].Length)
                : txt.Replace("}", ",\"theme\": \"" + newId + "\"}");
            try { File.WriteAllText(cfgPath, newTxt, new System.Text.UTF8Encoding(false)); }
            catch (Exception ex) { AddLog("err", "写入 site.config.json 失败：" + ex.Message); return; }

            PasswordBox tk = G<PasswordBox>("TxtToken");
            string token = tk != null ? tk.Password.Trim() : "";

            btn.IsEnabled = false;
            AddLog("cmd", "切换主题 → " + newName + "，正在推送…");

            Thread t = new Thread(delegate()
            {
                string outp;
                int code = Proc.Run("git", "add site.config.json", target, out outp);
                if (code == 0)
                {
                    code = Proc.Run("git", "commit -m \"theme: switch to " + newId + "\"", target, out outp);
                }

                bool nothingNew = outp != null && outp.IndexOf("nothing to commit", StringComparison.OrdinalIgnoreCase) >= 0;

                if (code != 0 && !nothingNew)
                {
                    Post(delegate
                    {
                        btn.IsEnabled = true;
                        AddLog("err", "提交失败：" + GitErrLine(outp));
                    });
                    return;
                }

                // 推送：有令牌就内嵌 URL 绕开凭据管理器
                string branch = "main";
                string bOut;
                Proc.Run("git", "rev-parse --abbrev-ref HEAD", target, out bOut);
                if (bOut != null && bOut.Trim().Length > 0) branch = bOut.Trim();

                string pushArgs = "push origin " + branch;
                if (token.Length > 0)
                {
                    string urlOut;
                    Proc.Run("git", "remote get-url origin", target, out urlOut);
                    string url = (urlOut ?? "").Trim();
                    if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        string bare = Regex.Replace(url, "^https://[^@]*@", "https://");
                        pushArgs = "push https://x-access-token:" + token + "@" + bare.Substring("https://".Length) + " " + branch;
                    }
                }

                int pcode = Proc.Run("git", pushArgs, target, out outp);
                Post(delegate
                {
                    btn.IsEnabled = true;
                    if (pcode == 0) RefreshThemeLabel();
                    if (pcode == 0)
                    {
                        AddLog("ok", "主题已切换为「" + newName + "」，GitHub 构建中，几分钟后网站自动换装");
                        AddLog("", "  提示：Pages 页面缓存约 10 分钟，若各页面颜色暂时不一致属正常，稍后刷新或按 Ctrl+F5 即可统一");
                    }
                    else
                    {
                        AddLog("err", "推送失败：" + GitErrLine(outp));
                        AddLog("warn", "主题改动已写入本地 site.config.json，可稍后在 Obsidian 里上传任意笔记触发推送");
                    }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>从 git 输出里挑真实错误行（最后一行固定是通用的 failed to push some refs）</summary>
        static string GitErrLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            string[] lines = s.Split('\n');
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("! ") || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    return l;
            }
            return lines[lines.Length - 1].Trim();
        }

        // ---------------------------------------------------------------- 初始化

        void Init()
        {
            TextBox tp = G<TextBox>("TxtPath");
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            tp.Text = Path.Combine(docs, "GardenBlog");

            // 表头 USER 块显示当前运行权限（恢复参考项目原来的 MODE / GUI 装饰风格）
            TextBlock target = G<TextBlock>("GaugeTarget");
            if (target != null) target.Text = IsAdmin() ? "ADMIN" : "USER";

            Button open = G<Button>("BtnOpen");
            if (open != null) open.IsEnabled = false;

            SetStep(0, "pending", "");
            SetBar(0, "等待开始");
            SetStatus("就绪", "#2c3a54");

            RefreshThemeLabel();
            AddLog("hi", "数字花园部署器已就绪");
            AddLog("", "流程：填好目录与令牌 → 点「开始部署」→ 完成后点「打开站点」");
            if (!IsAdmin()) AddLog("warn", "当前不是管理员权限，安装 Git/Obsidian 时可能失败");

            if (Program.PayloadDir == null)
            {
                AddLog("err", "找不到站点模板（src\\build.js）。请把本程序放在博客仓库内（deployer 目录）运行。");
                Button start = G<Button>("BtnStart");
                if (start != null) start.IsEnabled = false;
            }
            else
            {
                AddLog("", "模板目录：" + Program.PayloadDir);
            }

            StartAnimations();
            SyncDepsStepLabel();
            StartDetect();
        }

        // ---------------------------------------------------------------- 动画

        /// <summary>常驻动画：logo 光环旋转、状态灯呼吸、进度条流光</summary>
        void StartAnimations()
        {
            RotateTransform ring = G<RotateTransform>("RingRotate");
            if (ring != null)
            {
                DoubleAnimation spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(7)));
                spin.RepeatBehavior = RepeatBehavior.Forever;
                ring.BeginAnimation(RotateTransform.AngleProperty, spin);
            }

            Ellipse dot = G<Ellipse>("StatusDot");
            if (dot != null)
            {
                DoubleAnimation breathe = new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(1100)));
                breathe.AutoReverse = true;
                breathe.RepeatBehavior = RepeatBehavior.Forever;
                dot.BeginAnimation(UIElement.OpacityProperty, breathe);
            }

            SetBarSweep(false);   // 未开始时隐藏流光

            StartBackgroundFx();  // 背景网格/扫描光/流线

            StartButtonGlow();    // 主按钮光晕呼吸
            StartTopScan();       // 顶栏下沿的扫描光
        }

        /// <summary>竖直双色渐变画刷（导轨用：上亮下暗才有"连接"的错觉）</summary>
        static LinearGradientBrush VertBrush(string top, string bottom)
        {
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, 0);
            b.EndPoint = new Point(0, 1);
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top), 0));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1));
            return b;
        }

        /// <summary>背景氛围：网格漂移、扫描光下落、两条竖线流动。周期都很长，只是"机器在呼吸"</summary>
        void StartBackgroundFx()
        {
            TranslateTransform drift = G<TranslateTransform>("GridDrift");
            if (drift != null)
            {
                DoubleAnimation ax = new DoubleAnimation(0, 44, new Duration(TimeSpan.FromSeconds(36)));
                ax.RepeatBehavior = RepeatBehavior.Forever;
                drift.BeginAnimation(TranslateTransform.XProperty, ax);
                DoubleAnimation ay = new DoubleAnimation(0, 44, new Duration(TimeSpan.FromSeconds(36)));
                ay.RepeatBehavior = RepeatBehavior.Forever;
                drift.BeginAnimation(TranslateTransform.YProperty, ay);
            }

            TranslateTransform scan = G<TranslateTransform>("ScanShift");
            if (scan != null)
            {
                DoubleAnimation sy = new DoubleAnimation(-220, 900, new Duration(TimeSpan.FromSeconds(7)));
                sy.RepeatBehavior = RepeatBehavior.Forever;
                scan.BeginAnimation(TranslateTransform.YProperty, sy);
            }

            TranslateTransform fa = G<TranslateTransform>("FlowShiftA");
            if (fa != null)
            {
                DoubleAnimation fay = new DoubleAnimation(-160, 900, new Duration(TimeSpan.FromSeconds(4.5)));
                fay.RepeatBehavior = RepeatBehavior.Forever;
                fa.BeginAnimation(TranslateTransform.YProperty, fay);
            }

            TranslateTransform fb = G<TranslateTransform>("FlowShiftB");
            if (fb != null)
            {
                // 错开半个周期，两条线不同步才像"流动"而不是"一起动"
                DoubleAnimation fby = new DoubleAnimation(-160, 900, new Duration(TimeSpan.FromSeconds(4.5)));
                fby.BeginTime = TimeSpan.FromSeconds(2.2);
                fby.RepeatBehavior = RepeatBehavior.Forever;
                fb.BeginAnimation(TranslateTransform.YProperty, fby);
            }
        }

        /// <summary>主按钮外发光缓慢呼吸，提示这是主操作</summary>
        void StartButtonGlow()
        {
            Button b = G<Button>("BtnStart");
            if (b == null) return;

            try
            {
                DropShadowEffect glow = new DropShadowEffect();
                glow.Color = Color.FromRgb(0x00, 0xE5, 0xFF);
                glow.BlurRadius = 26;
                glow.ShadowDepth = 0;
                glow.Opacity = 0.5;
                b.Effect = glow;

                DoubleAnimation pulse = new DoubleAnimation(0.2, 0.72,
                    new Duration(TimeSpan.FromMilliseconds(1200)));
                pulse.AutoReverse = true;
                pulse.RepeatBehavior = RepeatBehavior.Forever;
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
            }
            catch (Exception ex)
            {
                Program.Trace("按钮光晕启动失败: " + ex.Message);
            }
        }

        /// <summary>顶栏下沿的一道扫描光，从左扫到右</summary>
        void StartTopScan()
        {
            Rectangle scan = G<Rectangle>("TopScan");
            if (scan == null) return;

            try
            {
                LinearGradientBrush lg = new LinearGradientBrush();
                lg.StartPoint = new Point(0, 0.5);
                lg.EndPoint = new Point(1, 0.5);
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0xE5, 0xFF), 0.00));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0xE5, 0xFF), 0.36));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, 0x00, 0xE5, 0xFF), 0.50));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0x88, 0x7F, 0xEA, 0xFF), 0.53));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0xE5, 0xFF), 0.66));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0xE5, 0xFF), 1.00));

                TranslateTransform tt = new TranslateTransform(0, 0);
                lg.RelativeTransform = tt;
                scan.Fill = lg;

                DoubleAnimation sweep = new DoubleAnimation(-1, 1,
                    new Duration(TimeSpan.FromSeconds(6.5)));
                sweep.RepeatBehavior = RepeatBehavior.Forever;
                tt.BeginAnimation(TranslateTransform.XProperty, sweep);
            }
            catch (Exception ex)
            {
                Program.Trace("扫描光启动失败: " + ex.Message);
            }
        }

        /// <summary>进度条上的流光扫过，只在部署进行时打开</summary>
        void SetBarSweep(bool on)
        {
            Rectangle glow = G<Rectangle>("BarGlow");
            if (glow == null) return;

            if (!on)
            {
                glow.BeginAnimation(UIElement.OpacityProperty, null);
                glow.Opacity = 0;
                return;
            }

            glow.Opacity = 1;
            TranslateTransform tt = new TranslateTransform(0, 0);
            glow.RenderTransform = tt;
            DoubleAnimation sweep = new DoubleAnimation(-90, 1400, new Duration(TimeSpan.FromSeconds(2.4)));
            sweep.RepeatBehavior = RepeatBehavior.Forever;
            tt.BeginAnimation(TranslateTransform.XProperty, sweep);
        }

        /// <summary>步骤圆点呼吸；非激活态清除动画</summary>
        void PulseDot(Ellipse dot, bool on)
        {
            if (dot == null) return;
            if (on)
            {
                DoubleAnimation pulse = new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromMilliseconds(650)));
                pulse.AutoReverse = true;
                pulse.RepeatBehavior = RepeatBehavior.Forever;
                dot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else
            {
                dot.BeginAnimation(UIElement.OpacityProperty, null);
                dot.Opacity = 1;
            }
        }

        // ---------------------------------------------------------------- 环境检测

        static string EnvDesc(Tool t)
        {
            if (t == null || !t.Ok) return "未安装";
            return (t.Version != null && t.Version.Length > 0) ? t.Version : "已安装";
        }

        static string EnvCls(Tool t)
        {
            return (t != null && t.Ok) ? "ok" : "warn";
        }

        void StartDetect()
        {
            Button refresh = G<Button>("BtnRefresh");
            if (refresh != null) refresh.IsEnabled = false;

            Thread t = new Thread(delegate()
            {
                Tool node = null;
                Tool git = null;
                Tool obs = null;
                try
                {
                    Env.UpdateSessionPath();
                    node = Env.FindNode();
                    git = Env.FindGit();
                    obs = Env.FindObsidian();
                }
                catch { }

                Post(delegate()
                {
                    lastNode = node;
                    SetEnvRow("node", node, !NodeWanted());
                    SetEnvRow("git", git);
                    SetEnvRow("obsidian", obs);

                    // 结果也写进日志，否则点「重新检测」看起来像没反应
                    AddLog("cmd", "环境检测结果：");
                    AddLog(EnvCls(node), "  Node      ：" + EnvDesc(node));
                    AddLog(EnvCls(git), "  Git       ：" + EnvDesc(git));
                    AddLog(EnvCls(obs), "  Obsidian  ：" + EnvDesc(obs));
                    if (git != null && git.Ok)
                        AddLog("ok", "  Git 就绪，可以直接开始部署");
                    else
                        AddLog("warn", "  Git 未安装，第 3 步会自动安装");
                    if (node == null || !node.Ok)
                    {
                        if (NodeWanted()) AddLog("warn", "  Node.js 未安装，第 3 步会自动安装");
                        else AddLog("", "  Node.js 可选：不装也能部署（站点由 GitHub 云端构建）");
                    }

                    Button b = G<Button>("BtnRefresh");
                    if (b != null) b.IsEnabled = true;
                    if (!running) SetStatus("就绪", "#2c3a54");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------------------------------------------------------- 部署

        void StartDeploy()
        {
            if (running) return;

            TextBox tp = G<TextBox>("TxtPath");
            // 用户名不再需要填写：worker 会用令牌查出真实账号
            TextBox tr = G<TextBox>("TxtRepo");
            PasswordBox tk = G<PasswordBox>("TxtToken");
            CheckBox ckNode = G<CheckBox>("ChkNode");
            bool wantNode = ckNode != null && ckNode.IsChecked == true;

            string target = tp.Text.Trim();
            string ownerInput = "";
            string repo = tr.Text.Trim();
            string token = tk.Password.Trim();

            if (target.Length == 0) { AddLog("err", "请先选择部署目录"); return; }
            if (token.Length == 0) { AddLog("err", "请填写 GitHub 访问令牌（点右侧「去创建令牌」）"); return; }
            if (repo.Length == 0) { repo = "my-garden"; tr.Text = repo; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo, "^[A-Za-z0-9._-]+$"))
            {
                AddLog("err", "仓库名只能包含字母、数字、点、下划线和连字符");
                return;
            }
            if (Program.PayloadDir == null)
            {
                AddLog("err", "找不到站点模板，无法部署");
                return;
            }

            // 安装 Git/Obsidian 需要管理员权限。先让界面正常出来，
            // 真正要动系统时才提权重启，并把当前输入带过去。
            if (!IsAdmin())
            {
                MessageBoxResult r = MessageBox.Show(
                    "安装 Git / Obsidian 需要管理员权限。\n\n"
                    + "「是」：以管理员身份重新启动，并自动继续本次部署\n"
                    + "「否」：以普通权限继续（Git / Obsidian 已装好也能成功）",
                    "需要管理员权限", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    Config handoff = new Config();
                    handoff.Target = target;
                    handoff.Owner = ownerInput;
                    handoff.Repo = repo;
                    handoff.Token = token;
                    handoff.InstallNode = wantNode;

                    if (Program.RelaunchElevated(handoff))
                    {
                        AddLog("cmd", "已请求以管理员身份重新启动，本窗口即将关闭…");
                        W.Close();
                        return;
                    }
                    AddLog("warn", "提权失败，改为以普通权限继续");
                }
            }

            StackPanel host = G<StackPanel>("LogHost");
            if (host != null) host.Children.Clear();

            for (int i = 0; i < 6; i++) SetStep(i, "pending", "");
            SetBar(0, "准备中…");
            SetBarSweep(true);
            SetStatus("部署中", "#00e5ff");

            running = true;
            completed = false;
            pagesUrl = "";

            Button start = G<Button>("BtnStart");
            if (start != null) start.IsEnabled = false;
            Button browse = G<Button>("BtnBrowse");
            if (browse != null) browse.IsEnabled = false;
            Button refresh = G<Button>("BtnRefresh");
            if (refresh != null) refresh.IsEnabled = false;
            Button open = G<Button>("BtnOpen");
            if (open != null) open.IsEnabled = false;
            tk.IsEnabled = false;
            if (ckNode != null) ckNode.IsEnabled = false;

            TextBlock hint = G<TextBlock>("HintText");
            if (hint != null) hint.Text = "正在部署，请不要关闭窗口";

            AddLog("hi", "开始部署到 " + target);
            currentTarget = target;

            Config cfg = new Config();
            cfg.Target = target;
            cfg.PayloadDir = Program.PayloadDir;
            cfg.Owner = ownerInput;
            cfg.Repo = repo;
            cfg.Token = token;
            cfg.InstallNode = wantNode;

            job = new Engine(cfg);
            job.OnLog = delegate(string cls, string text) { Post(delegate { AddLog(cls, text); }); };
            job.OnStep = delegate(int i, string state, string sub) { Post(delegate { SetStep(i, state, sub); }); };
            job.OnProgress = delegate(double pct, string label) { Post(delegate { SetBar(pct, label); }); };
            job.OnFinished = delegate(bool ok, string msg) { Post(delegate { OnFinished(ok, msg); }); };

            Thread t = new Thread(delegate() { job.Run(); });
            t.IsBackground = true;
            t.Start();
        }

        void OnFinished(bool ok, string msg)
        {
            running = false;
            completed = true;
            SetBarSweep(false);

            Button start = G<Button>("BtnStart");
            Button browse = G<Button>("BtnBrowse");
            Button refresh = G<Button>("BtnRefresh");
            Button open = G<Button>("BtnOpen");
            PasswordBox tk = G<PasswordBox>("TxtToken");
            CheckBox ckNode = G<CheckBox>("ChkNode");
            TextBlock hint = G<TextBlock>("HintText");

            if (start != null) { start.IsEnabled = true; start.Content = ok ? "重新部署" : "重试"; }
            if (browse != null) browse.IsEnabled = true;
            if (refresh != null) refresh.IsEnabled = true;
            if (tk != null) tk.IsEnabled = true;
            if (ckNode != null) ckNode.IsEnabled = true;

            if (ok)
            {
                SetStatus("部署完成", "#00ffa3");
                SetBar(100, "全部完成");
                AddLog("ok", "—— 部署完成 ——");
                if (msg != null && msg.Length > 0)
                {
                    pagesUrl = msg;
                    AddLog("hi", "站点地址：" + msg);
                    if (open != null)
                    {
                        open.IsEnabled = true;
                        open.Content = "打开我的网站";
                        open.ToolTip = "在浏览器中打开 " + msg + "（刚部署完站点还在构建，通常需要几分钟才能加载，首次可能更久）";
                    }
                }
                if (currentTarget != null && currentTarget.Length > 0)
                {
                    AddLog("hi", "本地笔记库（就是 Git 仓库）位置：" + currentTarget);
                    AddLog("", "  用 Obsidian 打开这个文件夹当仓库即可；.obsidian/plugins 里已装好 Blog Uploader 插件");
                }
                if (hint != null) hint.Text = "站点正在构建中，通常需要几分钟才能打开（首次部署可能更久，请稍候再点「打开我的网站」）";
                AddLog("", "现在就能改笔记：Obsidian 里点左侧 ↑ 图标上传，或右键笔记「上传到博客」");
                AddLog("", "想先看效果（需本机装了 Node）：Obsidian 命令面板 →「本地预览（构建并打开浏览器）」");

                // Obsidian 若最终没装上，在完成汇总里明确提醒，避免用户以为一切就绪
                if (currentTarget != null && currentTarget.Length > 0)
                {
                    string vault = currentTarget;
                    Thread ct = new Thread(delegate()
                    {
                        Tool obs2 = null;
                        try { Env.UpdateSessionPath(); obs2 = Env.FindObsidian(); } catch { }
                        Post(delegate
                        {
                            if (obs2 == null || !obs2.Ok)
                            {
                                AddLog("warn", "提醒：Obsidian 未安装成功，笔记已在本地，手动装好即可继续写作");
                                AddLog("cmd", "  下载地址：https://github.com/obsidianmd/obsidian-releases/releases");
                                AddLog("", "  安装后用 Obsidian 的「打开文件夹作为仓库」选择：" + vault);
                            }
                        });
                    });
                    ct.IsBackground = true;
                    ct.Start();
                }
            }
            else
            {
                SetStatus("部署失败", "#ff4d6d");
                AddLog("err", "—— 部署失败：" + msg + " ——");
                if (hint != null) hint.Text = "修正问题后点「重试」";
            }

            // 刚装好的 Git/Node/Obsidian 要让面板立刻反映出来，不然还显示「未安装」
            RefreshEnvQuiet();
            RefreshThemeLabel();
        }

        // ---------------------------------------------------------------- 界面更新

        SolidColorBrush LogBrush(string cls)
        {
            if (cls == "ok") return Ui.B("#00ffa3");
            if (cls == "warn") return Ui.B("#ffb020");
            if (cls == "err") return Ui.B("#ff4d6d");
            if (cls == "cmd") return Ui.B("#00e5ff");
            if (cls == "hi") return Ui.B("#ffffff");
            return Ui.B("#8aa6c4");
        }

        void AddLog(string cls, string text)
        {
            if (!D.CheckAccess()) { Post(delegate { AddLog(cls, text); }); return; }

            StackPanel host = G<StackPanel>("LogHost");
            ScrollViewer scroll = G<ScrollViewer>("LogScroll");
            if (host == null) return;

            TextBlock tb = new TextBlock();
            tb.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            tb.FontSize = 11;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.Margin = new Thickness(0, 1, 0, 1);

            Run r1 = new Run("[" + DateTime.Now.ToString("HH:mm:ss") + "] ");
            r1.Foreground = Ui.B("#5e7a9e");
            // 在路径分隔符后插入零宽空格，避免超长路径（如插件目录）从中间被硬劈断行
            Run r2 = new Run(text.Replace("\\", "\\\u200B").Replace("/", "/\u200B"));
            r2.Foreground = LogBrush(cls);

            tb.Inlines.Add(r1);
            tb.Inlines.Add(r2);
            host.Children.Add(tb);

            // 日志逐行淡入，避免一下子刷满
            DoubleAnimation fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(170)));
            fade.FillBehavior = FillBehavior.Stop;
            tb.BeginAnimation(UIElement.OpacityProperty, fade);

            while (host.Children.Count > 600) host.Children.RemoveAt(0);
            if (scroll != null) scroll.ScrollToEnd();
        }

        void SetStep(int index, string state, string sub)
        {
            if (!D.CheckAccess()) { Post(delegate { SetStep(index, state, sub); }); return; }

            Ellipse dot = G<Ellipse>("S" + index + "Dot");
            TextBlock name = G<TextBlock>("S" + index + "Name");
            TextBlock subTb = G<TextBlock>("S" + index + "Sub");
            TextBlock mark = G<TextBlock>("S" + index + "Mark");
            Rectangle rail = G<Rectangle>("S" + index + "Rail");
            if (dot == null) return;

            // 完成=✓，失败=✕，其它显示序号（序号给"还没轮到哪一步"提供位置感）
            string num = (index + 1).ToString();

            if (state == "pending")
            {
                dot.Fill = Ui.B("#060a13");
                dot.Stroke = Ui.B("#2c3a54");
                if (name != null) name.Foreground = Ui.B("#4d5f80");
                if (subTb != null) subTb.Foreground = Ui.B("#2c3a54");
                if (rail != null) rail.Fill = VertBrush("#2E00E5FF", "#0700E5FF");
                if (mark != null) { mark.Text = num; mark.Foreground = Ui.B("#43617f"); }
            }
            else if (state == "active")
            {
                dot.Fill = Ui.B("#00e5ff");
                dot.Stroke = Ui.B("#00e5ff");
                if (name != null) name.Foreground = Ui.B("#ffffff");
                if (subTb != null) subTb.Foreground = Ui.B("#0a7d92");
                if (mark != null) { mark.Text = num; mark.Foreground = Ui.B("#04222b"); }
            }
            else if (state == "done")
            {
                dot.Fill = Ui.B("#00ffa3");
                dot.Stroke = Ui.B("#00ffa3");
                if (name != null) name.Foreground = Ui.B("#c6d6ec");
                if (subTb != null) subTb.Foreground = Ui.B("#3d6a5c");
                if (rail != null) rail.Fill = VertBrush("#6B00FFA3", "#1400E5FF");
                if (mark != null) { mark.Text = "✓"; mark.Foreground = Ui.B("#04231a"); }
            }
            else if (state == "fail")
            {
                dot.Fill = Ui.B("#ff4d6d");
                dot.Stroke = Ui.B("#ff4d6d");
                if (name != null) name.Foreground = Ui.B("#ff8ea0");
                if (subTb != null) subTb.Foreground = Ui.B("#ff4d6d");
                if (mark != null) { mark.Text = "✕"; mark.Foreground = Ui.B("#2b0a10"); }
            }

            // 当前步骤加一圈外发光：比单纯变蓝更能一眼定位"卡在哪"
            if (state == "active")
            {
                DropShadowEffect glow = new DropShadowEffect();
                glow.Color = (Color)ColorConverter.ConvertFromString("#00e5ff");
                glow.BlurRadius = 14;
                glow.ShadowDepth = 0;
                glow.Opacity = 0.85;
                dot.Effect = glow;
            }
            else dot.Effect = null;

            PulseDot(dot, state == "active");
            if (subTb != null && sub != null && sub.Length > 0) subTb.Text = sub;
        }

        void SetBar(double pct, string label)
        {
            if (!D.CheckAccess()) { Post(delegate { SetBar(pct, label); }); return; }

            Grid track = G<Grid>("BarTrack");
            Border fill = G<Border>("BarFill");
            TextBlock pctText = G<TextBlock>("PctText");
            TextBlock phase = G<TextBlock>("PhaseText");

            double clamped = Math.Max(0, Math.Min(100, pct));
            double width = 0;
            if (track != null && track.ActualWidth > 0) width = track.ActualWidth * clamped / 100.0;

            if (fill != null) fill.Width = Math.Max(0, width);
            if (pctText != null) pctText.Text = ((int)Math.Round(clamped)) + "%";
            if (phase != null && label != null && label.Length > 0) phase.Text = label;
        }

        void SetStatus(string text, string colorHex)
        {
            TextBlock t = G<TextBlock>("StatusText");
            Ellipse dot = G<Ellipse>("StatusDot");
            if (t != null) { t.Text = text; t.Foreground = Ui.B(colorHex); }
            if (dot != null) dot.Fill = Ui.B(colorHex);
        }

        /// <summary>用户是否勾选了「顺便安装 Node.js」——决定了 Node 是可选还是必需</summary>
        bool NodeWanted()
        {
            CheckBox ck = G<CheckBox>("ChkNode");
            return ck != null && ck.IsChecked == true;
        }

        /// <summary>第 3 步的名字跟着勾选变化：勾了就写「安装 Git 与 Node.js」</summary>
        void SyncDepsStepLabel()
        {
            TextBlock s2 = G<TextBlock>("S2Name");
            if (s2 != null) s2.Text = NodeWanted() ? "安装 Git 与 Node.js" : "安装 Git";
        }

        void RefreshNodeRow()
        {
            SetEnvRow("node", lastNode, !NodeWanted());
        }

        /// <summary>部署结束后静默刷新环境面板（刚装好的 Git/Node/Obsidian 要立刻显示出来）</summary>
        void RefreshEnvQuiet()
        {
            Thread t = new Thread(delegate()
            {
                Tool node = null;
                Tool git = null;
                Tool obs = null;
                try
                {
                    Env.UpdateSessionPath();
                    node = Env.FindNode();
                    git = Env.FindGit();
                    obs = Env.FindObsidian();
                }
                catch { }

                Post(delegate()
                {
                    lastNode = node;
                    SetEnvRow("node", node, !NodeWanted());
                    SetEnvRow("git", git);
                    SetEnvRow("obsidian", obs);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetEnvRow(string key, Tool t, bool optional = false)
        {
            if (!D.CheckAccess()) { Post(delegate { SetEnvRow(key, t); }); return; }

            string tag = key == "node" ? "Node" : (key == "git" ? "Git" : "Obs");
            Ellipse dot = G<Ellipse>("Env" + tag + "Dot");
            TextBlock ver = G<TextBlock>("Env" + tag + "Ver");
            TextBlock pathTb = G<TextBlock>("Env" + tag + "Path");
            if (dot == null) return;

            if (t != null && t.Ok)
            {
                dot.Fill = Ui.B("#00ffa3");
                if (ver != null)
                {
                    ver.Text = t.Version.Length > 0 ? t.Version : "已安装";
                    ver.Foreground = Ui.B("#c6d6ec");
                }
                if (pathTb != null)
                {
                    pathTb.Text = t.FilePath;
                    pathTb.ToolTip = t.FilePath;
                }
            }
            else if (optional)
            {
                // 可选组件缺失：灰点、不呼吸，避免让人误以为出了问题
                dot.Fill = Ui.B("#3f5a75");
                if (ver != null) { ver.Text = "未安装（可选）"; ver.Foreground = Ui.B("#7c99b6"); }
                if (pathTb != null) { pathTb.Text = ""; pathTb.ToolTip = "不装也能部署：站点由 GitHub Actions 在推送后构建"; }
            }
            else
            {
                dot.Fill = Ui.B("#ffb020");
                if (ver != null) { ver.Text = "未安装"; ver.Foreground = Ui.B("#ffb020"); }
                if (pathTb != null) pathTb.Text = "";
            }

            // 缺必需组件时让黄点呼吸，把注意力引过去；装齐了或只是可选组件缺失就停
            PulseDot(dot, !(t != null && t.Ok) && !optional);
        }
    }
}
