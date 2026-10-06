using System.Diagnostics;
using System.IO;
using System.Text;

namespace WemLabeler.Pipeline;

/// <summary>
/// 用 wwiser 从 .bnk 生成 TXTP（替代原来手工开 wwiser GUI 点「Generate TXTP」的步骤）。
///
/// 等价命令（**工作目录 = `Extracted_Banks`，`-go` 必须给相对路径**）：
///   python.exe &lt;exe目录&gt;\utils\wwiser_cli.py &lt;exe目录&gt;\utils\wwiser.pyz -g -go "txtp" "*.bnk"
///
/// 两个坑：
/// 1. **`-go` 必须给相对路径**。wwiser 给内嵌音频行写 bank 路径时，会把「输出目录的绝对路径
///    在盘符以下的层数 + 1」当成 `../` 的个数；给绝对路径会写出 `../../../../X.bnk`，
///    从 txtp 目录解析出去直接指到盘根，vgmstream 一律失败。
/// 2. 走 <c>wwiser_cli.py</c> 而不是 `wwiser.pyz` 本身：pyz 的 `__main__.py` 无条件
///    `import wwiser.wgui`，而 wgui 需要 tkinter —— python.org 的 embeddable 包没有 tkinter。
///    启动脚本把 pyz 加进 sys.path 直接调 `wwiser.wcli`，就不碰 GUI 了。
/// </summary>
public static class WwiserRunner
{
    /// <summary>运行 wwiser 生成 TXTP，返回生成的 .txtp 数量。</summary>
    public static int GenerateTxtp(PipelinePaths paths, string pythonExe, string wwiserScript,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var banksDir = paths.ExtractedBanksDir;
        if (!Directory.Exists(banksDir))
            throw new DirectoryNotFoundException($"找不到 BNK 目录: {banksDir}");

        var bankCount = Directory.EnumerateFiles(banksDir, "*.bnk").Count();
        if (bankCount == 0)
            throw new InvalidOperationException($"目录里没有 .bnk 文件: {banksDir}（先跑「⓪ 读游戏资源」）");

        var txtpDir = paths.TxtpDir;
        Directory.CreateDirectory(txtpDir);

        // 相对路径！工作目录就是 banks 目录，所以 txtp 目录相对它叫 "txtp"，
        // bank 通配符也就是当前目录下的 *.bnk。
        var txtpRelative = Path.GetRelativePath(banksDir, txtpDir).Replace('\\', '/');
        var cliScript = ToolLocator.WwiserCliScript;

        // 有启动脚本就走它（不依赖 tkinter）；没有就直接跑 pyz（系统 Python 有 tkinter 时也能用）
        var entry = File.Exists(cliScript) ? cliScript : null;
        var entryArgs = entry != null ? $"\"{entry}\" \"{wwiserScript}\"" : $"\"{wwiserScript}\"";
        var arguments = $"{entryArgs} -g -go \"{txtpRelative}\" \"*.bnk\"";

        AudioPipeline.SafeLog(log, $"python : {pythonExe}");
        AudioPipeline.SafeLog(log, $"wwiser : {wwiserScript}");
        if (entry != null) AudioPipeline.SafeLog(log, $"启动脚本: {entry}（绕开 wwiser 的 tkinter 依赖）");
        AudioPipeline.SafeLog(log, $"命令   : (cwd={banksDir}) \"{pythonExe}\" {arguments}");
        AudioPipeline.SafeLog(log, $"待处理 : {bankCount} 个 .bnk（74 个 bank 约 1GB，首次可能几分钟）");
        AudioPipeline.SafeLog(log, "");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = banksDir
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Python 进程");

        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            AudioPipeline.SafeLog(log, e.Data);
            progress?.Report(new PipelineProgress { Message = Locale.S("status_wwiser_running", e.Data) });
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            stderr.AppendLine(e.Data);
            AudioPipeline.SafeLog(log, "[stderr] " + e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        while (!proc.WaitForExit(500))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new PipelineProgress { Message = Locale.S("status_wwiser_running", "") });
        }

        proc.WaitForExit(); // 确保异步输出读完

        if (proc.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr.ToString().Trim() : $"exitCode={proc.ExitCode}";
            throw new InvalidOperationException($"wwiser 退出码 {proc.ExitCode}: {detail}");
        }

        var generated = Directory.EnumerateFiles(txtpDir, "*.txtp").Count();
        AudioPipeline.SafeLog(log, "");
        AudioPipeline.SafeLog(log, Locale.S("pipe_wwiser_done", generated, txtpDir));
        return generated;
    }
}
