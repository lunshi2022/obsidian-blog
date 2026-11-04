using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GardenDeployer
{
    static class Program
    {
        public static string AppDir = "";
        public static string PayloadDir = null;

        /// <summary>当前版本号，与 GitHub Release 的 tag（v1.0）对照判断是否要更新</summary>
        public const string AppVersion = "1.0";
        /// <summary>检查更新用的 Release 源</summary>
        public const string UpdateRepo = "lunshi2022/obsidian-blog";

        /// <summary>启动跟踪日志，出问题时用户把这个文件发回来即可定位</summary>
        public static string LogPath = "";

        // 不嵌应用清单（清单被加载器判定非法会导致“并行配置不正确”，exe 根本起不来），
        // 高 DPI 改在这里用 API 打开。
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void Trace(string msg)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            LogPath = Path.Combine(Path.GetTempPath(), "garden-deployer-error.log");
            try { File.Delete(LogPath); } catch { }
            Trace("=== 启动 ===");
            Trace("args: " + string.Join(" ", args));

            try { SetProcessDPIAware(); } catch (Exception ex) { Trace("SetProcessDPIAware 失败: " + ex.Message); }

            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch (Exception ex) { Trace("TLS 设置失败: " + ex.Message); }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Trace("!!! 未处理异常: " + e.ExceptionObject);
            };

            bool selfTest = false;
            string elevatedPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                else if (string.Equals(args[i], "--elevated", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    elevatedPath = args[i + 1];
                    i++;
                }
            }

            try
            {
                AppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch (Exception ex)
            {
                Trace("取程序目录失败: " + ex.Message);
                AppDir = Environment.CurrentDirectory;
            }
            PayloadDir = ResolvePayload(AppDir);
            Trace("程序目录: " + AppDir);
            Trace("模板目录: " + (PayloadDir == null ? "(未找到)" : PayloadDir));

            if (selfTest) return SelfTest();

            // 以管理员身份重启后带过来的参数：读出来后立刻删掉临时文件
            Config pending = null;
            if (!string.IsNullOrEmpty(elevatedPath))
            {
                Trace("读取提权配置: " + elevatedPath);
                try
                {
                    pending = ReadConfigFile(elevatedPath);
                    Trace("提权配置读取成功, target=" + pending.Target);
                }
                catch (Exception ex) { Trace("读配置失败: " + ex.Message); }
                try { File.Delete(elevatedPath); } catch { }
            }

            Trace("管理员=" + Shell.IsAdmin());

            try
            {
                Trace("创建 Application");
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;

                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    Trace("!!! 界面线程异常: " + e.Exception);
                    try
                    {
                        MessageBox.Show("程序遇到一个错误：\n\n" + e.Exception.Message
                            + "\n\n详细信息已写入：\n" + LogPath, "数字花园部署器",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    catch { }
                    e.Handled = true;
                };

                Trace("加载界面");
                Window win = Ui.LoadWindow();
                Trace("界面加载成功 " + win.Width + "x" + win.Height);
                Ui.ApplyIcon(win);
                Ui.ClampToScreen(win);

                app.MainWindow = win;
                Trace("创建窗口控制器");
                Shell shell = new Shell(win);
                shell.Pending = pending;
                Trace("显示窗口");

                app.Run(win);
                Trace("=== 正常退出 ===");
                return 0;
            }
            catch (Exception ex)
            {
                Trace("!!! 启动失败: " + ex);
                Ui.Fatal("启动失败：" + ex.Message);
                return 1;
            }
        }

        /// <summary>以管理员身份重启自己，并把当前输入写进临时配置传过去</summary>
        public static bool RelaunchElevated(Config cfg)
        {
            try
            {
                string cfgPath = Path.Combine(Path.GetTempPath(),
                    "garden-elevated-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cfg");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("target=" + (cfg.Target == null ? "" : cfg.Target));
                sb.AppendLine("owner=" + (cfg.Owner == null ? "" : cfg.Owner));
                sb.AppendLine("repo=" + (cfg.Repo == null ? "" : cfg.Repo));
                sb.AppendLine("token=" + (cfg.Token == null ? "" : cfg.Token));
                sb.AppendLine("node=" + (cfg.InstallNode ? "1" : "0"));
                File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Assembly.GetExecutingAssembly().Location;
                psi.Arguments = "--elevated \"" + cfgPath + "\"";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Trace("已请求以管理员身份重启");
                return true;
            }
            catch (Exception ex)
            {
                Trace("提权失败: " + ex.Message);
                return false;
            }
        }

        static Config ReadConfigFile(string path)
        {
            Config c = new Config();
            c.PayloadDir = PayloadDir;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                string k = lines[i].Substring(0, eq).Trim();
                string v = lines[i].Substring(eq + 1);
                if (k == "target") c.Target = v;
                else if (k == "owner") c.Owner = v;
                else if (k == "repo") c.Repo = v;
                else if (k == "token") c.Token = v;
                else if (k == "node") c.InstallNode = (v.Trim() == "1");
            }
            return c;
        }

        /// <summary>站点模板的三种来源：程序目录的上一级、程序目录、程序目录下的 payload\；
        /// 都没有就用编译时内嵌的 payload.zip（这样用户只下载一个 exe 也能部署）。</summary>
        static string ResolvePayload(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) return null;
            string parent = "";
            try { parent = Path.GetDirectoryName(appDir.TrimEnd('\\')); } catch { }

            string[] cands = new string[] { parent, Path.Combine(appDir, "payload"), appDir };
            for (int i = 0; i < cands.Length; i++)
            {
                string c = cands[i];
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    if (File.Exists(Path.Combine(c, @"src\build.js"))) return c;
                }
                catch { }
            }

            // 磁盘上没有模板 → 用内嵌的那份
            return ExtractEmbeddedPayload();
        }

        /// <summary>把编译进 exe 的 payload.zip 解压到临时目录并返回路径。
        /// 用资源长度做版本戳：换了新 exe 会自动重新解压，不会用到旧模板。</summary>
        static string ExtractEmbeddedPayload()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string res = null;
                string[] all = asm.GetManifestResourceNames();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase)) { res = all[i]; break; }
                }
                if (res == null)
                {
                    Trace("exe 内没有 payload.zip（编译时未嵌入模板）");
                    return null;
                }

                long len = 0;
                using (Stream probe = asm.GetManifestResourceStream(res)) { len = probe.Length; }

                string dir = Path.Combine(Path.GetTempPath(), "garden-payload");
                string stamp = Path.Combine(dir, ".payload-stamp");
                string entry = Path.Combine(dir, @"src\build.js");

                // 已经解压过且版本一致：直接用
                if (File.Exists(entry) && File.Exists(stamp))
                {
                    try { if (File.ReadAllText(stamp).Trim() == len.ToString()) return dir; } catch { }
                }

                Trace("解压内嵌模板到 " + dir + "（" + len + " 字节）");
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                Directory.CreateDirectory(dir);

                string zip = Path.Combine(Path.GetTempPath(), "garden-payload-" + len + ".zip");
                using (Stream s = asm.GetManifestResourceStream(res))
                using (FileStream fs = File.Create(zip))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }

                ZipFile.ExtractToDirectory(zip, dir, Encoding.UTF8);
                try { File.Delete(zip); } catch { }
                File.WriteAllText(stamp, len.ToString(), new UTF8Encoding(false));

                if (File.Exists(entry))
                {
                    Trace("内嵌模板就绪: " + dir);
                    return dir;
                }
                Trace("解压完成但缺少 src\\build.js");
                return null;
            }
            catch (Exception ex)
            {
                Trace("解压内嵌模板失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>命令行自检：验证界面资源、控件、环境探测是否正常，结果写入 %TEMP%\garden-selftest.txt</summary>
        static int SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            string outFile = Path.Combine(Path.GetTempPath(), "garden-selftest.txt");
            sb.AppendLine("数字花园部署器 · 自检");
            sb.AppendLine("程序目录：" + AppDir);
            sb.AppendLine("模板目录：" + (PayloadDir == null ? "未找到" : PayloadDir));
            sb.AppendLine("管理员权限：" + Shell.IsAdmin());
            sb.AppendLine("");

            try
            {
                Application app = new Application();
                Window w = Ui.LoadWindow();
                sb.AppendLine("界面加载：OK  " + w.Width + " x " + w.Height);

                string[] names = new string[] {
                    "HeaderBar", "StatusDot", "StatusText", "StatusPill", "BtnMin", "BtnClose",
                    "TxtPath", "BtnBrowse", "TxtRepo", "TxtToken", "BtnToken",
                    "EnvNodeDot", "EnvNodeVer", "EnvNodePath",
                    "EnvGitDot", "EnvGitVer", "EnvGitPath",
                    "EnvObsDot", "EnvObsVer", "EnvObsPath", "BtnRefresh",
                    "LogHost", "LogScroll", "BtnClearLog",
                    "BarFill", "BarGlow", "BarTrack", "PctText", "PhaseText",
                    "BtnStart", "BtnOpen", "BtnQuit", "HintText", "GaugeTarget"
                };

                int missing = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    if (w.FindName(names[i]) == null)
                    {
                        sb.AppendLine("  缺失控件：" + names[i]);
                        missing++;
                    }
                }
                for (int i = 0; i < 6; i++)
                {
                    string[] suffixes = new string[] { "Dot", "Name", "Sub" };
                    for (int j = 0; j < suffixes.Length; j++)
                    {
                        string n = "S" + i + suffixes[j];
                        if (w.FindName(n) == null)
                        {
                            sb.AppendLine("  缺失控件：" + n);
                            missing++;
                        }
                    }
                }
                sb.AppendLine("控件检查：缺失 " + missing + " 个（共 " + (names.Length + 18) + " 个）");

                Env.UpdateSessionPath();
                Tool node = Env.FindNode();
                Tool git = Env.FindGit();
                Tool obs = Env.FindObsidian();
                sb.AppendLine("Node      ：" + node.Describe());
                sb.AppendLine("Git       ：" + git.Describe());
                sb.AppendLine("Obsidian  ：" + obs.Describe());

                bool ok = (missing == 0) && (PayloadDir != null);
                sb.AppendLine("");
                sb.AppendLine("结论：" + (ok ? "OK" : "有问题，请检查上面的项目"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常：" + ex.ToString());
            }

            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
            try { Console.WriteLine(sb.ToString()); }
            catch { }
            return 0;
        }
    }

    static class Ui
    {
        public static Window LoadWindow()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] all = asm.GetManifestResourceNames();

            // 不同版本的编译器对 /resource 的资源命名不一样，按三级匹配兜底
            string res = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].EndsWith("ui.xaml", StringComparison.OrdinalIgnoreCase)) { res = all[i]; break; }
            }
            if (res == null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) { res = all[i]; break; }
                }
            }
            if (res == null)
            {
                throw new Exception("程序内缺少界面资源 ui.xaml。现有资源："
                    + (all.Length == 0 ? "（无）" : string.Join(", ", all)));
            }

            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) throw new Exception("无法读取资源流：" + res);
                return (Window)XamlReader.Load(s);
            }
        }

        public static SolidColorBrush B(string hex)
        {
            object c = ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush((Color)c);
        }

        /// <summary>窗口尺寸自适应：屏幕比我设计的还小时自动缩小，避免底栏跑到屏幕外</summary>
        public static void ClampToScreen(Window w)
        {
            try
            {
                Rect area = SystemParameters.WorkArea;
                if (w.Height > area.Height - 16) w.Height = Math.Max(560, area.Height - 16);
                if (w.Width > area.Width - 16) w.Width = Math.Max(900, area.Width - 16);
                Program.Trace("窗口尺寸 " + w.Width + "x" + w.Height
                    + "（工作区 " + area.Width + "x" + area.Height + "）");
            }
            catch (Exception ex)
            {
                Program.Trace("尺寸自适应失败: " + ex.Message);
            }
        }

        /// <summary>把内嵌的 app.ico 设为窗口图标：任务栏和 Alt+Tab 都靠它，
        /// 不依赖 exe 资源能否被 shell 正确解析。</summary>
        public static void ApplyIcon(Window w)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] all = asm.GetManifestResourceNames();
                for (int i = 0; i < all.Length; i++)
                {
                    if (!all[i].EndsWith("app.ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream s = asm.GetManifestResourceStream(all[i]))
                    {
                        if (s == null) return;
                        IconBitmapDecoder dec = new IconBitmapDecoder(s,
                            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        if (dec.Frames.Count > 0) w.Icon = dec.Frames[dec.Frames.Count - 1];
                        Program.Trace("窗口图标已设置，帧数=" + dec.Frames.Count);
                    }
                    return;
                }
                Program.Trace("未找到内嵌 app.ico 资源，图标沿用 exe 自带");
            }
            catch (Exception ex)
            {
                Program.Trace("设置窗口图标失败: " + ex.Message);
            }
        }

        public static void Fatal(string msg)
        {
            try
            {
                File.AppendAllText(Program.LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            try
            {
                MessageBox.Show(msg + "\n\n详细信息已写入：\n" + Program.LogPath, "数字花园部署器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
    }
}
