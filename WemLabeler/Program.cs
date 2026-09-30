using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WemLabeler;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 必须在任何 WPF 类型被初始化之前执行：
        // WPF 会在 %TEMP%\WPF 下创建临时文件，如果那里不可写
        // （典型场景：程序在受限沙箱 / 被改写权限的临时目录下运行），
        // 就会抛 UnauthorizedAccessException 让程序直接消失。
        EnsureWritableTempDirectory();

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
