using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// 注意：随 Windows 自带的 csc 只支持 C# 5，
// 这里刻意不用字符串插值、?. 、nameof 等新语法。
namespace GardenDeployer
{
    sealed class Tool
    {
        public bool Ok;
        public string Version = "";
        public string FilePath = "";

        public string Describe()
        {
            if (!Ok) return "未安装";
            return Version + "   " + FilePath;
        }
    }

    sealed class Config
    {
        public string Target = "";
        public string PayloadDir = "";
        public string Owner = "";
        public string Repo = "";
        public string Token = "";
        public bool DryRun = false;   // 调试用：置 true 则只走流程不实际安装/推送
        public bool InstallNode = false;  // 用户勾选「顺便安装 Node.js」才装，默认不装
    }

    // ---------------------------------------------------------------- 环境探测

    static class Env
    {
        /// <summary>把注册表里的 Machine/User PATH 合并进当前进程（装完工具后调用才生效）</summary>
        public static void UpdateSessionPath()
        {
            StringBuilder sb = new StringBuilder();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string current = Environment.GetEnvironmentVariable("Path");
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);

            string[] parts = new string[] { current, machine, user };
            foreach (string block in parts)
            {
                if (string.IsNullOrEmpty(block)) continue;
                foreach (string raw in block.Split(';'))
                {
                    string p = raw.Trim().Trim('"').TrimEnd('\\');
                    if (p.Length == 0) continue;
                    if (seen.Contains(p)) continue;
                    seen.Add(p);
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(p);
                }
            }
            try { Environment.SetEnvironmentVariable("Path", sb.ToString(), EnvironmentVariableTarget.Process); }
            catch { }
        }

        static bool FileOk(string p)
        {
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }

