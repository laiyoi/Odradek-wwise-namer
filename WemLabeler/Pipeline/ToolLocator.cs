using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace WemLabeler.Pipeline;

/// <summary>下载进度。</summary>
public sealed class ToolDownloadProgress
{
    public string Message { get; init; } = "";
    public long Received { get; init; }
    public long Total { get; init; }
}

/// <summary>
/// 外部工具（vgmstream / wwiser / 嵌入式 Python）的定位与下载。
///
/// 位置固定为 **exe 旁边的 utils 目录**（<c>&lt;exe目录&gt;\utils</c>），不散落到别处：
///   <c>utils\vgmstream-cli.exe</c>（或 <c>utils\vgmstream*\vgmstream-cli.exe</c>）
///   <c>utils\wwiser.pyz</c>
///   <c>utils\wwnames.db3</c>      —— wwiser 的 hash→名字库，必须和 pyz 同目录
///   <c>utils\python\python.exe</c> —— 嵌入式 Python
///   <c>utils\wwiser_cli.py</c>     —— 随程序分发的启动脚本（绕开 wwiser 的 tkinter 依赖）
///
/// 注：**pyscript/ 那套老 Python 脚本**是按 <c>BASE_DIR</c> 写死的，它们的 wwiser 放在项目根，
/// 和这里无关（见 pyscript/README.md）。
/// </summary>
public static class ToolLocator
{
    private const string VgmstreamRepo = "vgmstream/vgmstream-releases";
    private const string VgmstreamRepoFallback = "vgmstream/vgmstream";
    private const string WwiserRepo = "bnnm/wwiser";

