using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WemLabeler.Pipeline;

namespace WemLabeler;

/// <summary>
/// MainWindow 的「提取音频」标签页：承载由 Python 脚本迁移过来的音频流水线，
/// 作业在本标签页内联执行（日志 + 进度 + 取消），不再弹独立窗口；
/// 另外负责「来源关联」里 bank / txtp 信息的解析，以及「播放所属 txtp」。
/// </summary>
public partial class MainWindow
{
    // 提取页的内联作业状态
    private CancellationTokenSource? _extractCts;
    private bool _extractRunning;
    private readonly ConcurrentQueue<string> _extractLogQueue = new();
    private DispatcherTimer? _extractFlushTimer;

    /// <summary>XAML 全部加载完成前，标签页切换事件不能去碰控件。</summary>
    private bool _uiReady;

    /// <summary>程序自己在刷新路径框时置位，避免把刷新的值当成用户输入再写回配置。</summary>
    private bool _suppressPathEvents;

    /// <summary>vgmstream 自动下载任务（多次请求共用一个）。</summary>
    private Task<string>? _vgmDownloadTask;

    /// <summary>txtp 反向索引是否已经安排过后台构建（避免每次选中都重建）。</summary>
    private bool _txtpIndexBuildKicked;

    #region 路径解析

    /// <summary>解析项目根目录与各子目录；结果缓存，直到路径配置变化。</summary>
    private PipelinePaths EnsurePaths()
    {
        if (_paths != null) return _paths;

        // 用户设过就直接用，**绝不**拿自动探测去覆盖他的选择 —— 原来的写法是
        // 「配置里的目录不像项目根」就跑去自动探测，然后把探测结果写回配置，
        // 于是用户设的值被顶掉、界面显示成别的路径、点作业还被要求重选。
        string baseDir;
        if (!string.IsNullOrWhiteSpace(_config.BaseDir))
        {
            baseDir = _config.BaseDir!;
        }
        else
        {
            // 只有从没设过时才自动探测（往上找带标志目录的那层，是给「exe 就放在数据目录里」的便利）
            var detected = PipelinePaths.DetectBaseDir(null, _loadedCsvPath);
            baseDir = detected ?? string.Empty;
            if (detected != null)
            {
                _config.BaseDir = detected;
                ConfigManager.Save(_config);
            }
        }

        _paths = new PipelinePaths(baseDir, _config);
        return _paths;
    }

    /// <summary>
    /// 需要项目根目录的作业在开始前调用：没设置就引导用户选一个。
    /// 返回 false 表示这次没法继续，调用方直接放弃。
    /// </summary>
    private bool EnsureProjectRoot()
    {
        var paths = EnsurePaths();
        // 只要用户设过目录就算数：那几个标志目录本身就是本程序的产物，不是前提条件。
        // 需要的话由程序建出来（这里顺带把各个输出目录都建好）。
        if (!string.IsNullOrWhiteSpace(paths.BaseDir))
        {
            paths.EnsureDirectories();
            return true;
        }

        SetStatus(Locale.S("status_need_basedir"));
        ShowProjectRootHint(true);

        // 从没设过才问一次
        var dir = PickFolder(Locale.S("dlg_set_basedir"), null);
        if (dir == null) return false;

        _config.BaseDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        var refreshed = EnsurePaths();
        refreshed.EnsureDirectories();
        ShowProjectRootHint(false);
        RefreshExtractPaths();
        SetStatus(Locale.S("status_basedir_set", refreshed.BaseDir));
        return true;
    }