        /// <summary>在 PATH 里找可执行文件（不依赖 where.exe）</summary>
        static string WhichExe(string name)
        {
            string path = Environment.GetEnvironmentVariable("Path");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string raw in path.Split(';'))
            {
                string d = raw.Trim().Trim('"');
                if (d.Length == 0) continue;
                try
                {
                    string c1 = Path.Combine(d, name + ".exe");
                    if (File.Exists(c1)) return c1;
                    string c2 = Path.Combine(d, name + ".cmd");
                    if (File.Exists(c2)) return c2;
                }
                catch { }
            }
            return null;
        }

        static string FindExe(string name, string[] candidates)
        {
            string hit = WhichExe(name);
            if (hit != null) return hit;
            if (candidates != null)
            {
                foreach (string c in candidates)
                {
                    if (FileOk(c)) return c;
                }
            }
            return null;
        }

        static string ProgramFiles()
        {
            string p = Environment.GetEnvironmentVariable("ProgramFiles");
            return p == null ? "" : p;
        }

        static string ProgramFilesX86()
        {
            string p = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            return p == null ? "" : p;
        }

        static string LocalAppData()
        {
            string p = Environment.GetEnvironmentVariable("LocalAppData");
            if (string.IsNullOrEmpty(p)) p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return p == null ? "" : p;
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return null;
            return Path.Combine(a, b);
        }

        /// <summary>从注册表读安装目录：winget/MSI 刚装完、PATH 还没刷新时靠它</summary>
        public static string RegInstallPath(string[] keys)
        {
            foreach (string key in keys)
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (rk != null)
                        {
                            string v = rk.GetValue("InstallPath") as string;
                            if (FileOk(v)) return v;
                        }
                    }
                    using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(key))
                    {
                        if (rk != null)
                        {
                            string v = rk.GetValue("InstallPath") as string;
                            if (FileOk(v)) return v;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public static Tool FindNode()
        {
            Tool t = new Tool();
            string reg = RegInstallPath(new string[] { @"SOFTWARE\Node.js", @"SOFTWARE\WOW6432Node\Node.js" });
            string exe = FindExe("node", new string[] {
                Join(ProgramFiles(), @"nodejs\node.exe"),
                Join(ProgramFilesX86(), @"nodejs\node.exe"),
                Join(LocalAppData(), @"Programs\nodejs\node.exe"),
                Join(reg, "node.exe")
            });
            if (exe == null) return t;

            t.Ok = true;
            t.FilePath = exe;
            string outp;
            try
            {
                Proc.Run(exe, "--version", null, out outp);
                t.Version = outp.Trim();
            }
            catch { }
            return t;
        }

        public static Tool FindGit()
        {
            Tool t = new Tool();
            string reg = RegInstallPath(new string[] { @"SOFTWARE\GitForWindows", @"SOFTWARE\WOW6432Node\GitForWindows" });
            string exe = FindExe("git", new string[] {
                Join(ProgramFiles(), @"Git\cmd\git.exe"),
                Join(ProgramFilesX86(), @"Git\cmd\git.exe"),
                Join(LocalAppData(), @"Programs\Git\cmd\git.exe"),
                Join(reg, @"cmd\git.exe")
            });
            if (exe == null) return t;

            t.Ok = true;
            t.FilePath = exe;
            string outp;
            try
            {
                Proc.Run(exe, "--version", null, out outp);
                t.Version = outp.Trim();
            }
            catch { }
            return t;
        }

        public static Tool FindObsidian()
        {
            Tool t = new Tool();
            string[] cands = new string[] {
                Join(LocalAppData(), @"Obsidian\Obsidian.exe"),
                Join(ProgramFiles(), @"Obsidian\Obsidian.exe"),
                Join(ProgramFilesX86(), @"Obsidian\Obsidian.exe")
            };
            string exe = null;
            foreach (string c in cands)
            {
                if (FileOk(c)) { exe = c; break; }
            }

            if (exe == null)
            {
                exe = FindInUninstall(RegistryHive.CurrentUser, RegistryView.Default);
                if (exe == null) exe = FindInUninstall(RegistryHive.LocalMachine, RegistryView.Registry64);
                if (exe == null) exe = FindInUninstall(RegistryHive.LocalMachine, RegistryView.Registry32);
            }
            if (exe == null) return t;

            t.Ok = true;
            t.FilePath = exe;
            try
            {
                t.Version = "v" + FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            }
            catch { }
            return t;
        }

        static string FindInUninstall(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (un == null) return null;
                    foreach (string name in un.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sub = un.OpenSubKey(name))
                            {
                                if (sub == null) continue;
                                string dn = sub.GetValue("DisplayName") as string;
                                if (dn == null || !dn.StartsWith("Obsidian", StringComparison.OrdinalIgnoreCase)) continue;
                                string icon = sub.GetValue("DisplayIcon") as string;
                                if (string.IsNullOrEmpty(icon)) continue;
                                string p = icon.Split(',')[0].Trim().Trim('"');
                                if (FileOk(p)) return p;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ---------------------------------------------------------------- 进程与网络

    static class Proc
    {
        public static int Run(string exe, string args, string workdir, out string output)
        {
            return RunInternal(exe, args, workdir, null, out output);
        }

        public static int RunWithInput(string exe, string args, string workdir, string input, out string output)
        {
            return RunInternal(exe, args, workdir, input, out output, 0);
        }

        /// <summary>带超时的变体：timeoutMs&gt;0 时若子进程未在限定时间内退出，会强制 Kill 并以 exit -1 返回，避免部署卡死。</summary>
        public static int RunWithInput(string exe, string args, string workdir, string input, out string output, int timeoutMs)
        {
            return RunInternal(exe, args, workdir, input, out output, timeoutMs);
        }

        static int RunInternal(string exe, string args, string workdir, string input, out string output, int timeoutMs = 0)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = input != null;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(workdir)) psi.WorkingDirectory = workdir;

            using (Process p = Process.Start(psi))
            {
                // 先写 stdin 再读输出，避免子进程等待输入而本端等待输出造成的死锁
                if (input != null)
                {
                    try
                    {
                        // 写入并关闭 stdin（using 会 flush 并关闭底层流，向子进程发送 EOF）
                        using (var sw = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
                        {
                            sw.Write(input);
                        }
                    }
                    catch { }
                }
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                bool exited;
                if (timeoutMs > 0) exited = p.WaitForExit(timeoutMs);
                else { p.WaitForExit(); exited = true; }
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    output = (so + "\n" + se + "\n[进程超时已被终止]").Trim();
                    return -1;
                }
                output = (so + "\n" + se).Trim();
                return p.ExitCode;
            }
        }

        public static int RunShell(string file, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }

    /// <summary>带超时的 WebClient，避免下载卡死</summary>
    sealed class TimeoutClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            r.Timeout = 30000;
            HttpWebRequest h = r as HttpWebRequest;
            if (h != null) h.ReadWriteTimeout = 300000;
            return r;
        }
    }

    // ---------------------------------------------------------------- 部署引擎

    sealed class Engine
    {
        public Action<string, string> OnLog;      // cls, text
        public Action<int, string, string> OnStep; // index, state(sub), sub
        public Action<double, string> OnProgress;
        public Action<bool, string> OnFinished;

        public string PagesUrl = "";
        public string RepoUrl = "";

        readonly Config C;
        string owner = "";
        string tmpDir = "";
        volatile bool cancelled;

        static readonly string[] STEP_NAMES = new string[] {
            "环境检测", "准备部署目录", "安装 Git（Node 可选）", "构建站点", "创建仓库并推送", "安装 Obsidian 与插件"
        };

        /// <summary>写入 Obsidian 的 CSS 片段，在文件浏览器里隐藏博客引擎的系统文件夹，只保留 content/ 下的笔记。
        /// 仅影响文件树显示，不影响站点构建与附件引用。</summary>
        const string HIDE_SYSTEM_CSS =
            "/* 隐藏博客引擎系统文件，只保留 content/ 下的笔记 */\n" +
            "/* 用 :has() 隐藏整个文件夹容器，只隐藏标题行会留下悬空的子项 */\n" +
            ".nav-folder:has(> .nav-folder-title[data-path=\"src\"]),\n" +
            ".nav-folder:has(> .nav-folder-title[data-path=\"assets\"]),\n" +
            ".nav-folder:has(> .nav-folder-title[data-path=\"blog-uploader\"]),\n.nav-folder:has(> .nav-folder-title[data-path=\"site\"]),\n" +
            ".nav-file:has(> .nav-file-title[data-path=\"README.md\"]),\n" +
            ".nav-file:has(> .nav-file-title[data-path=\"package.json\"]),\n" +
            ".nav-file:has(> .nav-file-title[data-path=\"site.config.json\"]),\n" +
            ".nav-file:has(> .nav-file-title[data-path=\"site_config.json\"]) {\n" +
            "  display: none;\n" +
            "}\n";

        public Engine(Config cfg)
        {
            C = cfg;
        }

        public void Cancel() { cancelled = true; }

        // ----- 输出 -----
        void Log(string cls, string text) { if (OnLog != null) OnLog(cls, text); }
        void Step(int idx, string state, string sub) { if (OnStep != null) OnStep(idx, state, sub); }
        void Progress(int step, double inner, string label)
        {
            if (OnProgress == null) return;
            double basePct = step * 100.0 / STEP_NAMES.Length;
            double pct = basePct + (Math.Max(0, Math.Min(100, inner)) / 100.0) * (100.0 / STEP_NAMES.Length);
            OnProgress(pct, label);
        }
        void Finish(bool ok, string msg) { if (OnFinished != null) OnFinished(ok, msg); }

        // ----- 主流程 -----
        public void Run()
        {
            int current = 0;
            try
            {
                tmpDir = Path.Combine(Path.GetTempPath(), "garden-deploy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmpDir);

                Log("hi", "数字花园部署器 v" + Program.AppVersion);
                Log("", "目标目录：" + C.Target);
                if (C.DryRun) Log("warn", "模拟模式：不会真的安装或推送");

                for (int i = 0; i < STEP_NAMES.Length; i++) Step(i, "pending", "");

                current = 0; StepEnvironment();
                current = 1; StepPrepare();
                current = 2; StepDependencies();
                current = 3; StepBuild();
                current = 4; StepGitHub();
                current = 5; StepObsidian();

                if (OnProgress != null) OnProgress(100, "部署完成");
                Log("", "");
                Log("ok", "全部完成");
                if (PagesUrl.Length > 0) Log("hi", "站点地址：" + PagesUrl);
                Log("warn", "站点正在构建，通常需要几分钟才能在浏览器打开（首次部署可能更久，请稍候再访问）。");
                Finish(true, PagesUrl);
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Step(current, "fail", msg);
                Log("err", "失败：" + msg);
                Finish(false, msg);
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ----- 步骤 0：环境 -----
        void StepEnvironment()
        {
            Step(0, "active", "正在读取系统信息");
            Log("hi", "检查运行环境");

            Log("", "  系统：" + Environment.OSVersion.VersionString);
            bool admin = Shell.IsAdmin();
            if (admin) Log("ok", "  管理员权限：是");
            else Log("warn", "  管理员权限：否（安装系统组件可能失败）");

            try
            {
                string root = Path.GetPathRoot(C.Target);
                if (!string.IsNullOrEmpty(root))
                {
                    DriveInfo di = new DriveInfo(root);
                    if (di.IsReady)
                    {
                        double freeGb = di.AvailableFreeSpace / 1073741824.0;
                        Log("", "  磁盘剩余：" + freeGb.ToString("0.0") + " GB");
                        if (freeGb < 1) Log("warn", "  磁盘空间不足 1GB");
                    }
                }
            }
            catch { }

            try
            {
                using (TimeoutClient wc = new TimeoutClient())
                {
                    wc.Headers.Add("User-Agent", "garden-deployer");
                    wc.DownloadString("https://api.github.com/zen");
                }
                Log("ok", "  网络：可访问 GitHub");
            }
            catch
            {
                Log("warn", "  网络：无法访问 GitHub API，后续推送可能失败");
            }

            Env.UpdateSessionPath();
            Tool node = Env.FindNode();
            Tool git = Env.FindGit();
            Tool obs = Env.FindObsidian();
            LogTool("node", node);
            LogTool("git", git);
            LogTool("obsidian", obs);

            // 立刻校验令牌，后面写站点地址和建仓都要用真实用户名
            if (!string.IsNullOrEmpty(C.Token))
            {
                string json;
                try
                {
                    json = HttpGet("https://api.github.com/user", C.Token);
                }
                catch (Exception ex)
                {
                    // 把 401 翻译成人话：这一步是第 1 步就卡住，提示必须能直接照着做
                    if (ex.Message.IndexOf("HTTP 401", StringComparison.Ordinal) >= 0)
                    {
                        throw new Exception("令牌无效或已过期。请点「去创建」重新生成一个"
                            + "（勾选 repo + workflow，有效期建议 90 天），再粘贴回来");
                    }
                    throw;
                }
                Match m = Regex.Match(json, "\"login\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) throw new Exception("令牌可用但无法解析用户名");
                owner = m.Groups[1].Value;
                Log("ok", "  GitHub 身份：" + owner);
            }
            else
            {
                owner = C.Owner;
            }

            Step(0, "done", "环境检测完成");
            Progress(0, 100, "环境检测完成");
        }

        void LogTool(string key, Tool t)
        {
            if (t.Ok) Log("ok", "  " + Label(key) + "：" + t.Version);
            else if (key == "node") Log("", "  Node：未安装（可选，不影响发布）");
            else Log("warn", "  " + Label(key) + "：未安装");
        }

        public static string Label(string key)
        {
            if (key == "node") return "Node";
            if (key == "git") return "Git";
            if (key == "obsidian") return "Obsidian";
            return key;
        }

        // ----- 步骤 1：准备目录 -----
        void StepPrepare()
        {
            Step(1, "active", "正在复制站点模板");
            Log("hi", "准备部署目录");

            if (!Directory.Exists(C.Target))
            {
                Directory.CreateDirectory(C.Target);
                Log("ok", "  已创建目录：" + C.Target);
            }
            else Log("", "  目录已存在：" + C.Target);

            string build = Path.Combine(C.PayloadDir, @"src\build.js");
            if (!File.Exists(build)) throw new Exception("模板目录无效（缺少 src\\build.js）：" + C.PayloadDir);

            bool inPlace = string.Equals(
                Path.GetFullPath(C.PayloadDir).TrimEnd('\\'),
                Path.GetFullPath(C.Target).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            if (inPlace)
            {
                Log("warn", "  模板与目标相同，跳过复制（就地部署）");
            }
            else
            {
                string[] items = new string[] {
                    "src", "assets", "content", "blog-uploader", ".github",
                    "site.config.json", "package.json", "README.md", ".gitignore"
                };
                for (int i = 0; i < items.Length; i++)
                {
                    Progress(1, 10 + 70.0 * i / items.Length, "复制 " + items[i]);
                    string s = Path.Combine(C.PayloadDir, items[i]);
                    string d = Path.Combine(C.Target, items[i]);
                    if (File.Exists(s))
                    {
                        File.Copy(s, d, true);
                    }
                    else if (Directory.Exists(s))
                    {
                        CopyDir(s, d);
                    }
                    else
                    {
                        Log("warn", "  跳过缺失项：" + items[i]);
                    }
                }
                Log("ok", "  站点模板复制完成");
            }

            // 站点地址
            string cfgFile = Path.Combine(C.Target, "site.config.json");
            if (File.Exists(cfgFile))
            {
                try
                {
                    string useOwner = owner.Length > 0 ? owner : "github-user";
                    string pages = "https://" + useOwner + ".github.io/" + C.Repo + "/";
                    string txt = File.ReadAllText(cfgFile, Encoding.UTF8);
                    txt = SetJsonString(txt, "siteUrl", pages);
                    if (txt.IndexOf("\"你的名字\"") >= 0)
                        txt = SetJsonString(txt, "author", useOwner);
                    File.WriteAllText(cfgFile, txt, new UTF8Encoding(false));
                    Log("ok", "  已写入站点地址：" + pages);
                }
                catch (Exception ex)
                {
                    Log("warn", "  站点配置更新失败：" + ex.Message);
                }
            }

            Step(1, "done", "目录就绪");
            Progress(1, 100, "目录就绪");
        }

        /// <summary>替换 JSON 里的某个字符串字段，找不到就当作没有</summary>
        static string SetJsonString(string json, string key, string value)
        {
            string pattern = "\"" + key + "\"\\s*:\\s*\"[^\"]*\"";
            string repl = "\"" + key + "\": \"" + EscapeJson(value) + "\"";
            if (!Regex.IsMatch(json, pattern)) return json;
            return Regex.Replace(json, pattern, repl);
        }

        /// <summary>同上，但字段不存在时插入到 JSON 末尾（用于给老配置补新字段）</summary>
        static string SetJsonStringOrAdd(string json, string key, string value)
        {
            string pattern = "\"" + key + "\"\\s*:\\s*\"[^\"]*\"";
            string repl = "\"" + key + "\": \"" + EscapeJson(value) + "\"";
            if (Regex.IsMatch(json, pattern)) return Regex.Replace(json, pattern, repl);
            int close = json.LastIndexOf('}');
            if (close < 0) return json;
            string head = json.Substring(0, close).TrimEnd();
            string sep = head.EndsWith("{") ? "" : ",";
            return head + sep + "\n  " + repl + "\n}" + json.Substring(close + 1);
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }

        // ----- 步骤 2：依赖 -----
        void StepDependencies()
        {
            Step(2, "active", "正在检测 Node 与 Git");
            Log("hi", "安装依赖");
            Env.UpdateSessionPath();

            Tool node = Env.FindNode();
            Tool git = Env.FindGit();

            if (node.Ok && git.Ok)
            {
                Log("ok", "  Node 与 Git 均已安装，跳过");
                Step(2, "done", "依赖齐全");
                Progress(2, 100, "依赖齐全");
                return;
            }

            if (C.DryRun)
            {
                List<string> miss = new List<string>();
                if (!git.Ok) miss.Add("Git");
                Log("warn", "  模拟：本应安装 " + (miss.Count > 0 ? string.Join("、", miss.ToArray()) : "（无）"));
                if (!node.Ok) Log("", "    Node.js 未安装：可选，不装也能部署");
                Step(2, "done", "模拟跳过");
                Progress(2, 100, "模拟跳过");
                return;
            }

            // Node 是可选组件：默认不装，只有用户勾了「顺便安装 Node.js」才装。
            // 本机已装则无论勾没勾都会用它做一次本地构建（推送前提前发现错误）。
            if (!node.Ok && C.InstallNode)
            {
                Progress(2, 10, "安装 Node.js…");
                // 顺序：国内镜像（npmmirror，满速，不走 GitHub）→ winget → 继续（不影响发布）
                bool installed = false;
                try
                {
                    InstallNodePackage();
                    installed = true;
                }
                catch (Exception ex)
                {
                    Log("warn", "  镜像安装未成功：" + ex.Message);
                }
                if (!installed && !InstallViaWinget("OpenJS.NodeJS.LTS", "Node.js LTS", 2, 10, 45))
                {
                    Log("warn", "  winget 也未成功，继续部署（不影响发布）");
                }
                Env.UpdateSessionPath();
                node = Env.FindNode();
                if (node.Ok) Log("ok", "  Node 就绪：" + node.Version);
                else Log("warn", "  Node.js 安装未生效，继续部署（不影响发布）");
            }
            if (!node.Ok)
            {
                Log("warn", "  未安装 Node.js（可选，不影响发布）");
                Log("", "    站点由 GitHub Actions 在推送时自动构建");
                Log("", "    想在推送前本地预览，可勾选上面的选项或自行安装");
            }
            else
            {
                Log("ok", "  Node 就绪：" + node.Version);
            }

            if (cancelled) throw new Exception("已取消");

            if (!git.Ok)
            {
                Progress(2, 55, "安装 Git…");

                // 顺序有讲究：先用国内镜像拿官方安装包（通常满速），
                // 失败再交给 winget，最后才走 GitHub 直连（国内可能只有几十 KB/s）。
                bool installed = false;
                try
                {
                    InstallGitPackage();
                    installed = true;
                }
                catch (Exception ex)
                {
                    Log("warn", "  镜像安装未成功：" + ex.Message);
                }

                if (!installed && !InstallViaWinget("Git.Git", "Git", 2, 55, 85))
                {
                    throw new Exception("Git 安装失败，请手动安装后重试");
                }

                Env.UpdateSessionPath();
                git = Env.FindGit();
                if (!git.Ok) throw new Exception("Git 安装后仍未检测到，请手动安装后重试");
                Log("ok", "  Git 就绪：" + git.Version);
            }

            Step(2, "done", "依赖安装完成");
            Progress(2, 100, "依赖安装完成");
        }

        bool InstallViaWinget(string id, string label, int step, double from, double to)
        {
            string winget = WhichWinget();
            if (winget == null)
            {
                Log("warn", "  未找到 winget，改用离线安装包");
                return false;
            }
            Log("cmd", "  winget install " + id);
            Progress(step, from + 5, "winget 安装 " + label + "…");

            // 不指定 --scope：让 winget 自己选（Node/Git 装机器级，Obsidian 装用户级），
            // 强制机器级反而会让部分包安装失败
            string args = "install --id " + id + " -e --silent "
                        + "--accept-package-agreements --accept-source-agreements --disable-interactivity";
            string outp;
            int code;
            try
            {
                code = Proc.Run(winget, args, null, out outp);
            }
            catch (Exception ex)
            {
                Log("warn", "  winget 调用失败：" + ex.Message);
                return false;
            }

            if (code == 0)
            {
                Log("ok", "  winget 安装成功");
                Progress(step, to, label + " 安装完成");
                return true;
            }
            // -1978335199 / -1978335189 ≈ 已安装
            if (code == -1978335199 || code == -1978335189)
            {
                Log("warn", "  winget 报告已安装");
                return true;
            }
            Log("warn", "  winget 失败（exit " + code + "），改用离线安装包");
            return false;
        }

        static string WhichWinget()
        {
            string p = Environment.GetEnvironmentVariable("Path");
            if (p != null)
            {
                foreach (string raw in p.Split(';'))
                {
                    string d = raw.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    try
                    {
                        string c = Path.Combine(d, "winget.exe");
                        if (File.Exists(c)) return c;
                    }
                    catch { }
                }
            }
            // WindowsApps 下的别名
            try
            {
                string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\winget.exe");
                if (File.Exists(alias)) return alias;
            }
            catch { }
            return null;
        }

        /// <summary>Git for Windows 官方安装包约 67 MB。从 GitHub 直连在国内常常只有几十 KB/s，
        /// 所以优先从 npmmirror（阿里维护的国内镜像，内容与官方一致）取，失败再回退 GitHub。</summary>
        void InstallGitPackage()
        {
            string url = null;
            string name = null;

            try
            {
                Log("cmd", "  查询 Git for Windows 版本（国内镜像）");
                string idx = HttpGet("https://registry.npmmirror.com/-/binary/git-for-windows/", null);
                string ver = PickLatestGitVersion(idx);
                if (ver != null)
                {
                    Log("", "  最新稳定版：" + ver);
                    string dirUrl = "https://registry.npmmirror.com/-/binary/git-for-windows/" + ver + "/";
                    string list = HttpGet(dirUrl, null);
                    // 只要官方安装包：排除 PortableGit / MinGit / arm64 / 32 位
                    Match m = Regex.Match(list, "\"(Git-[0-9][^\"]*-64-bit\\.exe)\"");
                    if (m.Success)
                    {
                        name = m.Groups[1].Value;
                        url = dirUrl + name;
                        Log("", "  镜像地址：" + name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("warn", "  镜像查询失败：" + ex.Message);
            }

            if (url == null)
            {
                Log("warn", "  改用 GitHub 官方地址（国内可能很慢）");
                string json = HttpGet("https://api.github.com/repos/git-for-windows/git/releases/latest", null);
                Match m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]*64-bit\\.exe)\"");
                if (!m.Success) throw new Exception("无法定位 Git 安装包");
                url = m.Groups[1].Value;
                name = url.Substring(url.LastIndexOf('/') + 1);
            }

            string dest = Path.Combine(tmpDir, name);
            Download(url, dest, "Git", 2, 65, 95);

            Log("cmd", "  " + name + " /VERYSILENT");
            int code = Proc.RunShell(dest, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP- /CLOSEAPPLICATIONS");
            if (code != 0) throw new Exception("Git 安装失败（exit " + code + "）");
            Log("ok", "  Git 安装完成");
        }

        /// <summary>从 npmmirror 的目录 JSON 里挑最新的稳定版 vX.Y.Z.windows.N（跳过 rc / prerelease）</summary>
        static string PickLatestGitVersion(string json)
        {
            // 目录名可能带尾随斜杠（v2.56.0.windows.4/），所以要允许
            MatchCollection ms = Regex.Matches(json, "\"(v(\\d+)\\.(\\d+)\\.(\\d+)\\.windows\\.(\\d+))/?\"");
            string best = null;
            long bestScore = -1;
            for (int i = 0; i < ms.Count; i++)
            {
                Match m = ms[i];
                long score = 0;
                try
                {
                    score = long.Parse(m.Groups[2].Value) * 1000000000L
                          + long.Parse(m.Groups[3].Value) * 10000000L
                          + long.Parse(m.Groups[4].Value) * 100000L
                          + long.Parse(m.Groups[5].Value);
                }
                catch { continue; }
                if (score > bestScore) { bestScore = score; best = m.Groups[1].Value; }
            }
            return best;
        }

        /// <summary>Node.js 官方安装包约 30 MB。优先从 npmmirror（阿里维护的国内镜像，内容与官方一致）
        /// 取最新偶数大版本（Node 的 LTS 都是偶数大版本），失败再回退 winget。</summary>
        void InstallNodePackage()
        {
            string ver = PickLatestNodeLts();
            if (ver == null) throw new Exception("无法从镜像定位 Node 版本");
            string baseUrl = "https://registry.npmmirror.com/-/binary/node/v" + ver + "/";
            string name = "node-v" + ver + "-x64.msi";
            string url = baseUrl + name;
            Log("cmd", "  查询到 Node " + ver + "（国内镜像）");
            string dest = Path.Combine(tmpDir, name);
            Download(url, dest, "Node", 2, 15, 45);

            Log("cmd", "  msiexec /i " + name + " /qn");
            int code = Proc.RunShell("msiexec.exe", "/i \"" + dest + "\" /qn /norestart");
            // 0=成功，3010=成功但需重启，都算装好
            if (code != 0 && code != 3010) throw new Exception("Node 安装失败（exit " + code + "）");
            Log("ok", "  Node 安装完成");
        }

        /// <summary>从 npmmirror 的目录 JSON 里挑最新的偶数大版本（LTS）。跳过 rc / alpha / 带后缀的预发布目录。</summary>
        string PickLatestNodeLts()
        {
            string idx = HttpGet("https://registry.npmmirror.com/-/binary/node/", null);
            MatchCollection ms = Regex.Matches(idx, "\"name\":\"v(\\d+)\\.(\\d+)\\.(\\d+)\\/\"");
            string best = null;
            long bestScore = -1;
            for (int i = 0; i < ms.Count; i++)
            {
                int major;
                if (!int.TryParse(ms[i].Groups[1].Value, out major)) continue;
                if (major % 2 != 0) continue;   // LTS = 偶数大版本
                long minor, patch;
                if (!long.TryParse(ms[i].Groups[2].Value, out minor)) continue;
                if (!long.TryParse(ms[i].Groups[3].Value, out patch)) continue;
                long score = major * 10000000000L + minor * 100000L + patch;
                if (score > bestScore) { bestScore = score; best = major + "." + minor + "." + patch; }
            }
            return best;
        }

        /// <summary>Obsidian 安装包约 100 MB，官方下载其实也走 GitHub Releases（国内很慢）。
        /// 镜像可用性随时变（实测 ghproxy.net 常慢到几十 KB/s），所以版本查询和下载都做多镜像轮换，
        /// 全部失败才回退官方直连 / 提示手动安装。</summary>
        void InstallObsidianPackage()
        {
            // 镜像列表：实测速度会变，把当前较快的放前面；空串代表官方直连
            string[] mirrors = { "https://ghfast.top/", "" };

            string ver = null;
            string rawUrl = null;

            // 1) 查版本：依次试镜像，全失败走官方 API
            foreach (string m in mirrors)
            {
                if (m.Length == 0) break;   // 版本查询走官方 API 专用分支
                try
                {
                    Log("cmd", "  查询 Obsidian 最新版（" + new Uri(m).Host + "）");
                    string json = HttpGet(m + "https://raw.githubusercontent.com/obsidianmd/obsidian-releases/master/desktop-releases.json", null);
                    Match mm = Regex.Match(json, "\"latestVersion\"\\s*:\\s*\"([0-9][^\"]*)\"");
                    if (mm.Success) { ver = mm.Groups[1].Value; break; }
                }
                catch (Exception ex) { Log("warn", "  镜像查询失败（" + new Uri(m).Host + "）：" + ex.Message); }
            }
            if (ver == null)
            {
                Log("warn", "  镜像查询都不可用，改用 GitHub 官方 API");
                string json = HttpGet("https://api.github.com/repos/obsidianmd/obsidian-releases/releases/latest", null);
                Match m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]*/Obsidian-[^\"/]*\\.exe)\"");
                if (!m.Success) throw new Exception("无法定位 Obsidian 安装包");
                rawUrl = m.Groups[1].Value;
                Match vm = Regex.Match(rawUrl, "Obsidian-([0-9.]+)\\.exe");
                if (vm.Success) ver = vm.Groups[1].Value;
            }
            if (rawUrl == null)
                rawUrl = "https://github.com/obsidianmd/obsidian-releases/releases/download/v" + ver + "/Obsidian-" + ver + ".exe";

            Log("", "  最新版：" + ver);

            // 2) 下载：先并发测速挑最快的渠道，只显示一个下载地址、只下一次
            string name = "Obsidian-" + ver + ".exe";
            string dest = Path.Combine(tmpDir, name);
            string chosen = mirrors[0];
            string chosenHost = chosen.Length > 0 ? new Uri(chosen).Host : "github.com";
            Log("cmd", "  下载地址：" + chosenHost);

            Exception last = null;
            try
            {
                Download(chosen + rawUrl, dest, "Obsidian", 5, 15, 55);
            }
            catch (Exception ex)
            {
                last = ex;
                Log("warn", "  " + chosenHost + " 下载失败：" + ex.Message);
            }

            // 只有首选渠道真的失败，才依次尝试其余渠道
            if (last != null)
            {
                foreach (string m in mirrors)
                {
                    if (m == chosen) continue;
                    string host = m.Length > 0 ? new Uri(m).Host : "github.com";
                    try
                    {
                        Log("cmd", "  改用 " + host + " 重试");
                        Download(m + rawUrl, dest, "Obsidian", 5, 15, 55);
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Log("warn", "  " + host + " 重试失败：" + ex.Message);
                    }
                }
            }
            if (last != null) throw new Exception("所有下载渠道均失败：" + last.Message);

            Log("cmd", "  " + name + " /S");
            int code = Proc.RunShell(dest, "/S");
            if (code != 0) throw new Exception("Obsidian 安装失败（exit " + code + "）");
            Log("ok", "  Obsidian 安装完成");
        }

        // ----- 步骤 3：构建 -----
        void StepBuild()
        {
            Step(3, "active", "正在构建站点");
            Log("hi", "构建站点");

            Tool node = Env.FindNode();
            if (!node.Ok)
            {
                // 不装 Node 也能部署：site/ 本来就不入库（.gitignore），
                // 由 GitHub Actions 在推送后构建，产物与本地构建一致。
                Log("warn", "  未检测到 Node.js，跳过本地构建");
                Log("", "    推送后 GitHub Actions 会自动构建并发布");
                Step(3, "done", "已跳过（云端构建）");
                Progress(3, 100, "跳过本地构建");
                return;
            }

            if (C.DryRun)
            {
                Log("warn", "  模拟：跳过构建");
                Step(3, "done", "模拟跳过");
                return;
            }

            Progress(3, 30, "运行 node src/build.js…");
            string outp;
            int code = Proc.Run(node.FilePath, "src/build.js", C.Target, out outp);
            foreach (string line in outp.Split('\n'))
            {
                string s = line.TrimEnd('\r').Trim();
                if (s.Length > 0) Log("", "  " + s);
            }
            if (code != 0) throw new Exception("构建失败（exit " + code + "）");
            if (!File.Exists(Path.Combine(C.Target, @"site\index.html"))) throw new Exception("构建完成但没有生成 site\\index.html");

            Log("ok", "  站点构建成功");
            Step(3, "done", "构建完成");
            Progress(3, 100, "构建完成");
        }

        // ----- 步骤 4：GitHub -----
        void StepGitHub()
        {
            Step(4, "active", "正在准备仓库");
            Log("hi", "创建 GitHub 仓库");

            if (string.IsNullOrEmpty(C.Token)) throw new Exception("未提供访问令牌，无法创建仓库");
            if (string.IsNullOrEmpty(owner))
            {
                string j = HttpGet("https://api.github.com/user", C.Token);
                Match mm = Regex.Match(j, "\"login\"\\s*:\\s*\"([^\"]+)\"");
                if (!mm.Success) throw new Exception("无法获取 GitHub 用户名");
                owner = mm.Groups[1].Value;
            }
            Log("ok", "  GitHub 账号：" + owner);
            if (C.Owner.Length > 0 && !string.Equals(C.Owner, owner, StringComparison.OrdinalIgnoreCase))
            {
                Log("warn", "  填写的用户名是 " + C.Owner + "，与令牌归属（" + owner + "）不一致，以令牌为准");
            }

            Progress(4, 20, "创建仓库…");
            bool exists = false;
            try
            {
                HttpGet("https://api.github.com/repos/" + owner + "/" + C.Repo, C.Token);
                exists = true;
                Log("warn", "  仓库已存在：" + owner + "/" + C.Repo);
            }
            catch { }

            if (!exists)
            {
                string body = "{\"name\":\"" + EscapeJson(C.Repo) + "\","
                            + "\"description\":\"" + EscapeJson("用 Obsidian 写作、自动发布到 GitHub Pages 的数字花园") + "\","
                            + "\"private\":false,\"auto_init\":false,\"has_issues\":true}";
                HttpPost("https://api.github.com/user/repos", body, C.Token);
                Log("ok", "  仓库已创建：" + owner + "/" + C.Repo);
            }

            string remote = "https://github.com/" + owner + "/" + C.Repo + ".git";
            Tool git = Env.FindGit();
            if (!git.Ok) throw new Exception("找不到 git");

            Progress(4, 45, "初始化本地仓库…");
            string outp;
            if (!Directory.Exists(Path.Combine(C.Target, ".git")))
            {
                Proc.Run(git.FilePath, "init", C.Target, out outp);
                Proc.Run(git.FilePath, "checkout -b main", C.Target, out outp);
                Log("", "  git init 完成");
            }
            Proc.Run(git.FilePath, "config user.name \"" + owner + "\"", C.Target, out outp);
            Proc.Run(git.FilePath, "config user.email \"" + owner + "@users.noreply.github.com\"", C.Target, out outp);
            Proc.Run(git.FilePath, "config core.autocrlf true", C.Target, out outp);

            string remotes;
            Proc.Run(git.FilePath, "remote", C.Target, out remotes);
            if (remotes.IndexOf("origin", StringComparison.OrdinalIgnoreCase) >= 0)
                Proc.Run(git.FilePath, "remote set-url origin \"" + remote + "\"", C.Target, out outp);
            else
                Proc.Run(git.FilePath, "remote add origin \"" + remote + "\"", C.Target, out outp);

            Progress(4, 60, "提交文件…");
            Proc.Run(git.FilePath, "add -A", C.Target, out outp);
            string st;
            Proc.Run(git.FilePath, "status --porcelain", C.Target, out st);
            if (st.Trim().Length > 0)
            {
                Proc.Run(git.FilePath, "commit -m \"deploy: 初始化数字花园\"", C.Target, out outp);
                Log("", "  已提交初始版本");
            }
            else Log("warn", "  没有需要提交的变更");

            if (C.DryRun)
            {
                Log("warn", "  模拟：跳过推送");
            }
            else
            {
                Progress(4, 75, "推送到 GitHub…");
                string pushUrl = "https://x-access-token:" + C.Token + "@github.com/" + owner + "/" + C.Repo + ".git";
                string pushOut;
                int code = Proc.Run(git.FilePath, "push \"" + pushUrl + "\" HEAD:refs/heads/main --force", C.Target, out pushOut);
                if (code != 0)
                {
                    Log("err", "  " + pushOut.Replace(C.Token, "***"));
                    ExplainPushFailure(pushOut, owner, C.Repo);
                    throw new Exception("git push 失败");
                }
                Log("ok", "  推送成功");

                // 把本地 main 的上游设为 origin/main，之后插件用 plain `git push` 也能正常推送
                Proc.Run(git.FilePath, "branch --set-upstream-to=origin/main main", C.Target, out outp);

                // 存进 Windows 凭据管理器，之后插件推送不用再输令牌
                Log("", "  保存凭据到 Windows 凭据管理器…");
                try
                {
                    Proc.Run(git.FilePath, "config --local credential.helper manager", C.Target, out outp);
                    string cred = "protocol=https\nhost=github.com\nusername=" + owner + "\npassword=" + C.Token + "\n\n";
                    Proc.RunWithInput(git.FilePath, "credential approve", C.Target, cred, out outp, 20000);
                    Log("ok", "  已保存凭据到 Windows 凭据管理器");
                }
                catch (Exception ex)
                {
                    Log("warn", "  凭据保存失败（不影响本次部署）：" + ex.Message);
                }
            }

            Progress(4, 90, "开启 GitHub Pages…");
            if (!C.DryRun)
            {
                bool pagesOk = false;
                try
                {
                    HttpPost("https://api.github.com/repos/" + owner + "/" + C.Repo + "/pages",
                        "{\"build_type\":\"workflow\"}", C.Token);
                    pagesOk = true;
                }
                catch
                {
                    try
                    {
                        HttpPut("https://api.github.com/repos/" + owner + "/" + C.Repo + "/pages",
                            "{\"build_type\":\"workflow\"}", C.Token);
                        pagesOk = true;
                    }
                    catch (Exception ex2)
                    {
                        Log("warn", "  Pages 自动开启失败：" + ex2.Message);
                    }
                }
                if (pagesOk) Log("ok", "  GitHub Pages 已设为 GitHub Actions 发布");

                // 关键：启用 Pages 后主动触发一次工作流运行。
                // 否则若本次没有新提交（例如重跑时内容未变），git push 不会自动触发，
                // 站点就永远不会被构建，访问地址会一直 404。
                if (pagesOk)
                {
                    try
                    {
                        HttpPost("https://api.github.com/repos/" + owner + "/" + C.Repo
                            + "/actions/workflows/deploy.yml/dispatches", "{\"ref\":\"main\"}", C.Token);
                        Log("ok", "  已触发站点构建（GitHub Actions），通常 1-3 分钟后可访问");
                    }
                    catch (Exception ex3)
                    {
                        Log("warn", "  自动触发构建失败（可到 GitHub → Actions 手动 Run workflow）：" + ex3.Message);
                    }
                }
            }

            PagesUrl = "https://" + owner + ".github.io/" + C.Repo + "/";
            RepoUrl = "https://github.com/" + owner + "/" + C.Repo;
            Step(4, "done", "仓库已就绪");
            Progress(4, 100, "仓库已就绪");
        }

        // ----- 步骤 5：Obsidian -----
        void StepObsidian()
        {
            Step(5, "active", "正在检测 Obsidian");
            Log("hi", "安装 Obsidian 与插件");
            Env.UpdateSessionPath();

            Tool obs = Env.FindObsidian();
            if (!obs.Ok)
            {
                Progress(5, 10, "安装 Obsidian…");
                // 顺序：国内镜像（ghproxy 转发 GitHub Releases，满速）→ winget → 失败仅提示
                bool installed = false;
                try
                {
                    InstallObsidianPackage();
                    installed = true;
                }
                catch (Exception ex)
                {
                    Log("warn", "  镜像安装未成功：" + ex.Message);
                }
                if (!installed && !InstallViaWinget("Obsidian.Obsidian", "Obsidian", 5, 10, 50))
                {
                    Log("warn", "  Obsidian 自动安装失败，请手动安装后再打开笔记库（笔记与插件仍会写入部署目录）");
                }
                Env.UpdateSessionPath();
                obs = Env.FindObsidian();
                if (obs.Ok) Log("ok", "  Obsidian 就绪：" + obs.Version);
                else
                {
                    Log("warn", "  Obsidian 未检测到，请手动安装后把插件复制进库");
                    Log("cmd", "  下载地址：https://github.com/obsidianmd/obsidian-releases/releases");
                }
            }
            else Log("ok", "  Obsidian 已安装：" + obs.Version);

            // 笔记库 = 部署目录（content/ 就是笔记所在子目录）
            string vault = C.Target;
            string pluginSrc = Path.Combine(C.Target, "blog-uploader");
            string pluginDst = Path.Combine(vault, @".obsidian\plugins\blog-uploader");

            Progress(5, 60, "安装上传插件…");
            if (!File.Exists(Path.Combine(pluginSrc, "main.js")))
            {
                Log("warn", "  找不到 blog-uploader 插件文件");
            }
            else
            {
                CopyDir(pluginSrc, pluginDst);

                string dataJson = "{\n"
                    + "  \"blogPath\": \"" + EscapeJson(vault) + "\",\n"
                    + "  \"contentDir\": \"content\",\n"
                    + "  \"keepStructure\": true,\n"
                    + "  \"autoBuild\": true,\n"
                    + "  \"autoPush\": true,\n"
                    + "  \"gitToken\": \"" + EscapeJson(C.Token) + "\"\n"
                    + "}";
                WriteText(Path.Combine(pluginDst, "data.json"), dataJson);

                string cpFile = Path.Combine(vault, @".obsidian\community-plugins.json");
                string list = ReadTextArray(cpFile);
                if (list.IndexOf("\"blog-uploader\"") < 0)
                {
                    if (list.Trim() == "[]" || list.Trim().Length == 0) list = "[\"blog-uploader\"]";
                    else list = list.TrimEnd().TrimEnd(']').TrimEnd() + ",\n  \"blog-uploader\"\n]";
                }
                WriteText(cpFile, list.Length == 0 ? "[\"blog-uploader\"]" : list);

                string appFile = Path.Combine(vault, @".obsidian\app.json");
                string appTxt = File.Exists(appFile) ? File.ReadAllText(appFile, Encoding.UTF8) : "{}";
                appTxt = SetJsonRaw(appTxt, "safeMode", "false");
                appTxt = SetJsonRaw(appTxt, "communityPluginsEnabled", "true");
                WriteText(appFile, appTxt);

                Log("ok", "  插件已安装并预配置：" + pluginDst);
            }

            // 隐藏博客引擎的系统文件夹，避免文件树里混入 src / assets / blog-uploader 等杂物
            // （仅影响 Obsidian 文件浏览器显示，不影响站点构建与附件引用）
            try
            {
                EnableCssSnippet(vault, "hide-system");
                Log("ok", "  已在文件树隐藏系统文件夹（src / assets / blog-uploader 等），只显示 content/ 笔记");
            }
            catch (Exception exCss)
            {
                Log("warn", "  隐藏系统文件夹失败（不影响发布）：" + exCss.Message);
            }

            string note = Path.Combine(vault, @"content\开始写作.md");
            if (!File.Exists(note))
            {
                string txt = "---\ntitle: 开始写作\ndate: " + DateTime.Now.ToString("yyyy-MM-dd") + "\ntags:\n  - 指南\n---\n\n"
                    + "这是你的第一篇笔记。左侧边栏点 ↑ 图标（或右键笔记选「上传到博客」），就会把它发布到\n"
                    + (PagesUrl.Length > 0 ? PagesUrl : "你的站点") + "\n\n"
                    + "写完想更新？再点一次上传即可，同名会覆盖。\n\n"
                    + "支持的写法：[[开始使用]]、`![[图片.png]]`、Callout、表格、任务列表。\n";
                WriteText(note, txt);
                Log("", "  已生成示例笔记：content\\开始写作.md");
            }

            // 自动用这个目录打开 Obsidian：它会直接当作已有仓库打开并登记，
            // 避免首次启动弹出「创建新仓库 / 打开文件夹作为仓库」向导让用户误建空仓库。
            // 注意：博客仓库 = 部署目录，独立于用户已有的其他仓库，不会互相影响。
            if (obs.Ok)
            {
                try
                {
                    bool running = false;
                    try { running = Process.GetProcessesByName("Obsidian").Length > 0; } catch { }
                    if (running)
                    {
                        // 用户可能正开着别的仓库：不强行弹新窗口，避免打扰，给明确路径即可
                        Log("ok", "  笔记库已就绪（部署目录就是仓库，无需创建）：" + vault);
                        Log("ok", "  Obsidian 正在运行，请在里面「打开文件夹作为仓库」选择上面的目录即可");
                    }
                    else
                    {
                        Process.Start(obs.FilePath, "\"" + vault + "\"");
                        Log("ok", "  已自动打开笔记库（就是部署目录，无需在 Obsidian 里「创建仓库」）");
                    }
                }
                catch (Exception ex)
                {
                    Log("warn", "  自动打开 Obsidian 失败：" + ex.Message);
                    Log("warn", "  请手动打开 Obsidian → 打开文件夹作为仓库 → 选择：" + vault);
                }
            }
            else
            {
                Log("warn", "  未检测到 Obsidian，请安装后「打开文件夹作为仓库」并选择：" + vault);
            }

            Step(5, "done", "Obsidian 与插件就绪");
            Progress(5, 100, "全部完成");
        }

        static string ReadTextArray(string path)
        {
            try
            {
                if (!File.Exists(path)) return "[]";
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { return "[]"; }
        }

        static string SetJsonRaw(string json, string key, string rawValue)
        {
            string pattern = "\"" + key + "\"\\s*:\\s*(true|false|\"[^\"]*\"|-?[0-9.]+)";
            string repl = "\"" + key + "\": " + rawValue;
            if (Regex.IsMatch(json, pattern)) return Regex.Replace(json, pattern, repl);
            // 没有这个字段就插到开头
            string t = json.Trim();
            if (t == "{}" || t.Length == 0) return "{\n  \"" + key + "\": " + rawValue + "\n}";
            if (t.StartsWith("{")) return "{\n  \"" + key + "\": " + rawValue + "," + t.Substring(1);
            return json;
        }

        static void WriteText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>写入并启用一段 Obsidian CSS 片段（隐藏博客引擎系统文件夹），不覆盖用户已有的外观设置</summary>
        void EnableCssSnippet(string vault, string name)
        {
            string snippetDir = Path.Combine(vault, @".obsidian\snippets");
            if (!Directory.Exists(snippetDir)) Directory.CreateDirectory(snippetDir);
            WriteText(Path.Combine(snippetDir, name + ".css"), HIDE_SYSTEM_CSS);

            string appFile = Path.Combine(vault, @".obsidian\appearance.json");
            string txt = File.Exists(appFile) ? File.ReadAllText(appFile, Encoding.UTF8) : "{}";
            if (txt.IndexOf("enabledCssSnippets") < 0)
            {
                // 该字段不存在：直接插入（SetJsonRaw 会自动处理开头/空对象的情况）
                txt = SetJsonRaw(txt, "enabledCssSnippets", "[\"" + name + "\"]");
            }
            else if (txt.IndexOf("\"" + name + "\"") < 0)
            {
                // 字段存在但还没有这个片段：在数组里追加
                int idx = txt.IndexOf("enabledCssSnippets");
                int b = txt.IndexOf('[', idx);
                int e = txt.IndexOf(']', b);
                if (b >= 0 && e > b)
                {
                    string inner = txt.Substring(b + 1, e - b - 1).Trim();
                    string arr = inner.Length == 0 ? "\"" + name + "\""
                                                    : inner.TrimEnd().TrimEnd(',') + ", \"" + name + "\"";
                    txt = txt.Substring(0, b + 1) + arr + txt.Substring(e);
                }
            }
            WriteText(appFile, txt);

            // Obsidian 运行期间会回写 appearance.json，此时写的配置可能被覆盖
            bool obsRunning = false;
            try { obsRunning = Process.GetProcessesByName("Obsidian").Length > 0; } catch { }
            if (obsRunning)
                Log("warn", "  Obsidian 正在运行：文件树隐藏需重启 Obsidian 后生效；若仍未生效，请在 设置→外观→CSS片段 手动开启 hide-system");
            else
                Log("ok", "  已在文件树隐藏系统文件夹（src / assets / blog-uploader / site 等），只显示 content/ 下的笔记");
        }

        // ----- 网络 -----

        TimeoutClient NewClient(string token)
        {
            TimeoutClient wc = new TimeoutClient();
            // 关键：UploadString 默认用系统 ANSI(中文 Windows 为 GBK)编码 body，
            // 但 Content-Type 声明了 charset=utf-8，含中文时 GitHub 解析乱码 → 400 Problems parsing JSON
            wc.Encoding = new UTF8Encoding(false);
            wc.Headers.Add("User-Agent", "garden-deployer");
            wc.Headers.Add("Accept", "application/vnd.github+json");
            if (!string.IsNullOrEmpty(token)) wc.Headers.Add("Authorization", "Bearer " + token);
            return wc;
        }

        string HttpGet(string url, string token)
        {
            using (TimeoutClient wc = NewClient(token))
            {
                try { return wc.DownloadString(url); }
                catch (WebException ex) { throw new Exception(Describe(ex, "GET " + url)); }
            }
        }

        string HttpPost(string url, string body, string token)
        {
            using (TimeoutClient wc = NewClient(token))
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                try { return wc.UploadString(url, "POST", body); }
                catch (WebException ex) { throw new Exception(Describe(ex, "POST " + url)); }
            }
        }

        string HttpPut(string url, string body, string token)
        {
            using (TimeoutClient wc = NewClient(token))
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                try { return wc.UploadString(url, "PUT", body); }
                catch (WebException ex) { throw new Exception(Describe(ex, "PUT " + url)); }
            }
        }

        /// <summary>推送失败时按错误特征给出可执行的下一步，而不是只丢一句 git 输出</summary>
        void ExplainPushFailure(string outText, string owner, string repo)
        {
            string s = (outText == null ? "" : outText).ToLowerInvariant();
            bool auth = s.IndexOf("401") >= 0
                     || s.IndexOf("authentication failed") >= 0
                     || s.IndexOf("could not read username") >= 0
                     || s.IndexOf("invalid username or password") >= 0
                     || s.IndexOf("terminal prompts disabled") >= 0;

            if (auth)
            {
                Log("warn", "  令牌认证失败，通常是这两种原因：");
                Log("", "    1) 令牌已过期（GitHub 默认只有 30 天）→ 重新创建，建议有效期 90 天");
                Log("", "    2) 权限不足 → 创建时必须勾选 repo 和 workflow");
                Log("", "    换新令牌后，先删掉旧凭据：控制面板 → 凭据管理器 → git:https://github.com");
            }
            else if (s.IndexOf("403") >= 0)
            {
                Log("warn", "  权限被拒绝（403）：令牌缺少 workflow 权限时，无法推送 .github/workflows/");
            }
            else if (s.IndexOf("404") >= 0 || s.IndexOf("not found") >= 0)
            {
                Log("warn", "  仓库不可写（404）：" + owner + "/" + repo + " 可能已被他人占用，或令牌无权访问");
                Log("", "    换一个仓库名再试");
            }
        }

        static string Describe(WebException ex, string what)
        {
            string detail = "";
            try
            {
                if (ex.Response != null)
                {
                    HttpWebResponse r = (HttpWebResponse)ex.Response;
                    detail = "HTTP " + (int)r.StatusCode;
                    Stream s = r.GetResponseStream();
                    if (s != null)
                    {
                        using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                        {
                            string body = sr.ReadToEnd();
                            if (!string.IsNullOrEmpty(body)) detail += " " + body;
                        }
                    }
                }
            }
            catch { }
            if (detail.Length > 280) detail = detail.Substring(0, 280) + "…";
            return what + " 失败：" + detail;
        }

        void Download(string url, string dest, string label, int step, double from, double to)
        {
            Log("cmd", "  下载 " + label);
            using (TimeoutClient wc = new TimeoutClient())
            {
                wc.Headers.Add("User-Agent", "garden-deployer");

                wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    double ratio = e.ProgressPercentage / 100.0;
                    Progress(step, from + (to - from) * ratio, label + "  " + e.ProgressPercentage + "%");
                };

                Exception err = null;
                using (System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false))
                {
                    wc.DownloadFileCompleted += delegate(object s, AsyncCompletedEventArgs e)
                    {
                        if (e.Error != null) err = e.Error;
                        done.Set();
                    };
                    wc.DownloadFileAsync(new Uri(url), dest);
                    done.WaitOne();
                }
                if (err != null) throw new Exception("下载失败：" + label + " - " + err.Message);
            }
            Progress(step, to, label + " 完成");
        }

        public static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(" ");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
