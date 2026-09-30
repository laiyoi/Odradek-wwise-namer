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

    /// <summary>vgmstream 自动下载任务（多次请求共用一个）。</summary>
    private Task<string>? _vgmDownloadTask;

    #region 路径解析

    /// <summary>解析项目根目录与各子目录；结果缓存，直到路径配置变化。</summary>
    private PipelinePaths EnsurePaths()
    {
        if (_paths != null) return _paths;

        var detected = PipelinePaths.DetectBaseDir(_config.BaseDir, _loadedCsvPath);

        // 探测不到就**不要**拿 exe 目录充数：真实用户常常把 exe 放在下载目录里，
        // 那样只会得到一堆根本不存在的路径（如 C:\下载\WemLabeler\GraphSoundRes）。
        // 保留已配置的值（可能用户手填过但目录暂时不可用），否则留空并提示用户去选。
        var baseDir = detected ?? _config.BaseDir ?? string.Empty;

        // 只在真正探测到项目根目录时回写配置
        if (detected != null && !string.Equals(_config.BaseDir, detected, StringComparison.OrdinalIgnoreCase))
        {
            _config.BaseDir = detected;
            ConfigManager.Save(_config);
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
        if (paths.HasBaseDir && PipelinePaths.LooksLikeBaseDir(paths.BaseDir)) return true;

        SetStatus(Locale.S("status_need_basedir"));
        ShowProjectRootHint(true);

        // 直接弹出选择框：用户点的就是要用到项目根目录的操作，这里问一次最省事
        var dir = PickFolder(Locale.S("dlg_set_basedir"));
        if (dir == null) return false;

        _config.BaseDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        var refreshed = EnsurePaths();
        RefreshExtractPaths();

        if (PipelinePaths.LooksLikeBaseDir(refreshed.BaseDir))
        {
            ShowProjectRootHint(false);
            SetStatus(Locale.S("status_basedir_set", refreshed.BaseDir));
            return true;
        }

        // 选的目录里没有那几个标志性子目录，提醒一下但允许继续（用户可能结构不常规）
        ShowProjectRootHint(true);
        SetStatus(Locale.S("status_basedir_suspect", refreshed.BaseDir));
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
            _txtpRepo = new TxtpRepository(paths.TxtpDir, paths.WemResJsonDir, paths.WemResWemDir);
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
                _txtpRepo = new TxtpRepository(paths.TxtpDir, paths.WemResJsonDir, paths.WemResWemDir);
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
            SetStatus(Locale.S("status_vgm_download_failed", Locale.S("lbl_not_found")));
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

        // 所属 txtp：CSV 的 TxtpFiles 列，多个用 ; 分隔
        var txtps = entry.TxtpFileList;
        InfoTxtp.Text = txtps.Count > 0
            ? Locale.S("lbl_txtp", string.Join("; ", txtps))
            : Locale.S("lbl_txtp", "—");

        InfoWwiseID.Text = string.IsNullOrEmpty(entry.WemSize) ? "" : Locale.S("lbl_wwiseid", entry.SizeDisplay);
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

    /// <summary>把当前解析出来的各路径显示到「提取音频」页的输入框里。</summary>
    private void RefreshExtractPaths()
    {
        var paths = EnsurePaths();
        var valid = paths.HasBaseDir && PipelinePaths.LooksLikeBaseDir(paths.BaseDir);
        PathBaseDirBox.Text = valid ? paths.BaseDir
            : (paths.HasBaseDir ? paths.BaseDir : Locale.S("lbl_not_set"));
        ShowProjectRootHint(!valid);
        if (ProjectRootHintText != null) ProjectRootHintText.Text = Locale.S("hint_need_basedir");
        PathOutputDirBox.Text = paths.OutputDir;
        PathWemResWemDirBox.Text = paths.WemResWemDir;
        PathTxtpDirBox.Text = paths.TxtpDir;

        // vgmstream 只从「exe 旁边的 utils」里找，找到就写回配置
        var vgm = ToolLocator.FindVgmstreamCli();
        PathVgmstreamBox.Text = vgm ?? Locale.S("lbl_not_found");
        if (vgm != null && !string.Equals(_config.VgmstreamPath, vgm, StringComparison.OrdinalIgnoreCase))
        {
            _config.VgmstreamPath = vgm;
            ConfigManager.Save(_config);
        }
    }

    private void BtnOpenUtilsDir_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ToolLocator.UtilsDir); } catch { }
        OpenPathInExplorer(ToolLocator.UtilsDir);
    }

    private void BtnBrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_basedir"));
        if (dir == null) return;
        _config.BaseDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_basedir_set", dir));
    }

    private void BtnBrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_outputdir"));
        if (dir == null) return;
        _config.OutputAudioDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_outputdir_set", dir));
    }

    private void BtnBrowseWemResWemDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_wemreswemdir"));
        if (dir == null) return;
        _config.WemResWemDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_wemreswemdir_set", dir));
    }

    private void BtnBrowseTxtpDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(Locale.S("dlg_set_txtpdir"));
        if (dir == null) return;
        _config.TxtpDir = dir;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_txtpdir_set", dir));
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
        _config.TxtpDir = null;
        _config.ExportByIdDir = null;
        ConfigManager.Save(_config);
        InvalidatePaths();
        RefreshExtractPaths();
        SetStatus(Locale.S("status_paths_reset", EnsurePaths().BaseDir));
    }

    private void BtnOpenBaseDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().BaseDir);
    private void BtnOpenOutputDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().OutputDir);
    private void BtnOpenWemResWemDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().WemResWemDir);
    private void BtnOpenTxtpDir_Click(object sender, RoutedEventArgs e) => OpenPathInExplorer(EnsurePaths().TxtpDir);

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
                     BtnDownloadTools, BtnExtractBanks, BtnGenerateTxtp, BtnBuildMapping,
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

    private void BtnExtractBanks_Click(object sender, RoutedEventArgs e)
    {
        var paths = EnsurePaths();
        if (!EnsureProjectRoot()) return;
        StartExtractJob(Locale.S("btn_extract_banks"), (progress, log, ct) =>
        {
            var count = AudioPipeline.ExtractBanks(paths, progress, log, ct);
            return Locale.S("pipe_summary_bnk", count, paths.ExtractedBanksDir);
        });
    }

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

            // wwiser：最新 release 的 wwiser.pyz（单文件，命令行可直接跑）
            var wwiserPyz = ToolLocator.DownloadWwiserAsync(log, reporter, ct)
                .GetAwaiter().GetResult();
            log(Locale.S("pipe_download_wwiser_ok", wwiserPyz));

            Dispatcher.Invoke(RefreshExtractPaths);
            return Locale.S("pipe_summary_download", ToolLocator.UtilsDir);
        });
    }

    private void BtnGenerateTxtp_Click(object sender, RoutedEventArgs e)
    {
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

        // wwiser.pyz 是 Python zipapp，需要解释器；不做 UI 配置，直接在 PATH 上找
        var python = ToolLocator.FindPython();
        if (python == null)
        {
            MessageBox.Show(this, Locale.S("dlg_python_missing"),
                Locale.S("dlg_vgmstream_missing_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!EnsureProjectRoot()) return;
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

    private void PlayOwnerTxtpButton_Click(object sender, RoutedEventArgs e) => PlayOwningTxtp();

    /// <summary>
    /// 找出引用了当前选中 WEM 的 txtp 并直接作为音频播放，
    /// 无需手动把 txtp 拖进窗口（拖入与「打开txtp预览」仍然可用）。
    /// </summary>
    private void PlayOwningTxtp()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;

        var entry = _entries[_currentIndex];
        if (!long.TryParse(entry.WemID, out var wemId))
        {
            SetStatus(Locale.S("status_owner_txtp_no_id", entry.WemID));
            return;
        }

        var paths = EnsurePaths();
        var repo = EnsureTxtpRepository(paths);
        if (repo == null) return;

        var candidates = new List<string>();

        // 1) 优先用 CSV 里已经算好的 TxtpFiles 列（来自 link_unused_wem.py）
        foreach (var name in entry.TxtpFileList)
        {
            var full = repo.ResolveTxtpPath(name);
            if (full != null && !candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                candidates.Add(full);
        }

        // 2) 反向索引：所有引用了该 WemID 的 txtp
        if (candidates.Count == 0 && repo.HasTxtpIndex)
            candidates.AddRange(repo.FindTxtpFilesForWem(wemId));

        // 3) 索引还没建过，先建一次（约 1 秒 / 1 万个文件）
        if (candidates.Count == 0 && !repo.HasTxtpIndex)
        {
            SetBusy(true);
            SetStatus(Locale.S("status_txtp_indexing"));
            try
            {
                repo.BuildTxtpIndex(msg => VgmLog(msg), CancellationToken.None);
                candidates.AddRange(repo.FindTxtpFilesForWem(wemId));
            }
            catch (Exception ex)
            {
                VgmLog($"[owner-txtp] 建立 txtp 索引失败: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        if (candidates.Count == 0)
        {
            SetStatus(Locale.S("status_owner_txtp_none", entry.WemID));
            return;
        }

        string? chosen;
        if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            var choices = candidates
                .Select(p => new TxtpChoice(p, Path.GetFileName(p)))
                .ToList();
            var picker = new TxtpPickerWindow(
                Locale.S("dlg_txtp_pick_hint", entry.WemID, choices.Count), choices) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedChoice == null) return;
            chosen = picker.SelectedChoice.Path;
        }

        SetStatus(Locale.S("status_owner_txtp_found", Path.GetFileName(chosen), candidates.Count));
        VgmLog($"[owner-txtp] wem={entry.WemID} -> {chosen} (候选 {candidates.Count} 个)");
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