    /// <summary>显示/隐藏「请先选择项目根目录」的提示条。</summary>
    private void ShowProjectRootHint(bool show)
    {
        if (ProjectRootHint == null) return;
        ProjectRootHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InvalidatePaths()
    {
        _paths = null;
        _txtpRepo = null;
        _txtpRepoBaseDir = null;
        _txtpIndexBuildKicked = false;
    }

    /// <summary>取得 txtp 目录的索引对象；目录不存在时返回 null 并把原因写到状态栏。</summary>
    private TxtpRepository? EnsureTxtpRepository(PipelinePaths paths)
    {
        if (!Directory.Exists(paths.TxtpDir))
        {
            SetStatus(paths.HasBaseDir
                ? Locale.S("status_txtp_dir_missing", paths.TxtpDir)
                : Locale.S("status_need_basedir"));
            return null;
        }
        if (_txtpRepo == null ||
            !string.Equals(_txtpRepoBaseDir, paths.TxtpDir, StringComparison.OrdinalIgnoreCase))
        {
            _txtpRepo = new TxtpRepository(paths.TxtpDir, paths.WemIndexJson, paths.WemResWemDir);
            _txtpRepoBaseDir = paths.TxtpDir;
        }
        return _txtpRepo;
    }

    /// <summary>不触碰 UI 的版本，供后台线程使用。</summary>
    private TxtpRepository? TryEnsureTxtpRepositoryQuiet()
    {
        try
        {
            var paths = EnsurePaths();
            if (!Directory.Exists(paths.TxtpDir)) return null;
            if (_txtpRepo == null ||
                !string.Equals(_txtpRepoBaseDir, paths.TxtpDir, StringComparison.OrdinalIgnoreCase))
            {
                _txtpRepo = new TxtpRepository(paths.TxtpDir, paths.WemIndexJson, paths.WemResWemDir);
                _txtpRepoBaseDir = paths.TxtpDir;
            }
            return _txtpRepo;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 确保 vgmstream 可用：exe 旁边的 utils 里有就直接用，没有就**自动下载**，
    /// 不再弹「未配置，是否设置路径」的对话框。返回 null 表示这次拿不到（下载失败）。
    /// </summary>
    private async Task<string?> EnsureVgmstreamAsync()
    {
        var existing = ToolLocator.FindVgmstreamCli(_config.VgmstreamPath);
        if (existing != null)
        {
            if (!string.Equals(_config.VgmstreamPath, existing, StringComparison.OrdinalIgnoreCase))
            {
                _config.VgmstreamPath = existing;
                ConfigManager.Save(_config);
            }
            return existing;
        }

        _vgmDownloadTask ??= StartVgmstreamDownload();
        try
        {
            await _vgmDownloadTask;
        }
        catch (Exception ex)
        {
            VgmLog($"[auto] vgmstream 自动下载失败: {ex.Message}");
            _vgmDownloadTask = null;
            StatusProgress.Visibility = Visibility.Collapsed;
            SetStatus(Locale.S("status_vgm_download_failed", ex.Message));
            MessageBox.Show(this, Locale.S("dlg_vgm_download_failed", ex.Message, ToolLocator.UtilsDir),
                Locale.S("dlg_error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            StatusProgress.Visibility = Visibility.Collapsed;
        }

        var found = ToolLocator.FindVgmstreamCli();
        if (found == null)
        {
            SetStatus(Locale.S("status_vgm_download_failed", Locale.S("status_vgm_still_missing")));
            return null;
        }

        _config.VgmstreamPath = found;
        ConfigManager.Save(_config);
        VgmLog($"[auto] vgmstream 就绪: {found}");
        SetStatus(Locale.S("status_vgm_ready", found));
        RefreshExtractPaths();
        return found;
    }

    /// <summary>流水线作业里用：找到就用，找不到就地下载（日志里有进度），失败返回 null。</summary>
    private string? EnsureVgmstreamInJob(Action<string>? log)
    {
        var existing = ToolLocator.FindVgmstreamCli(_config.VgmstreamPath);
        if (existing != null) return existing;

        AudioPipeline.SafeLog(log, Locale.S("pipe_vgm_auto_download", ToolLocator.UtilsDir));
        try
        {
            var reporter = new Progress<ToolDownloadProgress>(p => AudioPipeline.SafeLog(log, "  " + p.Message));
            ToolLocator.DownloadVgmstreamAsync(msg => AudioPipeline.SafeLog(log, "  " + msg), reporter,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AudioPipeline.SafeLog(log, Locale.S("pipe_vgm_download_failed", ex.Message));
            return null;
        }

        var found = ToolLocator.FindVgmstreamCli();
        if (found != null)
        {
            _config.VgmstreamPath = found;
            ConfigManager.Save(_config);
            AudioPipeline.SafeLog(log, Locale.S("status_vgm_ready", found));
        }
        return found;
    }

    /// <summary>后台开始下载 vgmstream（exe 旁边的 utils），并在状态栏显示进度。</summary>
    private Task<string> StartVgmstreamDownload()
    {
        SetStatus(Locale.S("status_vgm_downloading"));
        StatusProgress.Visibility = Visibility.Visible;
        VgmLog($"[auto] vgmstream 未找到，自动下载到 {ToolLocator.UtilsDir}");

        var reporter = new Progress<ToolDownloadProgress>(p => SetStatus(p.Message));
        return Task.Run(() => ToolLocator.DownloadVgmstreamAsync(msg => VgmLog("[auto] " + msg), reporter,
            CancellationToken.None));
    }

    private void OpenPathInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true
                });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true
                });
            else
                SetStatus(Locale.S("status_path_missing", path));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    #endregion

    #region 「来源关联」：bank / txtp

    /// <summary>
    /// 刷新「来源关联」里的 WemRes / Banks / Txtp / 大小。
    /// 数据全部来自 CSV 已有的列，不额外解析 banks.xml：
    ///   Banks ← FoundInBankRes / BankCount / Banks 列
    ///   Txtp  ← TxtpFiles 列（由 link_unused_wem.py 生成）
    /// </summary>
    private void RefreshSourceInfo()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count)
        {
            InfoWemRes.Text = Locale.S("lbl_wemres", "—");
            InfoBanks.Text = Locale.S("lbl_banks", "—");
            InfoTxtp.Text = Locale.S("lbl_txtp", "—");
            TxtpListPanel.Children.Clear();
            InfoWwiseID.Text = "";
            return;
        }

        var entry = _entries[_currentIndex];
        InfoWemRes.Text = Locale.S("lbl_wemres", entry.CoordSummary);

        // 所属 bank
        string bankText;
        if (!string.IsNullOrWhiteSpace(entry.Banks))
        {
            // 旧格式 CSV 直接带 bank 文件名列表
            bankText = entry.Banks;
        }
        else if (entry.FoundInBanks == "是" || entry.FoundInBanks.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            bankText = string.IsNullOrWhiteSpace(entry.BankCount)
                ? Locale.S("lbl_bank_referenced")
                : Locale.S("lbl_bank_count", entry.BankCount);
        }
        else
        {
            bankText = "—";
        }
        InfoBanks.Text = Locale.S("lbl_banks", bankText);

        // 所属 txtp：CSV 的 TxtpFiles 列，其次 txtp 反向索引；每个 txtp 一行、自带「打开 / 播放」
        RefreshTxtpRows(entry);

        InfoWwiseID.Text = string.IsNullOrEmpty(entry.WemSize) ? "" : Locale.S("lbl_wwiseid", entry.SizeDisplay);
    }

    /// <summary>「来源关联」里关联到该 WEM 的一个 txtp：显示名 + 解析到的完整路径（可能为 null）。</summary>
    private sealed record TxtpRef(string Name, string? FullPath);

