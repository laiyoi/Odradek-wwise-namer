using System.IO;
using System.Text.RegularExpressions;

namespace WemLabeler.Pipeline;

/// <summary>
/// txtp 目录索引，用于两件事：
///   1. 反查「某个 WEM 属于哪个 txtp」（WEM ID → 引用它的 txtp 文件）；
///   2. 解析 txtp 里的 <c>wem/&lt;id&gt;.wem</c> 行（WEM ID → WemResWem 里的实际 .wem 路径）。
///
/// 索引是惰性构建并缓存的，构建一次后可在会话内反复使用。
/// </summary>
public sealed class TxtpRepository
{
    private static readonly Regex TxtpWemPattern = new(@"(?:##|wem/)(\d+)\.wem", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly object _buildLock = new();
    private readonly string _txtpDir;
    private readonly string _wemResJsonDir;
    private readonly string _wemResWemDir;

    private Dictionary<long, List<string>>? _txtpByWemId;
    private Dictionary<string, string>? _wemPathByWemId;

    public TxtpRepository(string txtpDir, string wemResJsonDir, string wemResWemDir)
    {
        _txtpDir = txtpDir;
        _wemResJsonDir = wemResJsonDir;
        _wemResWemDir = wemResWemDir;
    }

    public string TxtpDir => _txtpDir;
    public bool HasTxtpIndex { get { lock (_lock) return _txtpByWemId != null; } }
    public int IndexedWemCount { get { lock (_lock) return _txtpByWemId?.Count ?? 0; } }
    public int TxtpFileCount { get { lock (_lock) return _txtpByWemId == null ? 0 : _txtpFiles; } }

    private int _txtpFiles;

    /// <summary>扫描 txtp 目录，建立 WEM ID → txtp 文件列表的索引（并发调用只会真正扫描一次）。</summary>
    public void BuildTxtpIndex(Action<string>? log, CancellationToken ct)
    {
        lock (_buildLock)
        {
            lock (_lock)
            {
                if (_txtpByWemId != null) return;
            }

            var index = new Dictionary<long, List<string>>();
            int fileCount = 0;

            if (Directory.Exists(_txtpDir))
            {
                foreach (var file in Directory.EnumerateFiles(_txtpDir, "*.txtp"))
                {
                    ct.ThrowIfCancellationRequested();
                    fileCount++;
                    string content;
                    try { content = File.ReadAllText(file); }
                    catch { continue; }

                    foreach (Match m in TxtpWemPattern.Matches(content))
                    {
                        if (!long.TryParse(m.Groups[1].Value, out var wemId)) continue;
                        if (!index.TryGetValue(wemId, out var list))
                        {
                            list = new List<string>();
                            index[wemId] = list;
                        }
                        if (!list.Contains(file)) list.Add(file);
                    }
                }
            }

            lock (_lock)
            {
                _txtpByWemId = index;
                _txtpFiles = fileCount;
            }

            log?.Invoke(Locale.S("status_txtp_index_built", index.Count, fileCount));
        }
    }

    /// <summary>返回引用该 WEM 的 txtp 文件完整路径列表（已按文件名排序）。</summary>
    public IReadOnlyList<string> FindTxtpFilesForWem(long wemId)
    {
        Dictionary<long, List<string>>? index;
        lock (_lock) index = _txtpByWemId;

        if (index == null || !index.TryGetValue(wemId, out var files)) return Array.Empty<string>();
        var copy = new List<string>(files);
        copy.Sort(StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>把 txtp 文件名（或相对/绝对路径）解析成存在的完整路径，失败返回 null。</summary>
    public string? ResolveTxtpPath(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);

        var byName = Path.Combine(_txtpDir, nameOrPath);
        if (File.Exists(byName)) return Path.GetFullPath(byName);

        var baseName = Path.GetFileName(nameOrPath);
        if (!string.IsNullOrEmpty(baseName))
        {
            var byBase = Path.Combine(_txtpDir, baseName);
            if (File.Exists(byBase)) return Path.GetFullPath(byBase);
        }
        return null;
    }

    /// <summary>WEM ID → WemResWem 中实际的 .wem 文件路径（惰性索引）。</summary>
    public string? ResolveWemFile(long wemId)
    {
        lock (_lock)
        {
            if (_wemPathByWemId == null)
            {
                var index = new Dictionary<string, string>();
                if (Directory.Exists(_wemResWemDir))
                {
                    var pattern = new Regex(@"WwiseWemResource_(\d+)_(\d+)\.wem$", RegexOptions.Compiled);
                    foreach (var file in Directory.EnumerateFiles(_wemResWemDir, "*.wem"))
                    {
                        var m = pattern.Match(Path.GetFileName(file));
                        if (!m.Success) continue;
                        var jsonPath = Path.Combine(_wemResJsonDir,
                            $"WwiseWemResource_{m.Groups[1].Value}_{m.Groups[2].Value}.json");
                        if (!File.Exists(jsonPath)) continue;
                        using var doc = AudioPipeline.LoadJsonDocument(jsonPath);
                        if (doc == null) continue;
                        var rawId = AudioPipeline.JsonLong(doc.RootElement, "WemID");
                        if (rawId.HasValue) index[rawId.Value.ToString()] = file;
                    }
                }
                _wemPathByWemId = index;
            }

            return _wemPathByWemId.TryGetValue(wemId.ToString(), out var path) ? path : null;
        }
    }

    /// <summary>丢弃已缓存的索引（切换项目根目录后调用）。</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _txtpByWemId = null;
            _wemPathByWemId = null;
            _txtpFiles = 0;
        }
    }

}