    /// <summary>exe 旁边的 utils：所有外部工具都放这里（下载目标，也是查找位置）。</summary>
    public static string UtilsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "utils");

    /// <summary>utils 下的 python 目录（嵌入式解释器解压到这里）。</summary>
    public static string PythonDir => Path.Combine(UtilsDir, "python");

    /// <summary>我们要跑的 wwiser 启动脚本（随程序分发）。</summary>
    public static string WwiserCliScript => Path.Combine(UtilsDir, "wwiser_cli.py");

    #region 定位

    /// <summary>
    /// 找 vgmstream-cli.exe：手工指定的路径优先，其次 exe 旁边的 utils（会递归找，
    /// 兼容解压出子目录的情况）。
    /// </summary>
    public static string? FindVgmstreamCli(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        if (!Directory.Exists(UtilsDir)) return null;
        try
        {
            var direct = Path.Combine(UtilsDir, "vgmstream-cli.exe");
            if (File.Exists(direct)) return direct;

            return Directory.EnumerateFiles(UtilsDir, "vgmstream-cli.exe", SearchOption.AllDirectories)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>找 wwiser.pyz（只认 exe 旁边 utils 下的这一个文件）。</summary>
    public static string? FindWwiserPyz()
    {
        try
        {
            var direct = Path.Combine(UtilsDir, "wwiser.pyz");
            if (File.Exists(direct)) return direct;

            if (!Directory.Exists(UtilsDir)) return null;
            return Directory.EnumerateFiles(UtilsDir, "wwiser.pyz", SearchOption.AllDirectories)
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 找 wwiser 的名字库。wwiser 的 <c>-nd</c> 默认自动找（和 pyz 同目录即可），
    /// 这个只用来在「下载工具」里判断要不要补下。
    /// </summary>
    public static string? FindWwnamesDb()
    {
        var db = Path.Combine(UtilsDir, "wwnames.db3");
        return File.Exists(db) ? db : null;
    }

    /// <summary>
    /// 找 Python 解释器。**utils 下的嵌入式 Python 优先**，最后才回落到 PATH 上的系统 Python。
    /// （wwiser.pyz 是 zipapp，必须靠解释器运行。）
    /// </summary>
    public static string? FindPython()
    {
        var embedded = Path.Combine(PythonDir, "python.exe");
        if (File.Exists(embedded)) return embedded;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = new[] { "", ".exe", ".bat", ".cmd" };
        foreach (var exe in new[] { "python", "py", "python3" })
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in exts)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), exe + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
        }
        return null;
    }

    #endregion

    #region 下载

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub API 强制要求 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WemLabeler/1.5");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>统一包装网络错误，便于定位问题。</summary>
    private static async Task<string> GetStringOrThrowAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            return await client.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = ex.Message;
            var inner = ex.InnerException;
            while (inner != null) { detail += $" → {inner.Message}"; inner = inner.InnerException; }
            throw new InvalidOperationException(
                $"无法访问 {url}\n（{detail}）\n可以手动下载后放到 {UtilsDir} 下。", ex);
        }
    }

    /// <summary>在仓库的 release 列表里找第一个含匹配资产的 release。</summary>
    private static async Task<(string Url, string Name, string Tag)> FindReleaseAssetAsync(
        HttpClient client, string repo, Func<string, bool> match, Action<string>? log, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{repo}/releases?per_page=20";
        var json = await GetStringOrThrowAsync(client, api, ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var name = release.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!release.TryGetProperty("assets", out var assets)) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (!match(assetName)) continue;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) continue;
                AudioPipeline.SafeLog(log, $"    找到 release {tag} ({name}) 的资产: {assetName}");
                return (url, assetName, tag);
            }
        }

        throw new InvalidOperationException($"在 {repo} 的 release 里没有找到匹配的资产");
    }

    /// <summary>下载 vgmstream 的 Windows 构建（优先 64 位）并解压到 exe\utils 下。</summary>
    public static async Task<string> DownloadVgmstreamAsync(Action<string>? log,
        IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        AudioPipeline.SafeLog(log, $"查询 {VgmstreamRepo} 的最新 release ...");

        (string Url, string Name, string Tag) found;
        try
        {
            found = await FindReleaseAssetAsync(client, VgmstreamRepo,
                name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^vgmstream-win(64)?\.zip$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase), log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AudioPipeline.SafeLog(log, $"    {VgmstreamRepo} 查询失败（{ex.Message}），回退到 {VgmstreamRepoFallback}");
            found = await FindReleaseAssetAsync(client, VgmstreamRepoFallback,
                name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^vgmstream-win(64)?\.zip$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase), log, ct);
        }

        var targetDir = Path.Combine(UtilsDir, Path.GetFileNameWithoutExtension(found.Name));
        var zipPath = Path.Combine(UtilsDir, found.Name);
        await DownloadFileAsync(client, found.Url, zipPath, found.Name, UtilsDir, log, progress, ct);

        AudioPipeline.SafeLog(log, $"解压到 {targetDir} ...");
        ExtractZipTo(zipPath, targetDir);
        try { File.Delete(zipPath); } catch { }
        return targetDir;
    }

    /// <summary>
    /// 下载 wwiser 的 <c>wwiser.pyz</c> 和它的名字库 <c>wwnames.db3</c> 到 <c>exe\utils</c> 下。
    /// 两个都下、且必须同目录：少了 db，txtp 名字里的 gamesync 只会写成裸 hash
    /// （`[3984055919=964811743]` 而不是 `[3984055919=Lv2]`），和别的次生成的文件对不上。
    /// </summary>
    public static async Task<string> DownloadWwiserAsync(Action<string>? log,
        IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        Directory.CreateDirectory(UtilsDir);
        AudioPipeline.SafeLog(log, $"查询 {WwiserRepo} 的最新 release ...");

        var found = await FindReleaseAssetAsync(client, WwiserRepo,
            name => name.Equals("wwiser.pyz", StringComparison.OrdinalIgnoreCase), log, ct);

        var target = Path.Combine(UtilsDir, "wwiser.pyz");
        await DownloadFileAsync(client, found.Url, target, "wwiser.pyz", UtilsDir, log, progress, ct);

        // 名字库是可选的（没有也能跑），但缺了它 txtp 只会用数字 hash 命名
        try
        {
            var db = await FindReleaseAssetAsync(client, WwiserRepo,
                name => name.Equals("wwnames.db3", StringComparison.OrdinalIgnoreCase), log, ct);
            await DownloadFileAsync(client, db.Url, Path.Combine(UtilsDir, "wwnames.db3"),
                "wwnames.db3", UtilsDir, log, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AudioPipeline.SafeLog(log, $"[!] wwnames.db3 没下到（{ex.Message}）；wwiser 会退回用数字 hash 命名");
        }

        return target;
    }

    /// <summary>
    /// 下载并解压 python.org 的 **embeddable** Python 到 <c>utils\python</c>，
    /// 返回 <c>python.exe</c> 的路径。以后跑 wwiser 就固定用这个解释器，不再依赖系统 Python。
    ///
    /// 注意 embeddable 包**不含 tkinter**，而 <c>wwiser.pyz</c> 的 <c>__main__.py</c> 会
    /// 无条件 <c>import wwiser.wgui</c>（wgui 需要 tkinter）。所以我们不走 <c>__main__.py</c>，
    /// 改用自带的 <c>utils\wwiser_cli.py</c> 直接把 pyz 加进 sys.path 再调 <c>wwiser.wcli</c>。
    /// </summary>
    public static async Task<string> DownloadPythonEmbeddedAsync(Action<string>? log,
        IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        var url = await FindPythonEmbedUrlAsync(client, log, ct);
        var version = Path.GetFileName(url).Replace("python-", "").Replace("-embed-amd64.zip", "");

        Directory.CreateDirectory(UtilsDir);
        var targetDir = PythonDir;
        var zipPath = Path.Combine(UtilsDir, $"python-embed-amd64-{version}.zip");

        await DownloadFileAsync(client, url, zipPath, $"python-{version}-embed-amd64.zip", UtilsDir, log,
            progress, ct);

        AudioPipeline.SafeLog(log, $"解压到 {targetDir} ...");
        if (Directory.Exists(targetDir)) { try { Directory.Delete(targetDir, true); } catch { } }
        ExtractZipTo(zipPath, targetDir);
        try { File.Delete(zipPath); } catch { }

        var exe = Path.Combine(targetDir, "python.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException($"解压后没找到 python.exe: {exe}");
        return exe;
    }

    /// <summary>
    /// 在 python.org 的版本目录里从最新往下找**第一个真的提供了 embed-amd64.zip 的稳定版本**；
    /// 找不到就回落到写死的版本。用 HEAD 探测，避免把 12 MB 下下来才发现 404。
    /// </summary>
    private static async Task<string> FindPythonEmbedUrlAsync(HttpClient client, Action<string>? log,
        CancellationToken ct)
    {
        const string listUrl = "https://www.python.org/ftp/python/";
        const string fallbackVersion = "3.14.8";

        try
        {
            AudioPipeline.SafeLog(log, $"查询 python.org 的最新版本 ...");
            var html = await GetStringOrThrowAsync(client, listUrl, ct);
            var versions = System.Text.RegularExpressions.Regex
                .Matches(html, @"href=""(3\.(\d+)\.(\d+))/""")
                .Select(m => (Text: m.Groups[1].Value, Minor: int.Parse(m.Groups[2].Value),
                    Patch: int.Parse(m.Groups[3].Value)))
                .Distinct()
                .OrderByDescending(v => v.Minor).ThenByDescending(v => v.Patch)
                .Take(12)
                .ToList();

            foreach (var v in versions)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = $"{listUrl}{v.Text}/python-{v.Text}-embed-amd64.zip";
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, candidate);
                    using var resp = await client.SendAsync(head, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        AudioPipeline.SafeLog(log, $"    选中最新的嵌入式 Python: {v.Text}");
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 探测失败就试下一个
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AudioPipeline.SafeLog(log, $"    查询失败（{ex.Message}），回落到 {fallbackVersion}");
        }

        return $"{listUrl}{fallbackVersion}/python-{fallbackVersion}-embed-amd64.zip";
    }

    private static async Task DownloadFileAsync(HttpClient client, string url, string targetPath, string fileName,
        string labelDir, Action<string>? log, IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        AudioPipeline.SafeLog(log, $"下载 {url}");
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"下载失败 {url}\n（{ex.GetType().Name}: {ex.Message}）\n" +
                $"可以手动下载后放到 {labelDir} 下。", ex);
        }

        using (response)
        {
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(targetPath);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            int lastReported = -1;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                var kb = (int)(received / 1024);
                if (kb / 512 != lastReported)
                {
                    lastReported = kb / 512;
                    progress?.Report(new ToolDownloadProgress
                    {
                        Message = total > 0
                            ? $"下载 {fileName}: {received / 1024} / {total / 1024} KB"
                            : $"下载 {fileName}: {received / 1024} KB",
                        Received = received,
                        Total = total
                    });
                }
            }
        }

        AudioPipeline.SafeLog(log, $"完成: {targetPath}");
    }

    /// <summary>
    /// 把 zip 解压到 targetDir。若压缩包里只有一层顶层目录，会把它的内容上提一层，
    /// 避免出现 utils\xxx\子目录\ 这种多余嵌套。
    /// </summary>
    public static void ExtractZipTo(string zipPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var staging = targetDir + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, true);

            var entries = Directory.GetFileSystemEntries(staging);
            var inner = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : null;
            var source = inner ?? staging;

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var dest = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    #endregion
}