    /// <summary>
    /// 收集引用了该 WEM 的 txtp：
    ///   1. CSV 的 TxtpFiles 列（link_unused_wem.py 算好的，优先）；
    ///   2. txtp 目录的反向索引（TxtpRepository）。
    /// <paramref name="buildIndexIfNeeded"/> 为 true 时会在索引没建过时就地构建一次（会短暂卡住 UI）。
    /// </summary>
    private List<TxtpRef> CollectTxtpRefs(WemEntry entry, bool buildIndexIfNeeded)
    {
        var refs = new List<TxtpRef>();

        void AddRef(string name, string? full)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (refs.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            refs.Add(new TxtpRef(name, full));
        }

        var repo = TryEnsureTxtpRepositoryQuiet();

        foreach (var name in entry.TxtpFileList)
            AddRef(name, repo?.ResolveTxtpPath(name));

        if (refs.Count > 0 || repo == null || !long.TryParse(entry.WemID, out var wemId)) return refs;

        if (!repo.HasTxtpIndex && buildIndexIfNeeded)
        {
            SetBusy(true);
            SetStatus(Locale.S("status_txtp_indexing"));
            try
            {
                repo.BuildTxtpIndex(msg => VgmLog(msg), CancellationToken.None);
            }
            catch (Exception ex)
            {
                VgmLog($"[txtp-refs] 建立 txtp 索引失败: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        if (repo.HasTxtpIndex)
            foreach (var file in repo.FindTxtpFilesForWem(wemId))
                AddRef(Path.GetFileName(file), file);

        return refs;
    }

    /// <summary>
    /// 后台构建一次 txtp 反向索引（CSV 没有 TxtpFiles 列时，
    /// 「来源关联」才能列出真正引用了该 WEM 的 txtp），建好后刷新当前显示。
    /// </summary>
    private void MaybeBuildTxtpIndexInBackground()
    {
        if (_txtpIndexBuildKicked) return;
        var repo = TryEnsureTxtpRepositoryQuiet();
        if (repo == null || repo.HasTxtpIndex) return;

        _txtpIndexBuildKicked = true;
        _ = Task.Run(() =>
        {
            try
            {
                repo.BuildTxtpIndex(msg => VgmLog(msg), CancellationToken.None);
            }
            catch (Exception ex)
            {
                VgmLog($"[txtp-refs] 后台建立 txtp 索引失败: {ex.Message}");
            }

            try
            {
                Dispatcher.Invoke(() => { if (_currentIndex >= 0) RefreshSourceInfo(); });
            }
            catch (Exception ex)
            {
                VgmLog($"[txtp-refs] 刷新来源关联失败: {ex.Message}");
            }
        });
    }

    /// <summary>把当前 WEM 关联的每个 txtp 渲染成一行：文件名 + 「打开（默认文本编辑器）」+「播放」。</summary>
    private void RefreshTxtpRows(WemEntry entry)
    {
        TxtpListPanel.Children.Clear();

        var refs = CollectTxtpRefs(entry, buildIndexIfNeeded: false);
        if (refs.Count == 0)
        {
            InfoTxtp.Text = Locale.S("lbl_txtp", "—");
            MaybeBuildTxtpIndexInBackground();
            return;
        }

        InfoTxtp.Text = Locale.S("lbl_txtp_header");

        foreach (var reference in refs)
        {
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = GridLength.Auto
            });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            {
                Width = GridLength.Auto
            });

            var name = new System.Windows.Controls.TextBlock
            {
                Text = reference.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = reference.FullPath ?? reference.Name
            };
            System.Windows.Controls.Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var found = reference.FullPath != null && File.Exists(reference.FullPath);

            // 注意：按钮**不**在找不到文件时禁用 —— 禁用的按钮点下去毫无反馈，
            // 看起来就像「点了没反应」。改为始终可点，由处理函数明确告知结果。
            var openButton = new System.Windows.Controls.Button
            {
                Content = Locale.S("btn_open_txtp_editor"),
                Height = 22,
                MinWidth = 48,
                Padding = new Thickness(6, 0, 6, 0),
                FontSize = 11,
                ToolTip = found
                    ? Locale.S("status_open_txtp_hint", reference.FullPath ?? reference.Name)
                    : Locale.S("status_txtp_missing_file", reference.Name)
            };
            // 左键 = 系统默认程序打开；不行退回记事本，再不行弹应用内查看器
            openButton.Click += (_, _) => OpenTxtpInEditor(reference.Name, reference.FullPath);
            System.Windows.Controls.Grid.SetColumn(openButton, 1);
            row.Children.Add(openButton);

            var playButton = new System.Windows.Controls.Button
            {
                Content = Locale.S("btn_play_txtp"),
                Height = 22,
                MinWidth = 48,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                FontSize = 11,
                IsEnabled = found,
                ToolTip = found ? Locale.S("status_play_txtp_hint", reference.Name) : null
            };
            playButton.Click += (_, _) => PlayTxtpFile(reference.FullPath!);
            System.Windows.Controls.Grid.SetColumn(playButton, 2);
            row.Children.Add(playButton);

            TxtpListPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// 在应用内打开一个小窗口，直接把 txtp 的内容显示出来。
    /// 这是「打开」按钮的左键行为 —— 不依赖系统关联，也不依赖外部程序能不能弹出窗口，
    /// 所以一定能看到内容。想用外部编辑器请用右键菜单。
    /// </summary>
    private void ShowTxtpViewer(string displayName, string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            var message = Locale.S("dlg_txtp_missing_file", displayName, fullPath ?? "—");
            SetStatus(Locale.S("status_txtp_missing_file", displayName));
            MessageBox.Show(this, message, Locale.S("dlg_error_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pathText = new System.Windows.Controls.TextBlock
        {
            Text = fullPath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var contentBox = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(6),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };

        var reloadButton = new System.Windows.Controls.Button
        {
            Content = Locale.S("btn_reload"), Width = 90, Height = 26, Margin = new Thickness(0, 8, 8, 0)
        };
        var externalButton = new System.Windows.Controls.Button
        {
            Content = Locale.S("btn_open_external"), Width = 150, Height = 26, Margin = new Thickness(0, 8, 8, 0)
        };
        var closeButton = new System.Windows.Controls.Button
        {
            Content = Locale.S("btn_close"), Width = 90, Height = 26, Margin = new Thickness(0, 8, 0, 0)
        };
        externalButton.Click += (_, _) => OpenTxtpInEditor(displayName, fullPath);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(reloadButton);
        buttons.Children.Add(externalButton);
        buttons.Children.Add(closeButton);

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        System.Windows.Controls.Grid.SetRow(pathText, 0);
        System.Windows.Controls.Grid.SetRow(contentBox, 1);
        System.Windows.Controls.Grid.SetRow(buttons, 2);
        grid.Children.Add(pathText);
        grid.Children.Add(contentBox);
        grid.Children.Add(buttons);

        var window = new Window
        {
            Title = Locale.S("txtp_viewer_title", displayName),
            Owner = this,
            Width = 760,
            Height = 580,
            MinWidth = 420,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = grid
        };

        void LoadContent()
        {
            try
            {
                const long maxBytes = 8L * 1024 * 1024;
                var info = new FileInfo(fullPath);
                if (info.Length > maxBytes)
                {
                    using var stream = File.OpenRead(fullPath);
                    var buffer = new byte[maxBytes];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    contentBox.Text = Encoding.UTF8.GetString(buffer, 0, read) + Locale.S("txtp_viewer_truncated");
                }
                else
                {
                    contentBox.Text = File.ReadAllText(fullPath);
                }
                contentBox.CaretIndex = 0;
                contentBox.ScrollToHome();
            }
            catch (Exception ex)
            {
                contentBox.Text = Locale.S("txtp_viewer_failed", ex.Message);
                VgmLog($"[txtp viewer] 读取失败: {fullPath} — {ex.Message}");
            }
        }

        reloadButton.Click += (_, _) => LoadContent();
        closeButton.Click += (_, _) => window.Close();

        LoadContent();
        VgmLog($"[open txtp] 应用内查看: {fullPath}");
        SetStatus(Locale.S("status_txtp_viewing", displayName));
        window.Show();
    }

    /// <summary>
    /// 「打开」txtp：
    ///   ① 交给系统默认程序 —— 最标准的写法，就这一句；
    ///   ② 抛异常就退回记事本；
    ///   ③ 再不行弹出应用内查看器 —— 总之一定给你看到内容。
    /// </summary>
    private void OpenTxtpInEditor(string displayName, string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            ShowTxtpViewer(displayName, fullPath);
            return;
        }

        // ① 系统默认程序
        try
        {
            Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            VgmLog($"[open txtp] 用系统默认程序打开: {fullPath}");
            SetStatus(Locale.S("status_txtp_opened", displayName, Locale.S("opener_system_default")));
            return;
        }
        catch (Exception ex)
        {
            VgmLog($"[open txtp] 系统默认程序打开失败: {ex.GetType().Name}: {ex.Message}");
        }

        // ② 记事本
        try
        {
            var notepad = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (!File.Exists(notepad)) notepad = "notepad.exe";

            var psi = new ProcessStartInfo { FileName = notepad, UseShellExecute = false };
            psi.ArgumentList.Add(fullPath);
            Process.Start(psi);

            VgmLog($"[open txtp] 用记事本打开: {fullPath}");
            SetStatus(Locale.S("status_txtp_opened", displayName, "notepad"));
            return;
        }
        catch (Exception ex)
        {
            VgmLog($"[open txtp] 记事本打开失败: {ex.GetType().Name}: {ex.Message}");
        }

        // ③ 兜底：应用内查看器
        ShowTxtpViewer(displayName, fullPath);
    }


    /// <summary>播放指定的 txtp 文件（解析其中的 wem 路径后交给 vgmstream 解码播放）。</summary>
    private void PlayTxtpFile(string path)
    {
        if (!File.Exists(path))
        {
            SetStatus(Locale.S("status_txtp_missing_file", Path.GetFileName(path)));
            return;
        }
        OpenTxtpFile(path);
    }

    #endregion

    #region 标签页切换

    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 构造期间 TabControl 会先选中第一个标签页，此时后面的控件还没创建
        if (!_uiReady) return;
        if (ReferenceEquals(e.Source, MainTabs) && ReferenceEquals(MainTabs.SelectedItem, ExtractTab))
            RefreshExtractPaths();
    }

    #endregion

    #region 提取音频页：路径字段

    /// <summary>
    /// ⓪ 用 odradek 的命令行自动导出全部资源。
    /// 流程：找 odradek.exe → 找游戏根目录 → 找并解析 links-*.db → 组目标清单 → 分批导出 → 按文件名归类。
    /// </summary>
    /// <summary>
    /// ⓪ 直接读游戏文件，把后面几步需要的东西一次写出来。
    ///
    /// 流程：找游戏根目录 → 打开 streaming_graph.core → 用纯元数据 + 按需读组解析
    /// GraphSound → GraphProgram → NodeConstants → WwiseID 链路，直接写
    /// sound_wem_mapping_export.json（链部分）、wem_index.json，并把 bank 直接落成 .bnk。
    /// **不再落任何按对象的资源 JSON，也不需要 odradek.exe / links-*.db。**
    /// </summary>
    private void BtnOdradekExport_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectRoot()) return;

        var gameRoot = _config.GameRoot;
        if (!OdradekExporter.IsGameRoot(gameRoot))
        {
            SetStatus(Locale.S("status_need_game_root"));
            gameRoot = OdradekExporter.AutoDetectGameRoot(gameRoot, null, CancellationToken.None);
            if (gameRoot == null)
            {
                gameRoot = PickFolder(Locale.S("dlg_set_game_root"), _config.GameRoot);
                if (gameRoot == null) return;
            }
        }
        _config.GameRoot = gameRoot;
        ConfigManager.Save(_config);
        RefreshExtractPaths();

        var rootFound = gameRoot!;   // 上面已判过 null，进 lambda 后编译器看不出来
        StartExtractJob(Locale.S("btn_odradek_export"), (progress, log, ct) =>
        {
            var paths = EnsurePaths();
            var summary = OdradekExporter.ExportResources(rootFound, paths, progress, log, ct);
            var text = Locale.S("pipe_odradek_summary", summary.Sounds, summary.Entries, summary.Banks);
            AudioPipeline.SafeLog(log, text);
            return text;
        });
    }

