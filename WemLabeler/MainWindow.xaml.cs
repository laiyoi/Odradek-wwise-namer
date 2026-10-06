using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using WemLabeler.Pipeline;

using Rectangle = System.Windows.Shapes.Rectangle;
using Line = System.Windows.Shapes.Line;

namespace WemLabeler;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<WemEntry> _entries = new();
    private int _currentIndex = -1;
    private Process? _vgmstreamProcess;
    private IWavePlayer? _wavePlayer;
    private byte[]? _pcmData;
    private WaveFormat? _pcmFormat;
    private float[] _peakData = [];
    private long _totalSamples;
    private long _samplePosition;
    private DateTime _playbackStartTime;
    private DispatcherTimer? _playbackTimer;
    private bool _suppressLabelEvents;
    private bool _suppressSelectionEvents;
    private CancellationTokenSource? _loadCts;
    private string? _loadedCsvPath;
    private AppConfig _config = new();
    private List<string> _originalHeader = new();
    private string? _sortPropertyName;
    private bool _sortAscending = true;
    private CancellationTokenSource? _durationCts;
    private string? _resolvedTxtpPath;
    private List<string> _resolvedTxtpLines = new();
    private string? _txtpBaseName;
    private int _txtpDecodeGen;
    private byte[]? _previewWavBytes;

    /// <summary>
    /// 当前内存里这段 PCM 是不是「所属 txtp」的预览解码结果。
    /// 用来区分播放器里装的是本条 WEM 还是某个 txtp。
    /// </summary>
    private bool _pcmIsTxtpPreview;

    /// <summary>播放器里当前这段音频的来源名字（用于界面显示）。</summary>
    private string? _pcmSourceName;

    /// <summary>是否有一次解码正在进行（txtp / WEM 都算），供空格键判断「该停还是该播」。</summary>
    private bool _audioDecoding;

    // --- 由 Python 脚本迁移而来的流水线状态 ---
    private PipelinePaths? _paths;
    private TxtpRepository? _txtpRepo;
    private string? _txtpRepoBaseDir;

    private MenuItem _fileMenuItem = null!;
    private MenuItem _openCsvItem = null!;
    private MenuItem _reloadCsvItem = null!;
    private MenuItem _openWemFolderItem = null!;
    private MenuItem _openTxtpItem = null!;
    private MenuItem _exportCsvItem = null!;
    private MenuItem _exportWavItem = null!;
    private MenuItem _exportTxtpItem = null!;
    private MenuItem _importLabelsItem = null!;
    private MenuItem _vgmstreamItem = null!;
    private MenuItem _exitItem = null!;
    private MenuItem _helpMenuItem = null!;
    private MenuItem _aboutItem = null!;
    private MenuItem _openLogDirItem = null!;
    private MenuItem _envDiagItem = null!;
    private MenuItem _langMenuItem = null!;
    private MenuItem _langZhItem = null!;
    private MenuItem _langEnItem = null!;

    public MainWindow()
    {
        VgmLog("=== MainWindow ctor: begin ===");
        InitializeComponent();
        _config = ConfigManager.Load();

        Locale.SetLanguage(_config.Language);
        Locale.OnLanguageChanged += ApplyLocale;

        FileListView.ItemsSource = _entries;
        AutoPlayCheck.IsChecked = _config.AutoPlay;
        UpdateProgress();
        BuildMenu();
        ApplyLocale();
        _uiReady = true; // 之后标签页切换事件才可以安全地刷新控件
        RefreshExtractPaths();
        VgmLog("=== MainWindow ctor: done ===");
    }

    private void BuildMenu()
    {
        _fileMenuItem = new MenuItem();
        _openCsvItem = new MenuItem { InputGestureText = "Ctrl+O" };
        _openCsvItem.Click += (_, _) => OpenCsv_Click();
        _reloadCsvItem = new MenuItem { InputGestureText = "Ctrl+R" };
        _reloadCsvItem.Click += (_, _) => ReloadCsv_Click();
        _openWemFolderItem = new MenuItem();
        _openWemFolderItem.Click += (_, _) => OpenWemFolder_Click();
        _openTxtpItem = new MenuItem();
        _openTxtpItem.Click += (_, _) => OpenTxtp_Click();
        _exportCsvItem = new MenuItem { InputGestureText = "Ctrl+E" };
        _exportCsvItem.Click += (_, _) => ExportLabels();
        _exportWavItem = new MenuItem { InputGestureText = "Ctrl+W" };
        _exportWavItem.Click += (_, _) => ExportWav();
        _exportTxtpItem = new MenuItem();
        _exportTxtpItem.Click += (_, _) => ExportResolvedTxtp();
        _importLabelsItem = new MenuItem();
        _importLabelsItem.Click += (_, _) => ImportLabelsFromFolder();
        _vgmstreamItem = new MenuItem { InputGestureText = "Ctrl+Shift+S" };
        _vgmstreamItem.Click += (_, _) => SetVgmstream_Click();
        _exitItem = new MenuItem();
        _exitItem.Click += (_, _) => Exit_Click();

        _fileMenuItem.Items.Add(_openCsvItem);
        _fileMenuItem.Items.Add(_reloadCsvItem);
        _fileMenuItem.Items.Add(_openWemFolderItem);
        _fileMenuItem.Items.Add(_openTxtpItem);
        _fileMenuItem.Items.Add(new Separator());
        _fileMenuItem.Items.Add(_exportCsvItem);
        _fileMenuItem.Items.Add(_exportWavItem);
        _fileMenuItem.Items.Add(_exportTxtpItem);
        _fileMenuItem.Items.Add(new Separator());
        _fileMenuItem.Items.Add(_importLabelsItem);
        _fileMenuItem.Items.Add(new Separator());
        _fileMenuItem.Items.Add(_vgmstreamItem);
        _fileMenuItem.Items.Add(new Separator());
        _fileMenuItem.Items.Add(_exitItem);

        _langMenuItem = new MenuItem();
        _langZhItem = new MenuItem();
        _langZhItem.Click += (_, _) => Locale.SetLanguage("zh-CN");
        _langEnItem = new MenuItem();
        _langEnItem.Click += (_, _) => Locale.SetLanguage("en-US");
        _langMenuItem.Items.Add(_langZhItem);
        _langMenuItem.Items.Add(_langEnItem);

        _helpMenuItem = new MenuItem();
        _aboutItem = new MenuItem();
        _aboutItem.Click += (_, _) => About_Click();
        _openLogDirItem = new MenuItem();
        _openLogDirItem.Click += (_, _) => OpenLogDir();
        _envDiagItem = new MenuItem();
        _envDiagItem.Click += (_, _) => RunEnvironmentDiagnostics();

        _helpMenuItem.Items.Add(_langMenuItem);
        _helpMenuItem.Items.Add(new Separator());
        _helpMenuItem.Items.Add(_openLogDirItem);
        _helpMenuItem.Items.Add(_envDiagItem);
        _helpMenuItem.Items.Add(new Separator());
        _helpMenuItem.Items.Add(_aboutItem);

        var menu = (Menu)FindName("MainMenu")!;
        menu.Items.Clear();
        menu.Items.Add(_fileMenuItem);
        menu.Items.Add(_helpMenuItem);
    }

    private void ApplyLocale()
    {
        var L = (Func<string, string>)Locale.S;

        Title = L("title_no_file");

        // 两个并列标签页
        LabelTab.Header = L("tab_label");
        ExtractTab.Header = L("tab_extract");
        PathsGroup.Header = L("gb_paths");
        ActionsGroup.Header = L("gb_actions");
        OutputGroup.Header = L("gb_output");

        PathBaseDirLabel.Text = L("lbl_path_basedir");
        PathOutputDirLabel.Text = L("lbl_path_output");
        PathWemResWemLabel.Text = L("lbl_path_wemreswem");
        PathVgmstreamLabel.Text = L("lbl_path_vgmstream");
        PathGameRootLabel.Text = L("lbl_path_game_root");
        BtnOdradekExport.Content = L("btn_odradek_export");
        BtnWemAudioExport.Content = L("btn_wem_audio_export");

        BtnBrowseBaseDir.Content = L("btn_browse");
        BtnBrowseOutputDir.Content = L("btn_browse");
        BtnBrowseWemResWemDir.Content = L("btn_browse");
        BtnBrowseVgmstream.Content = L("btn_browse");
        BtnFindGameRoot.Content = L("btn_find_game");
        BtnBrowseGameRoot.Content = L("btn_browse");
        BtnOpenBaseDir.Content = L("btn_open");
        BtnOpenOutputDir.Content = L("btn_open");
        BtnOpenWemResWemDir.Content = L("btn_open");
        BtnResetPaths.Content = L("btn_reset_paths");
        BtnGenerateTxtp.Content = L("btn_generate_txtp");
        BtnBuildMapping.Content = L("btn_build_mapping");
        BtnExportAudio.Content = L("btn_export_audio");
        BtnUnusedWem.Content = L("btn_unused_wem");
        BtnRebuildTxtpIndex.Content = L("btn_rebuild_txtp_index");
        BtnDownloadTools.Content = L("btn_download_tools");
        BtnOpenUtilsDir.Content = L("btn_open_utils");

        // 标注音频页顶部工具条
        BtnOpenCsv.Content = L("btn_open_csv");
        BtnReloadCsv.Content = L("btn_reload_csv");
        BtnOpenWemFolder.Content = L("btn_open_wem_folder");
        BtnOpenAudioDir.Content = L("btn_open_audio_dir");
        BtnOpenTxtpPreview.Content = L("btn_open_txtp_preview");
        BtnExportCsv.Content = L("btn_export_csv");
        BtnExportWavAll.Content = L("btn_export_wav_all");
        BtnImportLabels.Content = L("btn_import_labels");
        BtnSetVgmstream.Content = L("btn_set_vgmstream");
        BtnExportById.Content = L("btn_export_by_id");
        EventIdsLabel.Text = L("lbl_event_ids");
        ChkForceWemCache.Content = L("chk_refresh_wem_cache");
        BtnCopyExtractLog.Content = L("btn_copy_log");
        BtnClearExtractLog.Content = L("btn_clear_log");
        BtnCancelJob.Content = L("btn_cancel");

        _fileMenuItem.Header = L("menu_file");
        _openCsvItem.Header = L("menu_open_csv");
        _reloadCsvItem.Header = L("menu_reload_csv");
        _openWemFolderItem.Header = L("menu_open_wem_folder");
        _openTxtpItem.Header = L("menu_open_txtp");
        _exportCsvItem.Header = L("menu_export_csv");
        _exportWavItem.Header = L("menu_export_wav");
        _exportTxtpItem.Header = L("menu_export_txtp");
        _importLabelsItem.Header = L("menu_import_labels");
        _vgmstreamItem.Header = L("menu_vgmstream");
        _exitItem.Header = L("menu_exit");
        _helpMenuItem.Header = L("menu_help");
        _langMenuItem.Header = L("menu_language");
        _langZhItem.Header = L("menu_lang_zh");
        _langEnItem.Header = L("menu_lang_en");
        _aboutItem.Header = L("menu_about");
        _openLogDirItem.Header = L("menu_open_log_dir");
        _envDiagItem.Header = L("menu_env_diag");

        PlayButton.Content = L("btn_play");
        PlayEntryButton.Content = L("btn_play_entry_audio");
        StopButton.Content = L("btn_stop");
        AutoPlayCheck.Content = L("chk_autoplay");
        LabelHint.Text = L("lbl_label");
        PrevButton.Content = L("btn_prev");
        NextButton.Content = L("btn_next");
        ExportWavCoordButton.Content = L("btn_export_wav_coord");
        ExportTracksButton.Content = L("btn_export_tracks");
        FileInfoGroup.Header = L("gb_file_info");
        SourceInfoGroup.Header = L("gb_source_info");

        if (_currentIndex < 0)
        {
            InfoFilename.Text = L("lbl_no_file");
            SaveButton.Content = L("btn_save");
        }
        // 未选择文件时也让「来源关联」显示占位符，而不是整块空白
        RefreshSourceInfo();

        if (_suppressLabelEvents || _currentIndex < 0)
            SaveButton.Content = L("btn_save");
        else
            SaveButton.Content = L("btn_save_unsaved");

        SetStatus(L("status_idle"));
        UpdateProgress();
        UpdatePlayingSourceText();

        if (!string.IsNullOrEmpty(_loadedCsvPath))
            Title = Locale.S("title", Path.GetFileName(_loadedCsvPath), _entries.Count);

        _langZhItem.IsChecked = Locale.Language == "zh-CN";
        _langEnItem.IsChecked = Locale.Language == "en-US";

        // 路径标签上带「(未设置)」后缀，切语言后要重新套一遍
        RefreshExtractPaths();
    }

    #region Window Events

    /// <summary>
    /// 从终端（尤其是最大化的终端）启动时，Windows 不一定会把新窗口提到前台，
    /// 窗口可能完全被终端挡住，看起来就像「双击了没反应 / 打不开」。
    /// 这里强制前置一次。
    /// </summary>
    private void BringToFront()
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            Focus();
        }
        catch (Exception ex)
        {
            VgmLog($"[warn] BringToFront: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VgmLog("=== MainWindow loaded ===");
        // 先把「这台机器/这个会话到底什么环境」记下来，
        // 「外部程序打不开」这类问题只有这些事实能定性。
        LogEnvironmentReport();

        // 关键：首启对话框必须等窗口真正渲染并前置之后再弹。
        // 否则从最大化终端启动时，这条模态链（欢迎框 → 选择 vgmstream 的文件对话框）
        // 会留在终端后面，主窗口被禁用，看起来就像「程序打不开」。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            BringToFront();
            VgmLog("=== foreground requested ===");
            RunStartupPrompts();
        }), DispatcherPriority.ContextIdle);
    }

    /// <summary>首启处理：vgmstream 缺失就自动下载（不弹窗）；必要时提示恢复上次的 CSV。</summary>
    private void RunStartupPrompts()
    {
        // vgmstream 不再弹「是否设置路径」的对话框：找不到就直接后台自动下载到
        // exe 旁边的 utils，状态栏会显示进度，期间照常用其它功能。
        if (ToolLocator.FindVgmstreamCli(_config.VgmstreamPath) == null)
            _ = EnsureVgmstreamAsync();
        else
            RefreshExtractPaths();

        if (!string.IsNullOrEmpty(_config.LastCsvPath) && _entries.Count == 0)
        {
            var answer = MessageBox.Show(this,
                Locale.S("dlg_resume", _config.LastCsvPath),
                Locale.S("dlg_resume_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                LoadCsvAsync(_config.LastCsvPath);
        }

        BringToFront();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            SaveCurrentLabel();
            return;
        }
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            e.Handled = true;
            PlayOwningTxtp();
            return;
        }
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
            case Key.Space:
                // 光标在标注输入框时解放空格键（可以输入空格）
                if (e.Key == Key.Space && LabelTextBox.IsKeyboardFocusWithin)
                    break;
                e.Handled = true;
                Window_KeyDown(sender, e);
                break;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            SaveCurrentLabel();
            return;
        }
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            e.Handled = true;
            PlayOwningTxtp();
            return;
        }
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.Up: e.Handled = true; if (_currentIndex > 0) Navigate(-1); break;
            case Key.Down: e.Handled = true; if (_currentIndex < _entries.Count - 1) Navigate(1); break;
            case Key.Space:
                if (LabelTextBox.IsKeyboardFocusWithin) break;
                e.Handled = true;
                // 正在播/正在解码 → 停；否则继续播放器里那段音频（播放器为空才去解码本条 WEM）
                if (IsPlayingOrDecoding()) StopPlayback();
                else PlayTransport();
                break;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_extractRunning)
        {
            var result = MessageBox.Show(this,
                Locale.S("dlg_job_running"), Locale.S("dlg_unsaved_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
            try { _extractCts?.Cancel(); } catch { }
        }

        SaveCurrentLabel();
        var hasUnsaved = false;
        if (_currentIndex >= 0 && _currentIndex < _entries.Count)
        {
            var currentText = LabelTextBox.Text.Trim();
            var saved = _entries[_currentIndex].Label ?? "";
            if (currentText != saved) hasUnsaved = true;
        }
        if (hasUnsaved)
        {
            var result = MessageBox.Show(this,
                Locale.S("dlg_unsaved"), Locale.S("dlg_unsaved_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _config.AutoPlay = AutoPlayCheck.IsChecked == true;
        ConfigManager.Save(_config);
        StopPlayback();

        try { _extractCts?.Cancel(); } catch { }
        try { _extractFlushTimer?.Stop(); } catch { }
    }

    #endregion

    #region Menu Events

    // ===== 顶部工具条上的按钮（转发到原来的菜单逻辑）=====
    private void BtnOpenCsv_Click(object sender, RoutedEventArgs e) => OpenCsv_Click();
    private void BtnReloadCsv_Click(object sender, RoutedEventArgs e) => ReloadCsv_Click();
    private void BtnOpenWemFolder_Click(object sender, RoutedEventArgs e) => OpenWemFolder_Click();
    private void BtnOpenTxtpPreview_Click(object sender, RoutedEventArgs e) => OpenTxtp_Click();
    private void BtnExportCsv_Click(object sender, RoutedEventArgs e) => ExportLabels();
    private void BtnExportWavAll_Click(object sender, RoutedEventArgs e) => ExportWav();

    private void BtnSetVgmstream_Click(object sender, RoutedEventArgs e)
    {
        SetVgmstreamPath();
        RefreshExtractPaths();
    }

    /// <summary>
    /// 打开「音频目录」：优先打开当前选中 WEM 所在文件夹并选中它；
    /// 没选中就退回 Streaming WEM 目录，再退回已加载 CSV 所在目录。
    /// </summary>
    private void BtnOpenAudioDir_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex >= 0 && _currentIndex < _entries.Count)
        {
            var path = _entries[_currentIndex].Path;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                OpenPathInExplorer(path);
                return;
            }
        }

        var wemDir = EnsurePaths().WemResWemDir;
        if (Directory.Exists(wemDir))
        {
            OpenPathInExplorer(wemDir);
            return;
        }

        if (!string.IsNullOrEmpty(_loadedCsvPath))
        {
            var csvDir = Path.GetDirectoryName(_loadedCsvPath);
            if (!string.IsNullOrEmpty(csvDir) && Directory.Exists(csvDir))
            {
                OpenPathInExplorer(csvDir);
                return;
            }
        }

        SetStatus(Locale.S("status_no_audio_dir"));
    }

    private void OpenCsv_Click()
    {
        var initDir = !string.IsNullOrEmpty(_loadedCsvPath) ? Path.GetDirectoryName(_loadedCsvPath) : AppDomain.CurrentDomain.BaseDirectory;
        var dlg = new OpenFileDialog { Title = Locale.S("dlg_open_csv"), Filter = Locale.S("filter_csv"), InitialDirectory = initDir };
        if (dlg.ShowDialog() == true) LoadCsvAsync(dlg.FileName);
    }

    private void ReloadCsv_Click() { if (!string.IsNullOrEmpty(_loadedCsvPath)) LoadCsvAsync(_loadedCsvPath); }

    private void OpenWemFolder_Click()
    {
        var wemFolder = PickFolder(Locale.S("dlg_open_wem_folder"), EnsurePaths().WemResWemDir);
        if (wemFolder == null) return;

        // WemID / 时长从 ⓪ 写的 wem_index.json（WemID ↔ 对象坐标）反查，不再需要 WemResJson。
        // 列与「分析未使用的 WEM」保持一致：不写 IsStreaming（本作恒为 true，无信息量）。
        var paths = EnsurePaths();
        var coordToWem = new Dictionary<string, (string WemId, double Length)>(StringComparer.Ordinal);
        foreach (var (id, item) in AudioPipeline.LoadWemIndexItems(paths))
            if (!string.IsNullOrEmpty(item.Coord))
                coordToWem[item.Coord] = (id.ToString(), item.LengthSeconds);

        // Pick CSV save location
        var dlg = new SaveFileDialog
        {
            Title = Locale.S("dlg_save_csv_for_wem"),
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"wem_files_{DateTime.Now:yyyyMMddHHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        var csvPath = dlg.FileName;
        SetStatus(Locale.S("status_scanning_folder"));
        _ = Task.Run(() =>
        {
            try
            {
                var files = Directory.GetFiles(wemFolder, "*.wem", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Dispatcher.Invoke(() => SetStatus(Locale.S("status_folder_empty")));
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
                using var writer = new StreamWriter(csvPath, false, Encoding.UTF8);
                writer.WriteLine("WemID,Coord,JsonFile,WemSize,WemFile,WemPath,FoundInBankRes,TxtpFiles,Label,Duration,Channel");
                foreach (var wemPath in files)
                {
                    var fi = new FileInfo(wemPath);
                    var name = Path.GetFileNameWithoutExtension(wemPath);
                    // Try to parse WwiseWemResource_{x}_{y} pattern
                    var m = System.Text.RegularExpressions.Regex.Match(name,
                        @"WwiseWemResource_(\d+)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    string wemId, coord, jsonFile, wemFile;
                    double duration = -1;
                    if (m.Success)
                    {
                        coord = $"{m.Groups[1].Value}:{m.Groups[2].Value}";
                        jsonFile = $"WwiseWemResource_{m.Groups[1].Value}_{m.Groups[2].Value}.json";
                        wemFile = $"{name}.wem";
                        // WemID / 时长来自 wem_index.json（没有就跑一次 ⓪），查不到时 WemID 退回坐标数字
                        if (coordToWem.TryGetValue(coord, out var info))
                        {
                            wemId = info.WemId;
                            if (info.Length > 0) duration = info.Length;
                        }
                        else
                        {
                            wemId = coord.Replace(":", "");
                        }
                    }
                    else
                    {
                        // Fallback: use file hash; no WemID source for non-standard names
                        wemId = Math.Abs(wemPath.GetHashCode()).ToString();
                        coord = "";
                        jsonFile = $"{name}.json";
                        wemFile = $"{name}.wem";
                    }
                    var size = fi.Length.ToString();
                    var durStr = duration >= 0 ? (duration >= 3600
                        ? $"{(int)(duration / 3600)}:{(int)(duration % 3600 / 60):D2}:{(int)(duration % 60):D2}.{(int)(duration * 1000 % 1000):D3}"
                        : $"{(int)(duration / 60)}:{(int)(duration % 60):D2}.{(int)(duration * 1000 % 1000):D3}") : "";
                    // 列序与表头一致：WemID,Coord,JsonFile,WemSize,WemFile,WemPath,FoundInBankRes,TxtpFiles,Label,Duration,Channel
                    writer.WriteLine($"{EscapeCsv(wemId)},{EscapeCsv(coord)},{EscapeCsv(jsonFile)},{EscapeCsv(size)},{EscapeCsv(wemFile)},{EscapeCsv(wemPath)},,,,{EscapeCsv(durStr)},");
                }
                Dispatcher.Invoke(() =>
                {
                    SetStatus(Locale.S("status_folder_scanned", files.Length));
                    LoadCsvAsync(csvPath);
                });
            }
            catch (Exception ex)
            {
                VgmLog($"[OpenWemFolder] error: {ex.Message}");
                Dispatcher.Invoke(() => SetStatus(Locale.S("status_load_fail", ex.Message)));
            }
        });
    }

    private void SetVgmstream_Click() => SetVgmstreamPath();
    private void Exit_Click() => Close();

    private void About_Click()
    {
        MessageBox.Show(this, Locale.S("about_text"), Locale.S("about_title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #region Search

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySearchFilter();
        SearchClearButton.IsEnabled = SearchBox.Text.Length > 0;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; FileListView.Focus(); e.Handled = true; }
        if (e.Key == Key.Enter) { FileListView.Focus(); e.Handled = true; }
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void ApplySearchFilter()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        var text = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = obj => obj is WemEntry entry &&
                (entry.WemID.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                 entry.Filename.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                 (entry.Label?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        // Auto-select first visible result
        SelectFirstSearchResult();
    }

    private void SelectFirstSearchResult()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        foreach (WemEntry entry in view)
        {
            SelectEntry(_entries.IndexOf(entry));
            return;
        }
    }

    #endregion

    #region Txtp Preview

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0 && files[0].EndsWith(".txtp", StringComparison.OrdinalIgnoreCase))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0 && files[0].EndsWith(".txtp", StringComparison.OrdinalIgnoreCase))
                OpenTxtpFile(files[0]);
        }
        e.Handled = true;
    }

    private void OpenTxtp_Click() => OpenTxtpFile(null);

    private void OpenTxtpFile(string? path)
    {
        if (_entries.Count == 0)
        {
            SetStatus(Locale.S("status_txtp_no_csv"));
            return;
        }
        if (path == null)
        {
            var initDir = !string.IsNullOrEmpty(_loadedCsvPath)
                ? Path.GetDirectoryName(_loadedCsvPath)
                : AppDomain.CurrentDomain.BaseDirectory;
            var dlg = new OpenFileDialog
            {
                Title = Locale.S("dlg_open_txtp"),
                Filter = Locale.S("filter_txtp"),
                InitialDirectory = initDir
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        SetStatus(Locale.S("status_txtp_resolving"));
        var txtpPath = path;

        // 在 UI 线程上先把解析所需的查找表准备好，后台线程只做纯 IO 解析
        var csvLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in _entries)
            if (!string.IsNullOrEmpty(e.WemID) && !string.IsNullOrEmpty(e.Path))
                csvLookup[e.WemID] = e.Path;
        var repoForResolve = TryEnsureTxtpRepositoryQuiet();

        _ = Task.Run(() =>
        {
            try
            {
                var txtpLines = File.ReadAllLines(txtpPath);
                var resolvedLines = new List<string>();
                var dir = Path.GetDirectoryName(txtpPath) ?? "";
                foreach (var line in txtpLines)
                {
                    // Preserve leading whitespace
                    int leadingLen = 0;
                    while (leadingLen < line.Length && char.IsWhiteSpace(line[leadingLen])) leadingLen++;
                    var leading = line[..leadingLen];
                    var trimmed = line[leadingLen..];
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    {
                        resolvedLines.Add(line);
                        continue;
                    }
                    // Extract first token (the path) from the trimmed line
                    var firstSpace = trimmed.IndexOf(' ');
                    var pathToken = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
                    var suffix = firstSpace > 0 ? trimmed[firstSpace..] : "";
                    pathToken = pathToken.Trim('"');
                    // Extract numeric WemID from path (e.g. "wem/267537974.wem" → "267537974")
                    var refFile = Path.GetFileName(pathToken);
                    var numId = Path.GetFileNameWithoutExtension(refFile);
                    string? resolvedWemPath = null;
                    if (!string.IsNullOrEmpty(numId) && numId.All(char.IsAsciiDigit))
                        resolvedWemPath = ResolveWemAudioPath(numId, csvLookup, repoForResolve);
                    if (resolvedWemPath != null)
                    {
                        resolvedLines.Add($"{leading}{resolvedWemPath}{suffix}");
                    }
                    else
                    {
                        // Try to resolve relative to txtp location
                        var candidate = Path.Combine(dir, pathToken);
                        resolvedLines.Add(File.Exists(candidate) ? $"{leading}{candidate}{suffix}" : line);
                    }
                }
                var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
                Directory.CreateDirectory(tempDir);
                var outPath = Path.Combine(tempDir, $"resolved_{Guid.NewGuid():N}.txtp");
                File.WriteAllLines(outPath, resolvedLines, Encoding.UTF8);
                var fname = Path.GetFileName(txtpPath);
                var resolvedLinesCopy = resolvedLines;
                var baseName = Path.GetFileNameWithoutExtension(fname);
                Dispatcher.Invoke(() =>
                {
                    _resolvedTxtpPath = outPath;
                    _resolvedTxtpLines = resolvedLinesCopy;
                    _txtpBaseName = baseName;
                    ExportTracksButton.IsEnabled = true;
                    SetStatus(Locale.S("status_txtp_preview", fname));
                    PlayTxtpResolved();
                });
            }
            catch (Exception ex)
            {
                VgmLog($"[OpenTxtp] error: {ex.Message}");
                Dispatcher.Invoke(() => SetStatus(Locale.S("status_load_fail", ex.Message)));
            }
        });
    }

    /// <summary>把当前 txtp 的名字显示成播放器里的来源名。</summary>
    private string TxtpSourceName()
    {
        if (!string.IsNullOrEmpty(_txtpBaseName)) return _txtpBaseName!;
        return string.IsNullOrEmpty(_resolvedTxtpPath)
            ? "txtp"
            : Path.GetFileNameWithoutExtension(_resolvedTxtpPath);
    }

    private void PlayTxtpResolved()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;
        if (string.IsNullOrEmpty(_resolvedTxtpPath) || !File.Exists(_resolvedTxtpPath)) return;
        var vgmPath = _config.VgmstreamPath;
        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath)) return;
        StopPlayback();
        try
        {
            _audioDecoding = true;
            var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
            Directory.CreateDirectory(tempDir);
            StatusProgress.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ShowDecoding(TxtpSourceName());
            VgmLog("========== txtp decode start ==========");
            VgmLog($"input: {_resolvedTxtpPath}");
            var txtpPath = _resolvedTxtpPath;
            var txtpLines = _resolvedTxtpLines;
            int gen = ++_txtpDecodeGen;
            _ = Task.Run(async () =>
            {
                try
                {
                    var finalBytes = await DecodeTxtpToWav(txtpPath);
                    // 解码为全静音时,自动去掉时间/自动化参数(#E #B #b #r #m 等)重新解码预览
                    if (finalBytes.Length > 44 && IsWavSilent(finalBytes))
                    {
                        VgmLog("[warn] txtp decode is silent, retrying without time params...");
                        var rawTxtpPath = Path.Combine(tempDir, $"raw_{Guid.NewGuid():N}.txtp");
                        File.WriteAllText(rawTxtpPath, BuildRawTxtp(txtpLines), Encoding.UTF8);
                        var rawBytes = await DecodeTxtpToWav(rawTxtpPath);
                        try { File.Delete(rawTxtpPath); } catch { }
                        if (rawBytes.Length > 44 && !IsWavSilent(rawBytes))
                        {
                            VgmLog("[warn] fallback succeeded, playing raw decode");
                            var raw = rawBytes;
                            Dispatcher.Invoke(() =>
                            {
                                if (gen != _txtpDecodeGen) return;
                                _previewWavBytes = raw;
                                SetStatus(Locale.S("status_txtp_silent_fallback"));
                                LoadAndPlay(raw, true, TxtpSourceName());
                            });
                            return;
                        }
                    }
                    Dispatcher.Invoke(() =>
                    {
                        if (gen != _txtpDecodeGen) return;
                        if (finalBytes.Length > 44)
                        {
                            _previewWavBytes = finalBytes;
                            VgmLog("txtp decode success, playing");
                            LoadAndPlay(finalBytes, true, TxtpSourceName());
                        }
                        else
                        {
                            VgmLog($"[error] txtp decode failed or empty");
                            CleanupPlayback(false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    VgmLog($"[exception] txtp decode: {ex}");
                    Dispatcher.Invoke(() => CleanupPlayback(false));
                }
            });
        }
        catch (Exception ex)
        {
            VgmLog($"[exception] PlayTxtpResolved: {ex}");
            CleanupPlayback(false);
        }
    }

    // 用 vgmstream 将 txtp 解码为 WAV 字节;失败或文件过小时返回空数组
    private async Task<byte[]> DecodeTxtpToWav(string txtpPath)
    {
        var vgmPath = _config.VgmstreamPath;
        var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
        var tempWav = Path.Combine(tempDir, $"txtp_preview_{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vgmPath,
                Arguments = $"-o \"{tempWav}\" -i \"{txtpPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return [];
            var stdOut = proc.StandardOutput.ReadToEndAsync();
            var stdErr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var outText = await stdOut;
            var errText = await stdErr;
            if (!string.IsNullOrWhiteSpace(outText)) VgmLog(outText);
            if (!string.IsNullOrWhiteSpace(errText)) VgmLog(errText);
            byte[] wavBytes = [];
            if (File.Exists(tempWav))
            {
                var fi = new FileInfo(tempWav);
                if (fi.Length > 44) wavBytes = File.ReadAllBytes(tempWav);
            }
            return wavBytes;
        }
        catch (Exception ex)
        {
            VgmLog($"[error] DecodeTxtpToWav: {ex.Message}");
            return [];
        }
        finally
        {
            try { File.Delete(tempWav); } catch { }
        }
    }

    // 判断 WAV 是否全静音(峰值幅度极低)
    private static bool IsWavSilent(byte[] wavBytes)
    {
        try
        {
            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);
            var fmt = reader.WaveFormat;
            int bytesPerSample = fmt.BitsPerSample / 8;
            if (bytesPerSample <= 0) return true;
            var buf = new byte[fmt.BlockAlign * 4096];
            long maxAbs = 0;
            long samples = 0;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int count = read / bytesPerSample;
                for (int i = 0; i < count; i++)
                {
                    int off = i * bytesPerSample;
                    if (off + bytesPerSample > read) break;
                    long v;
                    if (bytesPerSample == 2) v = Math.Abs((int)(short)(buf[off] | (buf[off + 1] << 8)));
                    else if (bytesPerSample == 1) v = Math.Abs((int)buf[off] - 128) * 256L;
                    else v = Math.Abs(BitConverter.ToInt32(buf, off)) / 65536L;
                    if (v > maxAbs) maxAbs = v;
                    samples++;
                }
            }
            VgmLog($"[silence-check] samples={samples}, maxAbs={maxAbs}, silent={samples == 0 || maxAbs < 128}");
            return samples == 0 || maxAbs < 128;
        }
        catch (Exception ex)
        {
            VgmLog($"[error] IsWavSilent: {ex.Message}");
            return false;
        }
    }

    // 生成去掉时间/自动化参数后的原始 txtp(仅保留路径和 group 行),用于静音回退预览
    private static string BuildRawTxtp(List<string> resolvedLines)
    {
        var sb = new StringBuilder();
        foreach (var line in resolvedLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                sb.AppendLine(line);
                continue;
            }
            if (trimmed.StartsWith("group", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(line);
                continue;
            }
            // 音频行:去掉路径后的所有 # 参数,保留行首缩进
            int leadingLen = 0;
            while (leadingLen < line.Length && char.IsWhiteSpace(line[leadingLen])) leadingLen++;
            var leading = line[..leadingLen];
            var pathToken = line[leadingLen..];
            var firstSpace = pathToken.IndexOf(' ');
            if (firstSpace > 0) pathToken = pathToken[..firstSpace];
            sb.AppendLine($"{leading}{pathToken}");
        }
        return sb.ToString();
    }

    private void ExportResolvedTxtp()
    {
        if (string.IsNullOrEmpty(_resolvedTxtpPath) || !File.Exists(_resolvedTxtpPath))
        {
            SetStatus(Locale.S("status_txtp_no_resolved"));
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = Locale.S("dlg_export_txtp"),
            Filter = Locale.S("filter_txtp"),
            FileName = "resolved.txtp"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.Copy(_resolvedTxtpPath, dlg.FileName, true);
            SetStatus(Locale.S("status_txtp_exported", Path.GetFileName(dlg.FileName)));
        }
        catch (Exception ex)
        {
            SetStatus(Locale.S("status_export_fail", ex.Message));
        }
    }

    private async void ExportTxtpTracks()
    {
        if (_resolvedTxtpLines.Count == 0)
        {
            SetStatus(Locale.S("status_txtp_no_resolved"));
            return;
        }
        var vgmPath = await EnsureVgmstreamAsync();
        if (vgmPath == null) return;

        // 收集可导出的音频行（跳过空行、注释行和 group 行）
        var sourceLines = new List<string>();
        foreach (var line in _resolvedTxtpLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith("group", StringComparison.OrdinalIgnoreCase)) continue;
            sourceLines.Add(line);
        }
        if (sourceLines.Count == 0)
        {
            SetStatus(Locale.S("status_txtp_no_sources"));
            return;
        }

        var outDir = PickFolder(Locale.S("dlg_export_txtp_tracks_choose"), EnsurePaths().OutputDir);
        if (outDir == null) return;

        var baseName = SanitizeFileName(string.IsNullOrEmpty(_txtpBaseName) ? "txtp" : _txtpBaseName);
        var lines = sourceLines;
        var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
        Directory.CreateDirectory(tempDir);

        SetBusy(true);
        int success = 0, failed = 0;
        _ = Task.Run(() =>
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var singleTxtp = Path.Combine(tempDir, $"single_{Guid.NewGuid():N}.txtp");
                File.WriteAllText(singleTxtp, lines[i] + Environment.NewLine, Encoding.UTF8);
                var outName = lines.Count > 1 ? $"{baseName}_{i:00}.wav" : $"{baseName}.wav";
                var outPath = Path.Combine(outDir, outName);
                if (File.Exists(outPath))
                {
                    failed++;
                    VgmLog($"[export track] SKIP exists: {outName}");
                    try { File.Delete(singleTxtp); } catch { }
                    continue;
                }
                Dispatcher.Invoke(() => SetStatus(Locale.S("status_export_txtp_tracks", i + 1, lines.Count)));
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vgmPath,
                        Arguments = $"-o \"{outPath}\" -i \"{singleTxtp}\"",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();
                    if (proc?.ExitCode == 0 && File.Exists(outPath))
                    {
                        success++;
                        VgmLog($"[export track] OK: {outName}");
                    }
                    else
                    {
                        failed++;
                        VgmLog($"[export track] FAIL: exit={proc?.ExitCode}, line={lines[i].Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    VgmLog($"[export track] EX: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(singleTxtp); } catch { }
                }
            }
            Dispatcher.Invoke(() =>
            {
                SetBusy(false);
                ExportTracksButton.IsEnabled = true;
                SetStatus(Locale.S("status_export_wav_done", success, failed));
                MessageBox.Show(this,
                    Locale.S("dlg_export_txtp_tracks_ok", outDir, success, failed),
                    Locale.S("dlg_export_wav_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    #endregion

    #endregion

    #region CSV Loading

    private void LoadCsvAsync(string csvPath)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        _previewWavBytes = null;
        _resolvedTxtpPath = null;
        _resolvedTxtpLines.Clear();
        _txtpBaseName = null;
        _txtpDecodeGen++;
        var path = csvPath;

        SetBusy(true);
        SetStatus(Locale.S("status_loading"));
        _entries.Clear();
        _currentIndex = -1;
        ResetDetail();

        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetBusy(false);
                        SetStatus(Locale.S("status_file_not_found", path));
                        MessageBox.Show(this, Locale.S("dlg_file_not_found", path), Locale.S("dlg_error_title"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }
                var lines = File.ReadAllLines(path);
                if (token.IsCancellationRequested) return;
                if (lines.Length == 0)
                {
                    Dispatcher.Invoke(() => { SetBusy(false); SetStatus(Locale.S("status_csv_empty")); });
                    return;
                }
                var header = lines[0];
                var colMap = ParseHeader(header);
                var hasLabel = header.Contains("Label", StringComparison.OrdinalIgnoreCase);
                var tempEntries = new List<WemEntry>();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = ParseCsvLine(line);
                    var entry = new WemEntry();
                    TrySet(colMap, parts, "wemid", v => entry.WemID = v);
                    TrySet(colMap, parts, "coord", v => entry.Coord = v);
                    TrySet(colMap, parts, "jsonfile", v => entry.JsonFile = v);
                    TrySet(colMap, parts, "isstreaming", v => entry.IsStreaming = v);
                    TrySet(colMap, parts, "wemsize", v => entry.WemSize = v);
                    TrySet(colMap, parts, "wemfile", v => entry.Filename = v);
                    TrySet(colMap, parts, "wempath", v => entry.Path = v);
                    TrySet(colMap, parts, "foundinbanks", v => entry.FoundInBanks = v);
                    TrySet(colMap, parts, "foundinbankres", v => entry.FoundInBanks = v);
                    TrySet(colMap, parts, "bankcount", v => entry.BankCount = v);
                    TrySet(colMap, parts, "banks", v => entry.Banks = v);
                    TrySet(colMap, parts, "txtpfiles", v => entry.TxtpFiles = v);
                    if (hasLabel && colMap.TryGetValue("label", out int lidx) && lidx < parts.Count)
                    {
                        var label = parts[lidx];
                        if (!string.IsNullOrWhiteSpace(label)) entry.Label = label;
                    }
                    // Read duration from CSV if available (format: M:SS.FFF or H:MM:SS.FFF)
                    if (colMap.TryGetValue("duration", out int didx) && didx < parts.Count)
                    {
                        var durStr = parts[didx];
                        if (!string.IsNullOrWhiteSpace(durStr) && durStr != "—")
                        {
                            var d = ParseDurationDisplay(durStr);
                            if (d >= 0) entry.DurationSeconds = d;
                        }
                    }
                    // Read channel from CSV if available
                    if (colMap.TryGetValue("channel", out int chIdx) && chIdx < parts.Count)
                    {
                        var chVal = parts[chIdx];
                        if (!string.IsNullOrWhiteSpace(chVal))
                            entry.ChannelConfig = chVal;
                    }
                    // Preserve all column values so unknown columns aren't lost on write-back
                    foreach (var kv in colMap)
                        if (kv.Value < parts.Count)
                            entry.ExtraColumns[kv.Key] = parts[kv.Value];
                    tempEntries.Add(entry);
                }
                if (token.IsCancellationRequested) return;
                Dispatcher.Invoke(() =>
                {
                    foreach (var e in tempEntries) _entries.Add(e);
                    _originalHeader = ParseCsvLine(header);
                    _loadedCsvPath = path;
                    _config.LastCsvPath = path;
                    ConfigManager.Save(_config);
                    SetBusy(false);
                    UpdateProgress();
                    SetStatus(Locale.S("status_loaded", _entries.Count, Path.GetFileName(path)));
                    Title = Locale.S("title", Path.GetFileName(path), _entries.Count);
                    if (_entries.Count > 0) SelectEntry(0);
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        StartFetchingDurations();
                        RefreshExtractPaths();
                    }, DispatcherPriority.ApplicationIdle);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    SetBusy(false);
                    SetStatus(Locale.S("status_load_fail", ex.Message));
                    MessageBox.Show(this, Locale.S("dlg_csv_load_fail", ex.Message), Locale.S("dlg_error_title"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }, token);
    }

    private static void TrySet(Dictionary<string, int> colMap, List<string> parts, string key, Action<string> setter)
    {
        if (colMap.TryGetValue(key, out int idx) && idx < parts.Count) setter(parts[idx]);
    }

    private void ResetDetail()
    {
        InfoFilename.Text = Locale.S("lbl_no_file");
        InfoPath.Text = "";
        InfoWemID.Text = "";
        InfoChannel.Text = "";
        InfoWemRes.Text = Locale.S("lbl_wemres", "—");
        InfoBanks.Text = Locale.S("lbl_banks", "—");
        InfoTxtp.Text = Locale.S("lbl_txtp", "—");
        InfoWwiseID.Text = "";
        LabelTextBox.Text = "";
        LabelTextBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        ExportWavCoordButton.IsEnabled = false;
        ExportTracksButton.IsEnabled = false;
        PlayEntryButton.IsEnabled = false;
        SaveButton.Content = Locale.S("btn_save");
        ClearWaveform();
    }

    private void ClearWaveform()
    {
        WaveformCanvas.Children.Clear();
        WaveformTimeLabel.Text = "";
        _pcmData = null;
        _pcmFormat = null;
        _peakData = [];
        _totalSamples = 0;
        _samplePosition = 0;
        _pcmIsTxtpPreview = false;
        _pcmSourceName = null;
        UpdatePlayingSourceText();
    }

    /// <summary>刷新「播放器里装的是什么」那一行文字。</summary>
    private void UpdatePlayingSourceText()
    {
        if (PlayingSourceText == null) return;

        if (_pcmData == null || _pcmFormat == null || _totalSamples == 0)
        {
            PlayingSourceText.Text = Locale.S("lbl_player_empty");
            return;
        }

        var name = _pcmSourceName ?? "—";
        PlayingSourceText.Text = _pcmIsTxtpPreview
            ? Locale.S("lbl_player_txtp", name)
            : Locale.S("lbl_player_wem", name);
    }

    private static Dictionary<string, int> ParseHeader(string header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parts = ParseCsvLine(header);
        for (int i = 0; i < parts.Count; i++) map[parts[i].Trim().ToLowerInvariant()] = i;
        return map;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    #endregion

    #region File List & Navigation

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents) return;
        if (FileListView.SelectedItem is WemEntry selected)
        {
            var idx = _entries.IndexOf(selected);
            if (idx != _currentIndex)
            { StopPlayback(); SaveCurrentLabel(); SelectEntry(idx); }
        }
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayEntryAudio();

    private void FileListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Column.DisplayMemberBinding is not System.Windows.Data.Binding binding) return;

        var propName = binding.Path.Path switch
        {
            "StatusMark" => "HasLabel",
            "DisplayLabel" => "Label",
            "DurationDisplay" => "DurationSeconds",
            _ => binding.Path.Path
        };

        if (_sortPropertyName == propName)
            _sortAscending = !_sortAscending;
        else
        {
            _sortPropertyName = propName;
            _sortAscending = true;
        }

        // Update sort
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                propName, _sortAscending ? System.ComponentModel.ListSortDirection.Ascending
                                          : System.ComponentModel.ListSortDirection.Descending));
        }

        // Update sort arrows on all column headers
        if (FileListView.View is GridView gv)
        {
            foreach (var col in gv.Columns)
            {
                var h = col.Header as string;
                if (h != null)
                {
                    // Strip existing arrow
                    if (h.EndsWith(" ▲")) h = h[..^2];
                    else if (h.EndsWith(" ▼")) h = h[..^2];
                    col.Header = h;
                }
            }
            // Add arrow to clicked column
            var baseHeader = (header.Column.Header as string) ?? "";
            header.Column.Header = baseHeader + (_sortAscending ? " ▲" : " ▼");
        }
    }

    private void SelectEntry(int index)
    {
        if (index < 0 || index >= _entries.Count) return;

        StopWavePlayer();
        ClearWaveform();

        _currentIndex = index;
        _suppressLabelEvents = true;
        var visualIdx = FileListView.Items.IndexOf(_entries[index]);
        if (visualIdx >= 0)
        {
            _suppressSelectionEvents = true;
            FileListView.SelectedIndex = visualIdx;
            FileListView.ScrollIntoView(FileListView.Items[visualIdx]);
            _suppressSelectionEvents = false;
        }

        var entry = _entries[index];
        InfoFilename.Text = entry.Filename;
        InfoPath.Text = entry.Path;
        InfoWemID.Text = Locale.S("lbl_wemid", entry.WemID + (entry.IsStreaming == "true" ? " [S]" : ""));
        InfoChannel.Text = Locale.S("lbl_channel", string.IsNullOrEmpty(entry.ChannelConfig) ? "—" : entry.ChannelConfig);
        // 「来源关联」的 WemRes / Banks / Txtp / 大小 由 RefreshSourceInfo 统一渲染
        RefreshSourceInfo();

        LabelTextBox.Text = entry.Label ?? "";
        LabelTextBox.IsEnabled = true;
        SaveButton.IsEnabled = true;
        SaveButton.Content = Locale.S("btn_save");
        PlayButton.IsEnabled = true;
        PlayEntryButton.IsEnabled = true;
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _entries.Count - 1;
        ExportWavCoordButton.IsEnabled = _entries.Any(e => e.HasLabel);
        _suppressLabelEvents = false;

        if (AutoPlayCheck.IsChecked == true) PlayEntryAudio();
        SetStatus(Locale.S("status_current", entry.Filename, entry.WemID, index + 1, _entries.Count));
    }

    private void Navigate(int delta)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        var items = view.Cast<WemEntry>().ToList();
        var current = _currentIndex >= 0 ? _entries[_currentIndex] : null;
        if (current == null) return;
        var visualIdx = items.IndexOf(current);
        if (visualIdx < 0) return;
        var newVisualIdx = visualIdx + delta;
        if (newVisualIdx < 0 || newVisualIdx >= items.Count) return;
        var newEntry = items[newVisualIdx];
        var realIdx = _entries.IndexOf(newEntry);
        if (realIdx < 0) return;
        StopPlayback(); SaveCurrentLabel(); SelectEntry(realIdx);
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Navigate(1);

    #endregion

    #region Playback

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayTransport();
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void PlayEntryButton_Click(object sender, RoutedEventArgs e) => PlayEntryAudio();

    /// <summary>
    /// 播放器上的「播放/继续」：只操作**播放器里已经装好的那段音频**
    /// （不管是本条 WEM 还是某个 txtp），不会自己换成别的源。
    /// 播放器是空的才去解码当前这条 WEM。
    /// </summary>
    private void PlayTransport()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;

        if (_pcmData != null && _pcmFormat != null && _totalSamples > 0)
        {
            StopWavePlayer();
            // 已经放到末尾了就从头来，别让用户点了没反应
            if (_samplePosition >= _totalSamples) _samplePosition = 0;
            VgmLog($"resuming from sample {_samplePosition}");
            PlayFromMemory(_samplePosition);
            StartPlaybackTimer();
            StatusProgress.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            SetStatus(Locale.S("status_playing"));
            return;
        }

        PlayEntryAudio();
    }

    /// <summary>
    /// 把「当前这一条 WEM 音频」装进播放器播放。
    /// 双击列表 / 自动播放 / 「播放本条 WEM 音频」按钮 / 播放器为空时的「播放」都走这里。
    /// </summary>
    private async void PlayEntryAudio()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;

        StopPlayback();
        var entry = _entries[_currentIndex];
        var wemPath = entry.Path;
        if (!File.Exists(wemPath))
        {
            VgmLog($"[error] file not found: {wemPath}");
            SetStatus(Locale.S("status_file_not_found_short", wemPath));
            return;
        }
        _audioDecoding = true;
        var vgmPath = await EnsureVgmstreamAsync();
        if (vgmPath == null) { _audioDecoding = false; return; }
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"preview_{Guid.NewGuid():N}.wav");

            StatusProgress.Visibility = Visibility.Visible;
            SetStatus(Locale.S("status_decoding", entry.Filename));
            ShowDecoding(entry.Filename);
            PlayButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            VgmLog("========== decode start ==========");
            VgmLog($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            VgmLog($"vgmstream: {vgmPath}");
            VgmLog($"input: {wemPath}");
            VgmLog($"temp: {tempPath}");
            VgmLog($"input exists: {File.Exists(wemPath)}");
            VgmLog($"input size: {new FileInfo(wemPath).Length} bytes");

            var psi = new ProcessStartInfo
            {
                FileName = vgmPath,
                Arguments = $"-o \"{tempPath}\" \"{wemPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            _vgmstreamProcess = Process.Start(psi);
            if (_vgmstreamProcess == null) { VgmLog("[error] Process.Start returned null"); CleanupPlayback(false); return; }

            var stdOut = _vgmstreamProcess.StandardOutput.ReadToEndAsync();
            var stdErr = _vgmstreamProcess.StandardError.ReadToEndAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _vgmstreamProcess.WaitForExitAsync();
                    var outText = await stdOut;
                    var errText = await stdErr;
                    var exitCode = _vgmstreamProcess.ExitCode;

                    VgmLog($"exit code: {exitCode}");
                    if (!string.IsNullOrEmpty(outText)) VgmLog($"[stdout] {outText.Trim()}");
                    if (!string.IsNullOrEmpty(errText)) VgmLog($"[stderr] {errText.Trim()}");

                    _vgmstreamProcess.Dispose();
                    _vgmstreamProcess = null;

                    byte[] wavBytes = [];
                    if (exitCode == 0 && File.Exists(tempPath))
                    {
                        wavBytes = File.ReadAllBytes(tempPath);
                        VgmLog($"WAV read: {wavBytes.Length} bytes, RIFF=0x{wavBytes[0]:X2}{wavBytes[1]:X2}{wavBytes[2]:X2}{wavBytes[3]:X2}");
                    }
                    try { File.Delete(tempPath); } catch { }

                    var finalBytes = wavBytes;
                    Dispatcher.Invoke(() =>
                    {
                        if (exitCode == 0 && finalBytes.Length > 44)
                        {
                            VgmLog("decode success, computing peaks");
                            LoadAndPlay(finalBytes, false, entry.Filename);
                        }
                        else
                        {
                            var detail = !string.IsNullOrEmpty(errText) ? errText.Trim() : $"exitCode={exitCode}";
                            VgmLog($"[error] decode failed: {detail}");
                            SetStatus(Locale.S("status_decode_fail"));
                            var showLog = MessageBox.Show(this,
                                Locale.S("dlg_decode_fail", entry.Filename, exitCode, detail),
                                Locale.S("dlg_decode_fail_title"), MessageBoxButton.YesNo, MessageBoxImage.Error);
                            if (showLog == MessageBoxResult.Yes) OpenLogDir();
                            CleanupPlayback(false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    VgmLog($"[exception] decode: {ex}");
                    Dispatcher.Invoke(() => { SetStatus(Locale.S("status_decode_exception", ex.Message)); CleanupPlayback(false); });
                }
            });
        }
        catch (Exception ex)
        {
            VgmLog($"[exception] PlayEntryAudio: {ex}");
            SetStatus(Locale.S("status_play_exception", ex.Message));
            CleanupPlayback(false);
        }
    }

    /// <summary>
    /// 解码结果装进内存并开始播放，同时记住「播放器里现在装的是哪段音频」。
    /// <paramref name="fromTxtpPreview"/> = true 表示这是「所属 txtp」的预览音频。
    /// </summary>
    private void LoadAndPlay(byte[] wavBytes, bool fromTxtpPreview, string sourceName)
    {
        try
        {
            ClearWaveform();
            _pcmIsTxtpPreview = fromTxtpPreview;
            _pcmSourceName = sourceName;
            if (!fromTxtpPreview) _previewWavBytes = null;
            UpdatePlayingSourceText();
            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);
            var srcFmt = reader.WaveFormat;
            VgmLog($"NAudio raw format: {srcFmt} (rate={srcFmt.SampleRate}, ch={srcFmt.Channels}, bits={srcFmt.BitsPerSample}, enc={srcFmt.Encoding})");

            if (_currentIndex >= 0 && _currentIndex < _entries.Count)
            {
                _entries[_currentIndex].ChannelConfig = WemEntry.GetChannelDisplayName(srcFmt.Channels);
            }

            ISampleProvider sp = reader.ToSampleProvider();

            if (srcFmt.Channels > 2)
            {
                sp = new StereoDownmixProvider(sp);
                VgmLog($"Downmix: {srcFmt.Channels}ch → 2ch");
            }

            var targetCh = Math.Min(srcFmt.Channels, 2);
            var targetFmt = new WaveFormat(srcFmt.SampleRate, 16, targetCh);

            var allData = new List<byte>();
            var floatBuf = new float[targetFmt.SampleRate * targetFmt.Channels];
            var byteBuf = new byte[targetFmt.SampleRate * targetFmt.BlockAlign];
            int samplesRead;
            while ((samplesRead = sp.Read(floatBuf, 0, floatBuf.Length)) > 0)
            {
                int bytesWritten = FloatToPcm16(floatBuf, samplesRead, byteBuf);
                allData.AddRange(byteBuf.AsSpan(0, bytesWritten));
            }

            _pcmData = allData.ToArray();
            _pcmFormat = targetFmt;
            VgmLog($"PCM loaded: {_pcmData.Length} bytes, {targetFmt}");

            reader.Dispose();
            ms.Dispose();

            ComputePeaks();
            DrawWaveform();

            PlayFromMemory(0);
            StartPlaybackTimer();
            StatusProgress.Visibility = Visibility.Collapsed;
            HideDecoding();
            // 播放开始后必须把「播放」按钮放回可用状态：
            // 解码期间它有可能是灰的，而 txtp 预览播放时也会被禁用，
            // 结果就是「播放了 txtp 之后点左边的播放没反应」。
            PlayButton.IsEnabled = _currentIndex >= 0;
            StopButton.IsEnabled = true;
            SetStatus(Locale.S("status_playing"));
        }
        catch (Exception ex)
        {
            VgmLog($"[error] LoadAndPlay failed: {ex}");
            Dispatcher.Invoke(() =>
            {
                SetStatus(Locale.S("status_play_error", ex.Message));
                CleanupPlayback(false);
            });
        }
    }

    private static int FloatToPcm16(float[] source, int sampleCount, byte[] dest)
    {
        int offset = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short val = (short)Math.Clamp(source[i] * 32767f, -32768f, 32767f);
            dest[offset++] = (byte)(val & 0xFF);
            dest[offset++] = (byte)((val >> 8) & 0xFF);
        }
        return offset;
    }

    private sealed class StereoDownmixProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _srcChannels;
        public WaveFormat WaveFormat { get; }

        public StereoDownmixProvider(ISampleProvider source)
        {
            _source = source;
            _srcChannels = source.WaveFormat.Channels;
            WaveFormat = new WaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            var srcBuf = new float[frames * _srcChannels];
            int srcRead = _source.Read(srcBuf, 0, srcBuf.Length);
            int srcFrames = srcRead / _srcChannels;

            float lScale = _srcChannels == 1 ? 1.0f : 0.8f;
            float rScale = _srcChannels == 1 ? 1.0f : 0.8f;

            for (int i = 0; i < srcFrames; i++)
            {
                float fl = srcBuf[i * _srcChannels] * lScale;
                float fr = _srcChannels >= 2 ? srcBuf[i * _srcChannels + 1] * rScale : fl;

                if (_srcChannels >= 3)
                {
                    float c = srcBuf[i * _srcChannels + 2] * 0.5f;
                    fl += c; fr += c;
                }
                for (int ch = 3; ch < _srcChannels; ch++)
                {
                    float v = srcBuf[i * _srcChannels + ch] * 0.3f;
                    fl += v; fr += v;
                }

                buffer[offset + i * 2] = Math.Clamp(fl, -1f, 1f);
                buffer[offset + i * 2 + 1] = Math.Clamp(fr, -1f, 1f);
            }
            return srcFrames * 2;
        }
    }

    private void PlayFromMemory(long startSample)
    {
        if (_pcmData == null || _pcmFormat == null) return;
        StopWavePlayer();

        int blockAlign = _pcmFormat.BlockAlign;
        long byteOffset = (startSample * blockAlign);
        byteOffset = byteOffset / blockAlign * blockAlign;

        var ms = new MemoryStream(_pcmData, (int)byteOffset, _pcmData.Length - (int)byteOffset);
        var stream = new RawSourceWaveStream(ms, _pcmFormat);

        _samplePosition = startSample;
        _playbackStartTime = DateTime.Now - TimeSpan.FromSeconds((double)startSample / _pcmFormat.SampleRate);

        try
        {
            // 使用 WaveOutEvent（系统混音器）而非 WasapiOut，兼容性更好，
            // 避免部分声卡/驱动下 WasapiOut 静默无声的问题
            _wavePlayer = new WaveOutEvent();
            _wavePlayer.PlaybackStopped += (_, _) =>
                Dispatcher.Invoke(() =>
                {
                    VgmLog("[playback] stopped");
                    try { _playbackTimer?.Stop(); } catch { }
                    try { _wavePlayer?.Dispose(); } catch { }
                    _wavePlayer = null;
                    CleanupPlayback(true);
                });
            _wavePlayer.Init(stream);
            _wavePlayer.Play();
            VgmLog($"WaveOutEvent playing from sample {startSample}");
        }
        catch (Exception ex)
        {
            VgmLog($"[error] WaveOutEvent init/play failed: {ex.Message}");
            try { _wavePlayer?.Dispose(); } catch { }
            _wavePlayer = null;
            try { _playbackTimer?.Stop(); } catch { }
            CleanupPlayback(true);
        }
    }

    private void ComputePeaks()
    {
        if (_pcmData == null || _pcmFormat == null) return;

        int bytesPerFrame = _pcmFormat.BlockAlign;
        int bytesPerSample = _pcmFormat.BitsPerSample / 8;
        _totalSamples = _pcmData.Length / bytesPerFrame;

        int numSegments = Math.Min(600, (int)(_totalSamples / 100));
        if (numSegments < 20) numSegments = 20;
        int samplesPerSegment = (int)(_totalSamples / numSegments);
        if (samplesPerSegment < 1) samplesPerSegment = 1;

        _peakData = new float[numSegments];
        for (int seg = 0; seg < numSegments; seg++)
        {
            int startSample = seg * samplesPerSegment;
            int endSample = Math.Min(startSample + samplesPerSegment, (int)_totalSamples);
            long maxabs = 0;
            for (int s = startSample; s < endSample; s++)
            {
                int off = s * bytesPerFrame;
                if (off + bytesPerSample > _pcmData.Length) break;
                long sum = 0;
                for (int ch = 0; ch < _pcmFormat.Channels; ch++)
                {
                    int sampOff = off + ch * bytesPerSample;
                    if (sampOff + bytesPerSample <= _pcmData.Length)
                    {
                        long val;
                        if (bytesPerSample == 2) val = Math.Abs((int)(short)(_pcmData[sampOff] | (_pcmData[sampOff + 1] << 8)));
                        else if (bytesPerSample == 1) val = Math.Abs((int)_pcmData[sampOff] - 128) * 256L;
                        else val = Math.Abs(BitConverter.ToInt32(_pcmData, sampOff)) / 65536L;
                        sum += val;
                    }
                }
                long avg = sum / _pcmFormat.Channels;
                if (avg > maxabs) maxabs = avg;
            }
            _peakData[seg] = Math.Min(1f, maxabs / 32768f);
        }
    }

    private void DrawWaveform()
    {
        WaveformCanvas.Children.Clear();
        if (_peakData.Length == 0) return;
        double w = WaveformCanvas.ActualWidth;
        double h = WaveformCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double barW = w / _peakData.Length;
        double mid = h / 2;

        for (int i = 0; i < _peakData.Length; i++)
        {
            double barH = _peakData[i] * h * 0.85;
            if (barH < 1) barH = 1;

            double bw = Math.Max(1, barW - (barW > 3 ? 1 : 0.3));
            var rect = new Rectangle
            {
                Width = bw,
                Height = barH,
                Fill = new SolidColorBrush(Color.FromRgb(0, 200, 180))
            };
            Canvas.SetLeft(rect, i * barW + (barW - bw) / 2);
            Canvas.SetTop(rect, mid - barH / 2);
            WaveformCanvas.Children.Add(rect);
        }

        var overlay = new Rectangle
        {
            Width = 0,
            Height = h,
            Fill = new SolidColorBrush(Color.FromArgb(70, 0, 255, 180))
        };
        overlay.Tag = "overlay";
        WaveformCanvas.Children.Add(overlay);

        var playhead = new Line
        {
            X1 = 0, Y1 = 0, X2 = 0, Y2 = h,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5
        };
        playhead.Tag = "playhead";
        WaveformCanvas.Children.Add(playhead);
    }

    private void StartPlaybackTimer()
    {
        _playbackTimer?.Stop();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_pcmFormat == null || _totalSamples == 0) return;

        var elapsed = (DateTime.Now - _playbackStartTime).TotalSeconds;
        _samplePosition = (long)(elapsed * _pcmFormat.SampleRate);
        if (_samplePosition > _totalSamples) _samplePosition = _totalSamples;

        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        double w = WaveformCanvas.ActualWidth;
        if (w <= 0 || _totalSamples == 0) return;

        double frac = (double)_samplePosition / _totalSamples;
        double x = frac * w;

        foreach (var child in WaveformCanvas.Children)
        {
            if (child is Rectangle r && r.Tag as string == "overlay")
                r.Width = x;
            else if (child is Line l && l.Tag as string == "playhead")
                l.X1 = l.X2 = x;
        }

        if (_pcmFormat != null)
        {
            var dur = TimeSpan.FromSeconds((double)_totalSamples / _pcmFormat.SampleRate);
            var pos = TimeSpan.FromSeconds((double)_samplePosition / _pcmFormat.SampleRate);
            WaveformTimeLabel.Text = $"{pos:mm\\:ss} / {dur:mm\\:ss}";
        }
    }

    private void WaveformBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_pcmData == null || _pcmFormat == null || _totalSamples == 0) return;
        var pos = e.GetPosition((Border)sender);
        double frac = Math.Clamp(pos.X / ((Border)sender).ActualWidth, 0, 1);
        SeekTo(frac);
    }

    private void SeekTo(double fraction)
    {
        if (_pcmData == null || _pcmFormat == null) return;
        long target = (long)(fraction * _totalSamples);
        bool wasPlaying = _wavePlayer is { PlaybackState: PlaybackState.Playing };
        StopWavePlayer();
        _samplePosition = target;
        UpdatePlayhead();
        if (wasPlaying)
        {
            PlayFromMemory(target);
            StartPlaybackTimer();
        }
    }

    /// <summary>现在是不是「正在出声」或者「正在解码」——空格键据此决定停还是播。</summary>
    private bool IsPlayingOrDecoding() =>
        _audioDecoding ||
        _wavePlayer is { PlaybackState: PlaybackState.Playing } ||
        (_vgmstreamProcess != null && !_vgmstreamProcess.HasExited);

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_peakData.Length > 0) DrawWaveform();
        UpdatePlayhead();
    }

    private void StopWavePlayer()
    {
        try { _playbackTimer?.Stop(); } catch { }
        try
        {
            _wavePlayer?.Stop();
            _wavePlayer?.Dispose();
            _wavePlayer = null;
        }
        catch { }
    }

    private void StopPlayback()
    {
        StopWavePlayer();
        try { if (_vgmstreamProcess != null && !_vgmstreamProcess.HasExited) { _vgmstreamProcess.Kill(); _vgmstreamProcess.Dispose(); _vgmstreamProcess = null; } } catch { }
        CleanupPlayback(true);
    }

    private void CleanupPlayback(bool restoreUi)
    {
        _audioDecoding = false;
        HideDecoding();
        if (restoreUi)
        {
            PlayButton.IsEnabled = _currentIndex >= 0;
            StopButton.IsEnabled = false;
            StatusProgress.Visibility = Visibility.Collapsed;
            UpdatePlayhead();
        }
    }

    #endregion

    #region Logging

    private static readonly object _logLock = new();
    private static void VgmLog(string message)
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        try { Directory.CreateDirectory(logDir); } catch { }
        var logFile = Path.Combine(logDir, $"vgmstream_{DateTime.Now:yyyyMMdd}.log");
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_logLock) { try { File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); } catch { } }
        Debug.WriteLine(line);
    }

    private void OpenLogDir()
    {
        var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        try { Directory.CreateDirectory(logDir); Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{logDir}\"", UseShellExecute = true }); } catch { }
    }

    #endregion

    #region Labeling

    private void LabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressLabelEvents || _currentIndex < 0) return;
        SaveButton.Content = Locale.S("btn_save_unsaved");
    }

    private void LabelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveCurrentLabel(); }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveCurrentLabel();

    private void SaveCurrentLabel()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;
        var entry = _entries[_currentIndex];
        var newLabel = LabelTextBox.Text.Trim();
        entry.Label = string.IsNullOrEmpty(newLabel) ? null : newLabel;
        SaveButton.Content = Locale.S("btn_save");
        UpdateProgress();
        SetStatus(Locale.S("status_saved", entry.Filename));

        AutoSaveCsv();
    }

    private void AutoSaveCsv()
    {
        if (_entries.Count == 0 || string.IsNullOrEmpty(_loadedCsvPath)) return;
        try
        {
            WriteCsvFile(_loadedCsvPath);
            SetStatus(Locale.S("status_csv_autosaved", Path.GetFileName(_loadedCsvPath)));
        }
        catch (Exception ex)
        {
            VgmLog($"[error] AutoSaveCsv: {ex}");
        }
    }

    /// <summary>
    /// 回写 CSV：按原表头逐列写，并补上 Label / Duration / Channel。
    /// 除 <c>IsStreaming</c> 外**不删任何列** —— 表里没见过的列由
    /// <see cref="WemEntry.ExtraColumns"/> 原样带回，不会丢。
    /// （IsStreaming 是本作里恒为 true 的无信息列，明确不要；读取旧文件时仍然兼容。）
    /// </summary>
    private void WriteCsvFile(string path)
    {
        if (_entries.Count == 0) return;

        // 只过滤明确不要的那一列，其余原样保留
        var columns = _originalHeader
            .Where(h => !h.Equals("IsStreaming", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tmpPath = path + ".tmp";
        using (var writer = new StreamWriter(tmpPath, false, Encoding.UTF8))
        {
            var hasLabelInHeader = columns.Any(h =>
                h.Equals("Label", StringComparison.OrdinalIgnoreCase));
            var hasDurationInHeader = columns.Any(h =>
                h.Equals("Duration", StringComparison.OrdinalIgnoreCase));
            var hasChannelInHeader = columns.Any(h =>
                h.Equals("Channel", StringComparison.OrdinalIgnoreCase));

            var headerParts = new List<string>(columns);
            if (!hasLabelInHeader) headerParts.Add("Label");
            if (!hasDurationInHeader) headerParts.Add("Duration");
            if (!hasChannelInHeader) headerParts.Add("Channel");
            writer.WriteLine(string.Join(",", headerParts));

            foreach (var entry in _entries)
            {
                var parts = new List<string>();
                foreach (var col in columns)
                    parts.Add(EscapeCsv(GetColumnValue(entry, col)));
                if (!hasLabelInHeader)
                    parts.Add(EscapeCsv(entry.Label ?? ""));
                if (!hasDurationInHeader)
                    parts.Add(EscapeCsv(entry.DurationDisplay));
                if (!hasChannelInHeader)
                    parts.Add(EscapeCsv(entry.ChannelConfig));
                writer.WriteLine(string.Join(",", parts));
            }
        }
        // Atomic replace: temp → target, preserves original if crash happens mid-write
        try { File.Delete(path); } catch { }
        File.Move(tmpPath, path);
    }

    private void UpdateProgress()
    {
        var labeled = _entries.Count(e => e.HasLabel);
        ProgressLabel.Text = Locale.S("lbl_progress", labeled, _entries.Count, _entries.Count - labeled);
        ProgressBar.Maximum = _entries.Count > 0 ? _entries.Count : 1;
        ProgressBar.Value = labeled;
    }

    #endregion

    #region Export

    private void ExportLabels()
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, Locale.S("dlg_no_data"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var initFile = !string.IsNullOrEmpty(_loadedCsvPath) ? Path.GetFileName(_loadedCsvPath) : "wem_files.csv";
        var dlg = new SaveFileDialog { Title = Locale.S("dlg_export_csv"), Filter = Locale.S("filter_export"), FileName = initFile, InitialDirectory = Path.GetDirectoryName(_loadedCsvPath) ?? AppDomain.CurrentDomain.BaseDirectory };
        if (dlg.ShowDialog() != true) return;
        try
        {
            WriteCsvFile(dlg.FileName);
            var labeled = _entries.Count(e => e.HasLabel);
            SetStatus(Locale.S("status_export", dlg.FileName, _entries.Count, labeled));
            MessageBox.Show(this, Locale.S("dlg_export_ok", dlg.FileName, _entries.Count, labeled, _entries.Count - labeled),
                Locale.S("dlg_export_ok_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus(Locale.S("status_load_fail", ex.Message));
            MessageBox.Show(this, Locale.S("dlg_export_fail", ex.Message), Locale.S("dlg_error_title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #region 从导出的音频文件导入标注

    /// <summary>
    /// 导出的音频文件名形如 <c>688_6985_4208402129_查理登场的过场动画_.wav</c>：
    /// 前两段是对象坐标，第三段是 WemID，后面剩下的是标注文本。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ExportedWavNameWithCoord =
        new(@"^(?<c1>\d+)_(?<c2>\d+)_(?<id>\d+)_(?<label>.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 退路：只有 <c>WemID_标注</c> 的文件名。
    /// 标注必须以非数字开头，免得把 <c>688_6985</c> 这种坐标当成 WemID。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ExportedWavNameIdOnly =
        new(@"^(?<id>\d+)_(?<label>\D.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>解析导出音频的文件名；名字不符合约定或标注为空时返回 false（调用方直接忽略）。</summary>
    private static bool TryParseExportedWavName(string fileNameWithoutExtension,
        out string? wemId, out string? coord, out string? label)
    {
        wemId = null;
        coord = null;
        label = null;

        var m = ExportedWavNameWithCoord.Match(fileNameWithoutExtension);
        if (m.Success)
        {
            wemId = m.Groups["id"].Value;
            coord = $"{m.Groups["c1"].Value}:{m.Groups["c2"].Value}";
            label = CleanImportedLabel(m.Groups["label"].Value);
            return label.Length > 0;
        }

        m = ExportedWavNameIdOnly.Match(fileNameWithoutExtension);
        if (m.Success)
        {
            wemId = m.Groups["id"].Value;
            label = CleanImportedLabel(m.Groups["label"].Value);
            return label.Length > 0;
        }

        return false;
    }

    /// <summary>导出时标注末尾常留一个分隔用的下划线，这里连同首尾空白一起去掉。</summary>
    private static string CleanImportedLabel(string raw) => raw.Trim().Trim('_').Trim();

    private void BtnImportLabels_Click(object sender, RoutedEventArgs e) => ImportLabelsFromFolder();

    /// <summary>
    /// 打开一个文件夹，按导出的音频文件名（坐标_WemID_标注.wav）匹配回 CSV 行并写入标注。
    /// 名字对不上的文件直接忽略，不弹错。
    /// </summary>
    private void ImportLabelsFromFolder()
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, Locale.S("dlg_no_data"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var initDir = !string.IsNullOrEmpty(_loadedCsvPath)
            ? Path.GetDirectoryName(_loadedCsvPath)
            : EnsurePaths().OutputDir;
        var folder = PickFolder(Locale.S("dlg_import_labels_choose"), initDir);
        if (folder == null) return;

        // 匹配表在 UI 线程上先算好，后台线程只做「读文件名 → 查表」的纯 IO
        var byWemId = new Dictionary<string, int>(StringComparer.Ordinal);
        var byCoord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (!string.IsNullOrEmpty(entry.WemID) && !byWemId.ContainsKey(entry.WemID)) byWemId[entry.WemID] = i;
            if (!string.IsNullOrEmpty(entry.Coord) && !byCoord.ContainsKey(entry.Coord)) byCoord[entry.Coord] = i;
        }

        SetBusy(true);
        SetStatus(Locale.S("status_import_labels_scanning", folder));

        _ = Task.Run(() =>
        {
            var matches = new List<(int Index, string Label)>();
            int scanned = 0, ignored = 0;
            string? error = null;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories))
                {
                    scanned++;
                    if (!TryParseExportedWavName(Path.GetFileNameWithoutExtension(file),
                            out var wemId, out var coord, out var label))
                    {
                        ignored++;
                        continue;
                    }

                    var index = -1;
                    if (wemId != null && byWemId.TryGetValue(wemId, out var idIdx)) index = idIdx;
                    else if (coord != null && byCoord.TryGetValue(coord, out var coordIdx)) index = coordIdx;

                    if (index < 0)
                    {
                        ignored++;
                        continue;
                    }
                    matches.Add((index, label!));
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                VgmLog($"[import labels] 扫描失败: {ex}");
            }

            Dispatcher.Invoke(() => ApplyImportedLabels(folder, matches, scanned, ignored, error));
        });
    }

    private void ApplyImportedLabels(string folder, List<(int Index, string Label)> matches,
        int scanned, int ignored, string? error)
    {
        SetBusy(false);

        if (error != null)
        {
            SetStatus(Locale.S("status_import_labels_failed", error));
            MessageBox.Show(this, Locale.S("dlg_import_labels_failed", error), Locale.S("dlg_error_title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        int updated = 0, same = 0, overwritten = 0;
        foreach (var (index, label) in matches)
        {
            if (index < 0 || index >= _entries.Count) continue;
            var entry = _entries[index];
            if (string.Equals(entry.Label, label, StringComparison.Ordinal)) { same++; continue; }
            if (entry.HasLabel) overwritten++;
            entry.Label = label;
            updated++;
        }

        if (updated > 0)
        {
            UpdateProgress();
            RefreshCurrentLabelUi();
            if (!string.IsNullOrEmpty(_loadedCsvPath))
            {
                try { WriteCsvFile(_loadedCsvPath); }
                catch (Exception ex) { VgmLog($"[import labels] 写回 CSV 失败: {ex.Message}"); }
            }
        }

        SetStatus(Locale.S("status_import_labels_done", updated, ignored));
        MessageBox.Show(this,
            Locale.S("dlg_import_labels_ok", folder, scanned, updated, same, ignored, overwritten),
            Locale.S("dlg_import_labels_title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>导入后把当前选中行的标注框同步成导入结果。</summary>
    private void RefreshCurrentLabelUi()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;
        _suppressLabelEvents = true;
        try
        {
            LabelTextBox.Text = _entries[_currentIndex].Label ?? "";
            SaveButton.Content = Locale.S("btn_save");
        }
        finally
        {
            _suppressLabelEvents = false;
        }
    }

    #endregion

    private async void ExportWav()
    {
        var labeled = _entries.Where(e => e.HasLabel).ToList();
        if (labeled.Count == 0)
        {
            MessageBox.Show(this, Locale.S("dlg_export_wav_none"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vgmPath = await EnsureVgmstreamAsync();
        if (vgmPath == null) return;
        var outDir = PickFolder(Locale.S("dlg_export_wav_choose"), EnsurePaths().OutputDir);
        if (outDir == null) return;

        SetBusy(true);
        int success = 0, failed = 0;
        _ = Task.Run(() =>
        {
            for (int i = 0; i < labeled.Count; i++)
            {
                var entry = labeled[i];
                var wemPath = entry.Path;
                if (!File.Exists(wemPath))
                {
                    failed++;
                    VgmLog($"[export skip] file not found: {wemPath}");
                    continue;
                }
                var safeLabel = SanitizeFileName(entry.Label ?? entry.Filename);
                var outPath = Path.Combine(outDir, safeLabel + ".wav");
                if (File.Exists(outPath))
                {
                    outPath = Path.Combine(outDir, safeLabel + $"_{entry.WemID}.wav");
                }
                Dispatcher.Invoke(() => SetStatus(Locale.S("status_export_wav", i + 1, labeled.Count)));
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vgmPath,
                        Arguments = $"-o \"{outPath}\" \"{wemPath}\"",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();
                    if (proc?.ExitCode == 0 && File.Exists(outPath))
                    {
                        success++;
                        VgmLog($"[export WAV] OK: {outPath}");
                    }
                    else
                    {
                        failed++;
                        VgmLog($"[export WAV] FAIL: exit={proc?.ExitCode}, wem={wemPath}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    VgmLog($"[export WAV] EX: {ex.Message}");
                }
            }
            Dispatcher.Invoke(() =>
            {
                SetBusy(false);
                SetStatus(Locale.S("status_export_wav_done", success, failed));
                MessageBox.Show(this,
                    Locale.S("dlg_export_wav_ok", outDir, success, failed),
                    Locale.S("dlg_export_wav_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    private void ExportWavCoordButton_Click(object sender, RoutedEventArgs e) => ExportWavCoord();
    private void ExportTracksButton_Click(object sender, RoutedEventArgs e) => ExportTxtpTracks();

    private async void ExportWavCoord()
    {
        // If txtp preview WAV is available, export that instead
        if (_previewWavBytes != null)
        {
            var dlg = new SaveFileDialog
            {
                Title = Locale.S("dlg_export_txtp_wav"),
                Filter = "WAV file (*.wav)|*.wav",
                FileName = "preview.wav"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, _previewWavBytes);
                SetStatus(Locale.S("status_txtp_wav_exported", Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                SetStatus(Locale.S("status_export_fail", ex.Message));
            }
            return;
        }

        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;
        var entry = _entries[_currentIndex];
        if (!entry.HasLabel)
        {
            MessageBox.Show(this, Locale.S("dlg_export_wav_none"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vgmPath = await EnsureVgmstreamAsync();
        if (vgmPath == null) return;
        var wemPath = entry.Path;
        if (!File.Exists(wemPath))
        {
            SetStatus(Locale.S("status_file_not_found_short", wemPath));
            return;
        }
        var coordParts = (entry.Coord ?? "").Split(':');
        var prefix = coordParts.Length == 2 ? $"{coordParts[0]}_{coordParts[1]}" : "unknown";
        var safeLabel = SanitizeFileName(entry.Label ?? entry.Filename);
        var outName = $"{prefix}_{entry.WemID}_{safeLabel}.wav";
        var outDir = PickFolder(Locale.S("dlg_export_wav_choose"), EnsurePaths().OutputDir);
        if (outDir == null) return;
        var outPath = Path.Combine(outDir, outName);
        if (File.Exists(outPath))
        {
            var r = MessageBox.Show(this,
                Locale.S("dlg_export_overwrite", outName),
                Locale.S("dlg_export_overwrite_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }

        SetBusy(true);
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vgmPath,
                    Arguments = $"-o \"{outPath}\" \"{wemPath}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                Dispatcher.Invoke(() =>
                {
                    SetBusy(false);
                    if (proc?.ExitCode == 0 && File.Exists(outPath))
                    {
                        SetStatus(Locale.S("status_export_wav_done_single", outName));
                        VgmLog($"[export WAV] OK: {outPath}");
                    }
                    else
                    {
                        SetStatus(Locale.S("status_export_wav_fail"));
                        VgmLog($"[export WAV] FAIL: exit={proc?.ExitCode}, wem={wemPath}");
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => { SetBusy(false); SetStatus(Locale.S("status_export_wav_fail")); });
                VgmLog($"[export WAV] EX: {ex.Message}");
            }
        });
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        if (result.Length > 200) result = result[..200];
        return string.IsNullOrWhiteSpace(result) ? "unnamed" : result;
    }

    private static double ParseDurationDisplay(string s)
    {
        // Try H:MM:SS.FFF first, then M:SS.FFF
        var m = System.Text.RegularExpressions.Regex.Match(s,
            @"^(\d+):(\d+):([\d.]+)$");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var h) &&
            int.TryParse(m.Groups[2].Value, out var mn) &&
            double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var sc))
            return h * 3600 + mn * 60 + sc;

        m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+):([\d.]+)$");
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var mins) &&
            double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            return mins * 60 + secs;

        return -1;
    }

    private static string GetColumnValue(WemEntry entry, string colName)
    {
        return colName.Trim().ToLowerInvariant() switch
        {
            "wemid" => entry.WemID,
            "coord" => entry.Coord,
            "jsonfile" => entry.JsonFile,
            "isstreaming" => entry.IsStreaming,
            "wemsize" => entry.WemSize,
            "wemfile" => entry.Filename,
            "wempath" => entry.Path,
            "foundinbanks" => entry.FoundInBanks,
            "bankcount" => entry.BankCount,
            "banks" => entry.Banks,
            "txtpfiles" => entry.TxtpFiles,
            "label" => entry.Label ?? "",
            "duration" => entry.DurationDisplay,
            "channel" => entry.ChannelConfig,
            _ => entry.ExtraColumns.TryGetValue(colName.Trim().ToLowerInvariant(), out var v) ? v : ""
        };
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    #endregion

    #region Settings

    private void SetVgmstreamPath()
    {
        var initDir = !string.IsNullOrEmpty(_config.VgmstreamPath) ? Path.GetDirectoryName(_config.VgmstreamPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dlg = new OpenFileDialog { Title = Locale.S("dlg_vgmstream_select"), Filter = Locale.S("filter_vgmstream"), InitialDirectory = initDir };
        if (dlg.ShowDialog() == true)
        {
            _config.VgmstreamPath = dlg.FileName;
            ConfigManager.Save(_config);
            SetStatus(Locale.S("status_vgmstream_set", _config.VgmstreamPath));
        }
    }

    #endregion

    #region Duration Fetching

    private void StartFetchingDurations()
    {
        _durationCts?.Cancel();
        _durationCts = new CancellationTokenSource();
        var token = _durationCts.Token;
        var vgmPath = _config.VgmstreamPath;

        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath))
            return;

        var pending = _entries.Where(e => e.DurationSeconds < 0).ToList();
        if (pending.Count == 0) return;

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (token.IsCancellationRequested) return;

                var entry = pending[i];
                var wemPath = entry.Path;
                if (!File.Exists(wemPath)) continue;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = vgmPath,
                        Arguments = $"-m \"{wemPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) continue;

                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0) continue;

                    // Parse channels from "channels: N"
                    var chMatch = System.Text.RegularExpressions.Regex.Match(output,
                        @"channels:\s*(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var channels))
                    {
                        var chDisplay = WemEntry.GetChannelDisplayName(channels);
                        Dispatcher.Invoke(() => entry.ChannelConfig = chDisplay, DispatcherPriority.Background);
                    }

                    // Parse duration from "play duration: N samples (M:S.FFF seconds)"
                    // Try h:mm:ss.fff first, then m:ss.fff
                    var durMatch = System.Text.RegularExpressions.Regex.Match(output,
                        @"play duration:.*\((\d+):(\d+):([\d.]+)\s*seconds\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (durMatch.Success &&
                        int.TryParse(durMatch.Groups[1].Value, out var hours) &&
                        int.TryParse(durMatch.Groups[2].Value, out var minutes) &&
                        double.TryParse(durMatch.Groups[3].Value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        var dur = hours * 3600 + minutes * 60 + seconds;
                        Dispatcher.Invoke(() => entry.DurationSeconds = dur, DispatcherPriority.Background);
                    }
                    else
                    {
                        durMatch = System.Text.RegularExpressions.Regex.Match(output,
                            @"play duration:.*\((\d+):([\d.]+)\s*seconds\)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (durMatch.Success &&
                            double.TryParse(durMatch.Groups[1].Value, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var mins) &&
                            double.TryParse(durMatch.Groups[2].Value, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var secs))
                        {
                            var dur = mins * 60 + secs;
                            Dispatcher.Invoke(() => entry.DurationSeconds = dur, DispatcherPriority.Background);
                        }
                    }
                }
                catch { }
            }

            Dispatcher.Invoke(() =>
            {
                SetStatus(Locale.S("status_durations_done", pending.Count(e => e.DurationSeconds >= 0), pending.Count));
                if (!string.IsNullOrEmpty(_loadedCsvPath)) WriteCsvFile(_loadedCsvPath);
            });
        }, token);
    }

    #endregion

    #region Helpers

    private void SetStatus(string message) => StatusText.Text = message;

    /// <summary>
    /// 显示「正在解码音频」提示。不弹窗、不禁用界面，只是别让人以为程序卡住了。
    /// </summary>
    private void ShowDecoding(string what)
    {
        if (DecodingBanner == null) return;
        DecodingText.Text = Locale.S("decoding_banner", what);
        DecodingBanner.Visibility = Visibility.Visible;
    }

    private void HideDecoding()
    {
        if (DecodingBanner == null) return;
        DecodingBanner.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            Cursor = Cursors.Wait;
            FileListView.IsEnabled = false;
            LabelTextBox.IsEnabled = false;
            SaveButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            ExportWavCoordButton.IsEnabled = false;
            ExportTracksButton.IsEnabled = false;
            PlayEntryButton.IsEnabled = false;
        }
        else
        {
            Cursor = null;
            FileListView.IsEnabled = true;
            RestoreDetailControls();
        }
    }

    /// <summary>
    /// 忙完一轮（SetBusy(false)）后按当前选中行恢复右侧控件的可用状态。
    /// 以前只恢复 FileListView，导致导出/导入标注等流程结束后标注框一直灰着。
    /// </summary>
    private void RestoreDetailControls()
    {
        var hasSelection = _currentIndex >= 0 && _currentIndex < _entries.Count;
        LabelTextBox.IsEnabled = hasSelection;
        SaveButton.IsEnabled = hasSelection;
        PlayButton.IsEnabled = hasSelection;
        PlayEntryButton.IsEnabled = hasSelection;
        PrevButton.IsEnabled = hasSelection && _currentIndex > 0;
        NextButton.IsEnabled = hasSelection && _currentIndex < _entries.Count - 1;
        ExportWavCoordButton.IsEnabled = _entries.Any(e => e.HasLabel);
    }

    /// <summary>
    /// 选择文件夹。用 .NET 8+ 的 <see cref="Microsoft.Win32.OpenFolderDialog"/>
    /// （就是「打开CSV」那种标准资源管理器样式的对话框），
    /// 取代以前 SHBrowseForFolder 那个老式树形框。
    /// </summary>
    private string? PickFolder(string title, string? initialDirectory = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    #endregion
}
