using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WemLabeler.Pipeline;

namespace WemLabeler;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 必须在任何 WPF 类型被初始化之前执行：
        // WPF 会在 %TEMP%\WPF 下创建临时文件，如果那里不可写
        // （典型场景：程序在受限沙箱 / 被改写权限的临时目录下运行），
        // 就会抛 UnauthorizedAccessException 让程序直接消失。
        EnsureWritableTempDirectory();

        // 无界面模式：本程序是 WinExe（GUI 子系统），带参数运行时不启动 WPF。
        if (args.Length > 0)
        {
            Environment.ExitCode = RunCommandLine(args);
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        // WemLabeler 是 WinExe（GUI 子系统），没有控制台：
        // 未处理异常默认不会显示任何东西，进程直接消失——表现为「双击没反应 / 打不开」。
        // 这里把所有未处理异常都变成可见的错误框 + 崩溃日志。
        app.DispatcherUnhandledException += (_, e) =>
        {
            ReportFatal("DispatcherUnhandledException", e.Exception);
            e.Handled = true; // 保证界面还能继续响应，便于用户把日志发出来
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ReportFatal("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportFatal("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            app.Run(new MainWindow());
        }
        catch (Exception ex)
        {
            ReportFatal("Main", ex);
            throw;
        }
    }

    #region 命令行模式

    /// <summary>
    /// 无界面运行：
    /// <code>
    /// WemLabeler.exe soundmap [resources|mapping|all] --base &lt;项目根&gt; --game &lt;游戏根&gt;
    ///     [--out 映射.json] [--report 报告.txt] [--log 日志.txt]
    /// </code>
    /// <c>resources</c> = GUI 的 ⓪（读游戏 → 写链路 + wem_index.json + .bnk），
    /// <c>mapping</c> = GUI 的 ③（基于 mapping JSON + txtp 富化），<c>all</c>（默认）= 两步都跑。
    /// 默认**不会**写 config.json，也不会碰基准文件 sound_wem_mapping_export.json（用 --out 指定输出）。
    /// </summary>
    private static int RunCommandLine(string[] args)
    {
        var logPath = Arg(args, "--log") ?? Path.Combine(
            Arg(args, "--base") ?? AppDomain.CurrentDomain.BaseDirectory, "soundmap_cli.log");
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { }
        void Log(string message)
        {
            try { writer?.WriteLine(message); } catch { }
            try { Console.WriteLine(message); } catch { }
        }

        try
        {
            if (args[0] is "-h" or "--help" or "help")
            {
                Log("usage: WemLabeler.exe soundmap [resources|mapping|unused|all] --base <dir> --game <dir> " +
                    "[--out file.json] [--report file.txt] [--log file.txt]");
                return 0;
            }

            if (!string.Equals(args[0], "soundmap", StringComparison.OrdinalIgnoreCase))
            {
                Log($"unknown command: {args[0]}");
                return 2;
            }

            var step = args.Length > 1 && !args[1].StartsWith("--") ? args[1].ToLowerInvariant() : "all";
            if (step is not ("resources" or "mapping" or "unused" or "all"))
            {
                Log($"unknown step: {step} (expected resources | mapping | unused | all)");
                return 2;
            }

            Locale.Initialize(Arg(args, "--lang") ?? "zh-CN");

            var config = ConfigManager.Load();
            if (Arg(args, "--base") is { } baseDir) config.BaseDir = baseDir;
            if (Arg(args, "--game") is { } game) config.GameRoot = game;

            var root = config.BaseDir;
            if (string.IsNullOrWhiteSpace(root))
            {
                Log("[!] --base <项目根> 是必需的（或先在 GUI 里设好项目根目录）");
                return 2;
            }

            var paths = new PipelinePaths(root, config)
            {
                MappingJsonOverride = Arg(args, "--out"),
                ChainReportOverride = Arg(args, "--report"),
            };
            paths.EnsureDirectories();

            Log($"项目根: {paths.BaseDir}");
            Log($"输出   : {paths.MappingJson}");

            if (step is "resources" or "all")
            {
                var gameRoot = config.GameRoot;
                if (!OdradekExporter.IsGameRoot(gameRoot))
                    gameRoot = OdradekExporter.AutoDetectGameRoot(gameRoot, Log, CancellationToken.None);
                if (gameRoot == null)
                {
                    Log("[!] 找不到 DS2 安装目录（需要包含 DS2.exe 的那一层）");
                    return 1;
                }
                var summary = OdradekExporter.ExportResources(gameRoot, paths, null, Log, CancellationToken.None);
                Log("⓪ " + summary);
            }

            if (step is "mapping" or "all")
            {
                var report = AudioPipeline.BuildMapping(paths, null, Log, CancellationToken.None);
                Log($"mapping 条目: {report.MappingData.Count}");
            }

            if (step is "unused" or "all")
            {
                var count = AudioPipeline.BuildUnusedWemCsv(paths, null, Log, CancellationToken.None);
                Log($"未使用的 WEM: {count}");
            }

            Log($"日志: {logPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            return 1;
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    #endregion

    #region 临时目录

    /// <summary>
    /// 确认系统临时目录可写；不可写时把 TEMP/TMP 改到程序能写的地方。
    /// 这一步必须在 new Application() 之前完成，否则 WPF 已经把临时目录缓存下来了。
    /// </summary>
    private static void EnsureWritableTempDirectory()
    {
        var systemTemp = Path.GetTempPath();
        if (CanWriteTo(Path.Combine(systemTemp, "WPF"))) return;

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WemLabeler", "temp")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!CanWriteTo(candidate)) continue;

            Environment.SetEnvironmentVariable("TEMP", candidate);
            Environment.SetEnvironmentVariable("TMP", candidate);
            WriteStartupNote($"system temp not writable ('{systemTemp}'); TEMP/TMP redirected to '{candidate}'");
            return;
        }

        WriteStartupNote($"system temp not writable ('{systemTemp}') and no fallback available; " +
                         "WPF may fail to create its temporary files");
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $"probe_{Guid.NewGuid():N}.tmp");
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
            fs.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    /// <summary>把致命异常写进 logs 并弹窗显示，避免 GUI 程序静默退出。</summary>
    private static void ReportFatal(string source, Exception ex)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}";
        var logPath = AppendLog($"crash_{DateTime.Now:yyyyMMdd}.log", text);

        try
        {
            var message = Locale.S("dlg_fatal", source, ex.GetType().Name, ex.Message,
                logPath ?? Locale.S("lbl_unavailable"));
            MessageBox.Show(message, Locale.S("dlg_fatal_title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 连弹窗都失败时至少别再把异常吞掉
        }
    }

    /// <summary>启动阶段的自检记录（临时目录重定向等），写进 logs 便于排查。</summary>
    private static void WriteStartupNote(string note) =>
        AppendLog($"startup_{DateTime.Now:yyyyMMdd}.log", $"[{DateTime.Now:HH:mm:ss}] {note}");

    private static string? AppendLog(string fileName, string text)
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, fileName);
            File.AppendAllText(logPath, text + Environment.NewLine + new string('-', 60) + Environment.NewLine,
                new UTF8Encoding(false));
            return logPath;
        }
        catch
        {
            return null;
        }
    }
}