    /// <summary>
    /// 抽取 WEM 原始音频（约 10 GB）。先警告体积，再问「用默认目录吗」，
    /// 选否则弹出目录选择窗口。
    /// </summary>
    private void BtnWemAudioExport_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectRoot()) return;

        var paths = EnsurePaths();
        // 合并后只有一个 WEM 目录：既是 pipeline 的输入，也是导出 WEM 音频的默认输出
        var defaultDir = paths.WemResWemDir;

        var answer = MessageBox.Show(this,
            Locale.S("dlg_wem_audio_size_warning", defaultDir),
            Locale.S("dlg_wem_audio_title"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return;

        string targetDir;
        if (answer == MessageBoxResult.Yes)
        {
            targetDir = defaultDir;
        }
        else
        {
            var picked = PickFolder(Locale.S("dlg_wem_audio_choose"), defaultDir);
            if (picked == null) return;
            targetDir = picked;
        }

        var gameRoot = _config.GameRoot;
        if (!OdradekExporter.IsGameRoot(gameRoot))
        {
            SetStatus(Locale.S("status_need_game_root"));
            gameRoot = OdradekExporter.AutoDetectGameRoot(gameRoot, null, CancellationToken.None)
                       ?? PickFolder(Locale.S("dlg_set_game_root"), _config.GameRoot);
            if (gameRoot == null) return;
        }

        _config.GameRoot = gameRoot;
        ConfigManager.Save(_config);
        RefreshExtractPaths();

        var chosen = targetDir;
        var rootFound = gameRoot!;   // 上面已判过 null，进 lambda 后编译器看不出来
        StartExtractJob(Locale.S("btn_wem_audio_export"), (progress, log, ct) =>
        {
            var count = OdradekExporter.ExportWemAudio(rootFound, chosen, progress, log, ct);
            var text = Locale.S("pipe_wem_audio_summary", count, chosen);
            AudioPipeline.SafeLog(log, text);
            return text;
        });
    }
    /// <summary>
    /// 把当前解析出来的各路径显示到「提取音频」页的输入框里。
    /// 输入框现在都是可编辑的，所以**不会**再往里写「(未设置)」这类占位文字
    /// （否则会被当成用户输入的真值）；没设置就把框留空，并在标签后面加个提示。
    /// </summary>
    private void RefreshExtractPaths()
    {
        var paths = EnsurePaths();
        var baseExists = paths.HasBaseDir;                                        // 目录确实存在（用户设过就算数）
        var notSetSuffix = Locale.S("lbl_suffix_not_set");
        var notFoundSuffix = Locale.S("lbl_suffix_not_found");

        _suppressPathEvents = true;
        try
        {
            // 正在某个框里打字时不要覆盖它 —— 否则每敲一个键都会被程序重写，光标会跳、内容会被清空
            void SetBox(System.Windows.Controls.TextBox box, string value)
            {
                if (box.IsKeyboardFocusWithin) return;
                box.Text = value;
            }

            // 原样显示配置里的值，哪怕是还不存在的路径 —— 否则一边输一边被判为无效，框会被反复清空。
            SetBox(PathBaseDirBox, paths.BaseDir);

            // 派生目录：项目根目录没设好就留空（不要显示 "Exported_Audio" 这种相对路径）；
            // 用户单独指定过的项仍然照实显示。
            SetBox(PathOutputDirBox, paths.OutputDirOverride != null || baseExists ? paths.OutputDir : "");
            SetBox(PathWemResWemDirBox, paths.WemResWemDirOverride != null || baseExists ? paths.WemResWemDir : "");

            // vgmstream：手工指定的路径优先，其次 exe 旁边的 utils
            var vgm = ToolLocator.FindVgmstreamCli(_config.VgmstreamPath);
            SetBox(PathVgmstreamBox, vgm ?? "");
            if (vgm != null && !string.Equals(_config.VgmstreamPath, vgm, StringComparison.OrdinalIgnoreCase))
            {
                _config.VgmstreamPath = vgm;
                ConfigManager.Save(_config);
            }

            // 标签后缀：把「未设置 / 未找到」放在标签上，不污染输入框
            // 与其它行保持一致：只在「根本没值」时标注（未设置）。路径存在与否、
            // 像不像项目根目录，交给下面的黄色提示条表达 —— 否则用户从资源管理器粘贴
            // 一个带引号的路径时，标签就会莫名翻成「未设置」。
            PathBaseDirLabel.Text = string.IsNullOrWhiteSpace(paths.BaseDir)
                ? Locale.S("lbl_path_basedir") + notSetSuffix
                : Locale.S("lbl_path_basedir");
            PathOutputDirLabel.Text = paths.OutputDirOverride != null || baseExists
                ? Locale.S("lbl_path_output") : Locale.S("lbl_path_output") + notSetSuffix;
            PathWemResWemLabel.Text = paths.WemResWemDirOverride != null || baseExists
                ? Locale.S("lbl_path_wemreswem") : Locale.S("lbl_path_wemreswem") + notSetSuffix;

            // 直接读游戏文件导出：游戏根目录

            SetBox(PathGameRootBox, _config.GameRoot ?? "");
            PathGameRootLabel.Text = OdradekExporter.IsGameRoot(_config.GameRoot)
                ? Locale.S("lbl_path_game_root") : Locale.S("lbl_path_game_root") + notSetSuffix;
        }
        finally
        {
            _suppressPathEvents = false;
        }

        // 只有「从没设过」才提示；用户设过就一律不再唠叨（那些标志目录是产物，不是前提）
        ShowProjectRootHint(string.IsNullOrWhiteSpace(paths.BaseDir));
        if (ProjectRootHintText != null && string.IsNullOrWhiteSpace(paths.BaseDir))
            ProjectRootHintText.Text = Locale.S("hint_need_basedir");
    }

    /// <summary>
    /// 路径输入框允许直接手输。改动即写回 config.json；
    /// 程序自己刷新界面时用 _suppressPathEvents 屏蔽，避免打架。
    /// </summary>
    private void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_uiReady || _suppressPathEvents) return;
        if (sender is not System.Windows.Controls.TextBox box) return;

        // 容错：去掉首尾空白，以及从资源管理器「复制为路径」带来的包裹引号
        var text = box.Text.Trim().Trim('"').Trim();
        string? value = text.Length == 0 ? null : text;

        if (ReferenceEquals(box, PathBaseDirBox))
        {
            if (string.Equals(_config.BaseDir, value, StringComparison.OrdinalIgnoreCase)) return;
            _config.BaseDir = value;
            ConfigManager.Save(_config);
            InvalidatePaths();
            // 这里**不能**刷新界面：刷新会重新跑自动探测，把用户正在输的内容替换掉甚至清空
            // （输到一半的路径当然「不存在」）。改为标记路径失效，等失焦后由 PathBox_LostFocus 统一刷新。
        }
        else if (ReferenceEquals(box, PathOutputDirBox))
        {
            if (string.Equals(_config.OutputAudioDir, value, StringComparison.Ordinal)) return;
            _config.OutputAudioDir = value;
            ConfigManager.Save(_config);
            InvalidatePaths();
        }
        else if (ReferenceEquals(box, PathWemResWemDirBox))
        {
            if (string.Equals(_config.WemResWemDir, value, StringComparison.Ordinal)) return;
            _config.WemResWemDir = value;
            ConfigManager.Save(_config);
            InvalidatePaths();
        }
        else if (ReferenceEquals(box, PathVgmstreamBox))
        {
            if (string.Equals(_config.VgmstreamPath, value, StringComparison.Ordinal)) return;
            _config.VgmstreamPath = value;
            ConfigManager.Save(_config);
        }
        else if (ReferenceEquals(box, PathGameRootBox))
        {
            if (string.Equals(_config.GameRoot, value, StringComparison.Ordinal)) return;
            _config.GameRoot = value;
            ConfigManager.Save(_config);
        }
    }

    /// <summary>路径框失焦后再统一刷新派生目录与标签 —— 避免一边打字一边被程序改写。</summary>
    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressPathEvents) return;
        InvalidatePaths();
        RefreshExtractPaths();
    }
    private void BtnFindGameRoot_Click(object sender, RoutedEventArgs e)
    {
        var found = OdradekExporter.AutoDetectGameRoot(_config.GameRoot, null, CancellationToken.None);
        if (found == null)
        {
            SetStatus(Locale.S("status_game_not_found"));
            return;
        }
        _config.GameRoot = found;
        ConfigManager.Save(_config);
        RefreshExtractPaths();
        SetStatus(Locale.S("status_game_found", found));
    }

    private void BtnBrowseGameRoot_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_game_root"), _config.GameRoot);
        if (dir == null) return;
        _config.GameRoot = dir;
        ConfigManager.Save(_config);
        RefreshExtractPaths();
    }

    private void BtnOpenUtilsDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ToolLocator.UtilsDir); } catch { }
        OpenPathInExplorer(ToolLocator.UtilsDir);
    }

    private void BtnBrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_basedir"), _config.BaseDir);
        if (dir == null) return;
        _config.BaseDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_basedir_set", dir));
    }

    private void BtnBrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_outputdir"), _config.OutputAudioDir ?? EnsurePaths().OutputDir);
        if (dir == null) return;
        _config.OutputAudioDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_outputdir_set", dir));
    }

    private void BtnBrowseWemResWemDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_wemreswemdir"), _config.WemResWemDir ?? EnsurePaths().WemResWemDir);
        if (dir == null) return;
        _config.WemResWemDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_wemreswemdir_set", dir));
    }


    private void BtnBrowseVgmstream_Click(object sender, RoutedEventArgs e)
    {
        SetVgmstreamPath();
        RefreshExtractPaths();
    }

    private void BtnResetPaths_Click(object sender, RoutedEventArgs e)
    {
        _config.BaseDir = null;
        _config.OutputAudioDir = null;
        _config.WemResWemDir = null;
        _config.ExportByIdDir = null;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_paths_reset", EnsurePaths().BaseDir));
    }

    private void BtnOpenBaseDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().BaseDir);
    private void BtnOpenOutputDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().OutputDir);
    private void BtnOpenWemResWemDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().WemResWemDir);
    #endregion

    #region 提取音频页：内联作业执行

    /// <summary>在本标签页内联执行一个流水线作业（后台线程 + 日志 + 进度 + 可取消）。</summary>
    private void StartExtractJob(string title,
        Func<IProgress<PipelineProgress>, Action<string>, CancellationToken, string> job)
    {
        if (_extractRunning)
        {
            SetStatus(Locale.S("status_job_busy"));
            return;
        }

        _extractRunning = true;
        _extractCts = new CancellationTokenSource();
        var token = _extractCts.Token;

        ExtractLogBox.Clear();
        while (_extractLogQueue.TryDequeue(out _)) { }
        ExtractProgress.IsIndeterminate = true;
        ExtractProgress.Value = 0;
        ExtractRunStatus.Text = Locale.S("pipe_running", title);
        SetExtractUiBusy(true);

        _extractFlushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _extractFlushTimer.Tick -= ExtractFlushTimer_Tick;
        _extractFlushTimer.Tick += ExtractFlushTimer_Tick;
        _extractFlushTimer.Start();

        var progress = new Progress<PipelineProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.Message)) ExtractRunStatus.Text = p.Message;
            if (p.Total > 0)
            {
                ExtractProgress.IsIndeterminate = false;
                ExtractProgress.Maximum = p.Total;
                ExtractProgress.Value = Math.Min(p.Current, p.Total);
            }
            else
            {
                ExtractProgress.IsIndeterminate = true;
            }
        });
        var log = new Action<string>(EnqueueExtractLog);

        Task.Run(() =>
        {
            string summary;
            bool ok;
            try
            {
                summary = job(progress, log, token);
                ok = true;
            }
            catch (OperationCanceledException)
            {
                summary = Locale.S("pipe_cancelled");
                ok = false;
            }
            catch (Exception ex)
            {
                VgmLog($"[extract] {ex}");
                summary = Locale.S("pipe_failed", ex.Message);
                ok = false;
            }

            Dispatcher.Invoke(() => FinishExtractJob(summary, ok));
        });
    }

    private void FinishExtractJob(string summary, bool ok)
    {
        _extractFlushTimer?.Stop();
        FlushExtractLog();

        EnqueueExtractLog("");
        EnqueueExtractLog("====" + " " + summary);
        FlushExtractLog();

        ExtractRunStatus.Text = summary;
        ExtractProgress.IsIndeterminate = false;
        if (ExtractProgress.Maximum <= 0) ExtractProgress.Maximum = 1;
        ExtractProgress.Value = ExtractProgress.Maximum;

        _extractRunning = false;
        SetExtractUiBusy(false);
        SetStatus(summary);
        RefreshExtractPaths();
        MaybeReloadUnusedCsv();
    }

    private void SetExtractUiBusy(bool busy)
    {
        BtnCancelJob.IsEnabled = busy;
        foreach (var button in new[]
                 {
                     BtnDownloadTools, BtnGenerateTxtp, BtnBuildMapping,
                     BtnExportAudio, BtnUnusedWem, BtnRebuildTxtpIndex, BtnExportById
                 })
        {
            button.IsEnabled = !busy;
        }
    }

    private void EnqueueExtractLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        foreach (var line in message.Split('\n')) _extractLogQueue.Enqueue(line.TrimEnd('\r'));
    }

    private void ExtractFlushTimer_Tick(object? sender, EventArgs e) => FlushExtractLog();

    private void FlushExtractLog()
    {
        if (_extractLogQueue.IsEmpty) return;
        var sb = new StringBuilder();
        while (_extractLogQueue.TryDequeue(out var line))
        {
            sb.Append(line).Append('\n');
            if (sb.Length > 200_000) break; // 一次最多刷这么多，避免长时间占用 UI 线程
        }
        ExtractLogBox.AppendText(sb.ToString());
        ExtractLogBox.ScrollToEnd();
    }

    private void BtnCancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (!_extractRunning) return;
        BtnCancelJob.IsEnabled = false;
        ExtractRunStatus.Text = Locale.S("pipe_cancelling");
        try { _extractCts?.Cancel(); } catch { }
    }

    private void BtnCopyExtractLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(ExtractLogBox.Text)) Clipboard.SetText(ExtractLogBox.Text);
        }
        catch { }
    }

    private void BtnClearExtractLog_Click(object sender, RoutedEventArgs e)
    {
        ExtractLogBox.Clear();
        while (_extractLogQueue.TryDequeue(out _)) { }
        ExtractRunStatus.Text = "";
        ExtractProgress.Value = 0;
    }

    #endregion

    #region 提取音频页：各操作按钮

    private void BtnDownloadTools_Click(object sender, RoutedEventArgs e)
    {
        StartExtractJob(Locale.S("btn_download_tools"), (progress, log, ct) =>
        {
            var reporter = new Progress<ToolDownloadProgress>(p =>
                progress.Report(new PipelineProgress { Message = p.Message }));

            log(Locale.S("pipe_download_target", ToolLocator.UtilsDir));

            // vgmstream：最新 release 的 Windows 构建（优先 64 位）
            var vgmDir = ToolLocator.DownloadVgmstreamAsync(log, reporter, ct)
                .GetAwaiter().GetResult();
            var vgmExe = ToolLocator.FindVgmstreamCli() ?? vgmDir;
            log(Locale.S("pipe_download_vgmstream_ok", vgmExe));

            // wwiser：wwiser.pyz + wwnames.db3（必须同目录，否则 gamesync 只会写成裸 hash）
            var wwiserPyz = ToolLocator.DownloadWwiserAsync(log, reporter, ct)
                .GetAwaiter().GetResult();
            log(Locale.S("pipe_download_wwiser_ok", wwiserPyz));

            // 嵌入式 Python：python.org 的 embed-amd64 → utils\python
            // （wwiser.pyz 需要解释器；嵌入式包不含 tkinter，所以走 utils\wwiser_cli.py 绕开 GUI）
            var pythonExe = ToolLocator.DownloadPythonEmbeddedAsync(log, reporter, ct)
                .GetAwaiter().GetResult();
            log(Locale.S("pipe_download_python_ok", pythonExe));

            Dispatcher.Invoke(RefreshExtractPaths);
            return Locale.S("pipe_summary_download", ToolLocator.UtilsDir);
        });
    }

    private void BtnGenerateTxtp_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProjectRoot()) return;
        var paths = EnsurePaths();

        var wwiser = ToolLocator.FindWwiserPyz();
        if (wwiser == null)
        {
            SetStatus(Locale.S("status_wwiser_missing", ToolLocator.UtilsDir));
            var r = MessageBox.Show(this, Locale.S("dlg_wwiser_missing", ToolLocator.UtilsDir),
                Locale.S("dlg_vgmstream_missing_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) BtnDownloadTools_Click(sender, e);
            return;
        }

        // 优先用 utils 下的嵌入式 Python，其次 PATH 上的系统 Python
        var python = ToolLocator.FindPython();
        if (python == null)
        {
            MessageBox.Show(this, Locale.S("dlg_python_missing", ToolLocator.PythonDir),
                Locale.S("dlg_vgmstream_missing_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartExtractJob(Locale.S("btn_generate_txtp"), (progress, log, ct) =>
        {
            var count = WwiserRunner.GenerateTxtp(paths, python, wwiser, progress, log, ct);
            return Locale.S("pipe_summary_txtp", count, paths.TxtpDir);
        });
    }

    private void BtnBuildMapping_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_build_mapping"), (progress, log, ct) =>
        {
            var r = AudioPipeline.BuildMapping(paths, progress, log, ct);
            return Locale.S("pipe_summary_mapping", r.MappingData.Count, r.Skipped, r.Errors);
        });
    }

    private void BtnExportAudio_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_export_audio"), (progress, log, ct) =>
        {
            var vgm = EnsureVgmstreamInJob(log);
            if (vgm == null) throw new InvalidOperationException(Locale.S("status_vgmstream_not_set"));

            var r = AudioPipeline.ExportFromMapping(paths, vgm, progress, log, ct);
            return Locale.S("pipe_summary_export", r.Success.Count, r.Failed.Count, r.Skipped.Count, r.Streaming.Count);
        });
    }

    private void BtnUnusedWem_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_unused_wem"), (progress, log, ct) =>
        {
            var count = AudioPipeline.BuildUnusedWemCsv(paths, progress, log, ct);
            return Locale.S("pipe_summary_unused", count, paths.UnusedWemCsv);
        });

        // 若当前加载的正是这个 CSV，作业结束后询问是否重新载入（见 MaybeReloadUnusedCsv）
        _pendingUnusedCsvReload = paths.UnusedWemCsv;
    }

    private string? _pendingUnusedCsvReload;

    /// <summary>作业结束后，如果重新生成的就是当前加载的 CSV，询问是否重载。</summary>
    private void MaybeReloadUnusedCsv()
    {
        var target = _pendingUnusedCsvReload;
        _pendingUnusedCsvReload = null;
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(_loadedCsvPath)) return;
        if (!string.Equals(Path.GetFullPath(_loadedCsvPath), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase)) return;

        var answer = MessageBox.Show(this, Locale.S("dlg_reload_csv_after_tool", _loadedCsvPath),
            Locale.S("dlg_resume_title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) LoadCsvAsync(_loadedCsvPath);
    }

    private void BtnRebuildTxtpIndex_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();
        var repo = EnsureTxtpRepository(paths);
        if (repo == null) return;

        repo.Invalidate();
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_rebuild_txtp_index"), (progress, log, ct) =>
        {
            repo.BuildTxtpIndex(log, ct);
            return Locale.S("status_txtp_index_done", repo.IndexedWemCount, repo.TxtpFileCount);
        });
    }

    private void BtnExportById_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();

        var tokens = (EventIdsBox.Text ?? "").Split(
            new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var ids = new List<long>();
        var bad = new List<string>();
        foreach (var token in tokens)
        {
            var isHex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var text = isHex ? token[2..] : token;
            var style = isHex ? System.Globalization.NumberStyles.HexNumber
                              : System.Globalization.NumberStyles.Integer;
            if (long.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
            else
                bad.Add(token);
        }

        if (bad.Count > 0)
        {
            MessageBox.Show(this, Locale.S("dlg_export_by_id_invalid", string.Join(", ", bad)),
                Locale.S("dlg_error_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ids.Count == 0)
        {
            MessageBox.Show(this, Locale.S("dlg_export_by_id_empty"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var forceRefresh = ChkForceWemCache.IsChecked == true;
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_export_by_id"), (progress, log, ct) =>
        {
            var vgm = EnsureVgmstreamInJob(log);
            if (vgm == null) throw new InvalidOperationException(Locale.S("status_vgmstream_not_set"));

            var r = AudioPipeline.ExportByIds(paths, vgm, ids, forceRefresh, progress, log, ct);
            return Locale.S("pipe_summary_byid", r.Success.Count, r.Failed.Count, r.NotFoundTxtp.Count);
        });
    }

    #endregion

    #region 播放该 WEM 所属的 txtp

    /// <summary>
    /// 找出引用了当前选中 WEM 的 txtp 并直接作为音频播放（Ctrl+T）。
    /// 平时也可以直接在「来源关联」里点某个 txtp 旁边的「播放」。
    /// </summary>
    private void PlayOwningTxtp()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;

        var entry = _entries[_currentIndex];
        var refs = CollectTxtpRefs(entry, buildIndexIfNeeded: true)
            .Where(r => r.FullPath != null && File.Exists(r.FullPath))
            .ToList();

        if (refs.Count == 0)
        {
            if (!long.TryParse(entry.WemID, out _))
                SetStatus(Locale.S("status_owner_txtp_no_id", entry.WemID));
            else
                SetStatus(Locale.S("status_owner_txtp_none", entry.WemID));
            return;
        }

        string chosen;
        if (refs.Count == 1)
        {
            chosen = refs[0].FullPath!;
        }
        else
        {
            var choices = refs
                .Select(r => new TxtpChoice(r.FullPath!, r.Name))
                .ToList();
            var picker = new TxtpPickerWindow(
                Locale.S("dlg_txtp_pick_hint", entry.WemID, choices.Count), choices) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedChoice == null) return;
            chosen = picker.SelectedChoice.Path;
        }

        SetStatus(Locale.S("status_owner_txtp_found", Path.GetFileName(chosen), refs.Count));
        VgmLog($"[owner-txtp] wem={entry.WemID} -> {chosen} (候选 {refs.Count} 个)");
        OpenTxtpFile(chosen);
    }

    /// <summary>
    /// 把一个纯数字的文件名（txtp 里的 <c>wem/&lt;id&gt;.wem</c>）解析成实际的音频文件路径。
    /// 优先用已加载 CSV 的路径，其次用 WemResWem 索引。
    /// </summary>
    private string? ResolveWemAudioPath(string numericId, Dictionary<string, string> csvLookup,
        TxtpRepository? repo)
    {
        if (csvLookup.TryGetValue(numericId, out var csvPath) &&
            !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath))
            return csvPath;

        if (repo != null && long.TryParse(numericId, out var id))
        {
            var fromIndex = repo.ResolveWemFile(id);
            if (!string.IsNullOrEmpty(fromIndex) && File.Exists(fromIndex)) return fromIndex;
        }

        return null;
    }

    #endregion
}
