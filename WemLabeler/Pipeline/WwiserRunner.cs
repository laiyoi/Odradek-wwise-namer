using System.Diagnostics;
using System.IO;
using System.Text;

namespace WemLabeler.Pipeline;

/// <summary>
/// 用 wwiser 从 .bnk 生成 TXTP（替代原来手工开 wwiser GUI 点「Generate TXTP」的步骤）。
///
/// 等价命令：
///   python &lt;wwiser.py | wwiser.pyz&gt; -g -go "&lt;txtp目录&gt;" "&lt;banks目录&gt;\*.bnk"
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
            throw new InvalidOperationException($"目录里没有 .bnk 文件: {banksDir}（先跑「① 提取 BNK」）");

        var txtpDir = paths.TxtpDir;
        Directory.CreateDirectory(txtpDir);

        // wwiser 自己会展开通配符
        var pattern = Path.Combine(banksDir, "*.bnk");
        var arguments = $"-g -go \"{txtpDir}\" \"{pattern}\"";

        AudioPipeline.SafeLog(log, $"python : {pythonExe}");
        AudioPipeline.SafeLog(log, $"wwiser : {wwiserScript}");
        AudioPipeline.SafeLog(log, $"命令   : \"{pythonExe}\" \"{wwiserScript}\" {arguments}");
        AudioPipeline.SafeLog(log, $"待处理 : {bankCount} 个 .bnk（74 个 bank 约 1GB，首次可能几分钟）");
        AudioPipeline.SafeLog(log, "");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{wwiserScript}\" {arguments}",
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
