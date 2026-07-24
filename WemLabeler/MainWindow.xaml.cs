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
    private byte[]? _previewWavBytes;

    private MenuItem _fileMenuItem = null!;
    private MenuItem _openCsvItem = null!;
    private MenuItem _reloadCsvItem = null!;
    private MenuItem _openWemFolderItem = null!;
    private MenuItem _openTxtpItem = null!;
    private MenuItem _exportCsvItem = null!;
    private MenuItem _exportWavItem = null!;
    private MenuItem _exportTxtpItem = null!;
    private MenuItem _vgmstreamItem = null!;
    private MenuItem _exitItem = null!;
    private MenuItem _helpMenuItem = null!;
    private MenuItem _aboutItem = null!;
    private MenuItem _langMenuItem = null!;
    private MenuItem _langZhItem = null!;
    private MenuItem _langEnItem = null!;

    public MainWindow()
    {
        InitializeComponent();
        _config = ConfigManager.Load();

        Locale.SetLanguage(_config.Language);
        Locale.OnLanguageChanged += ApplyLocale;

        FileListView.ItemsSource = _entries;
        AutoPlayCheck.IsChecked = _config.AutoPlay;
        UpdateProgress();
        BuildMenu();
        ApplyLocale();
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

        _helpMenuItem.Items.Add(_langMenuItem);
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

        _fileMenuItem.Header = L("menu_file");
        _openCsvItem.Header = L("menu_open_csv");
        _reloadCsvItem.Header = L("menu_reload_csv");
        _openWemFolderItem.Header = L("menu_open_wem_folder");
        _openTxtpItem.Header = L("menu_open_txtp");
        _exportCsvItem.Header = L("menu_export_csv");
        _exportWavItem.Header = L("menu_export_wav");
        _exportTxtpItem.Header = L("menu_export_txtp");
        _vgmstreamItem.Header = L("menu_vgmstream");
        _exitItem.Header = L("menu_exit");
        _helpMenuItem.Header = L("menu_help");
        _langMenuItem.Header = L("menu_language");
        _langZhItem.Header = L("menu_lang_zh");
        _langEnItem.Header = L("menu_lang_en");
        _aboutItem.Header = L("menu_about");

        PlayButton.Content = L("btn_play");
        StopButton.Content = L("btn_stop");
        AutoPlayCheck.Content = L("chk_autoplay");
        LabelHint.Text = L("lbl_label");
        PrevButton.Content = L("btn_prev");
        NextButton.Content = L("btn_next");
        ExportWavCoordButton.Content = L("btn_export_wav_coord");
        FileInfoGroup.Header = L("gb_file_info");
        SourceInfoGroup.Header = L("gb_source_info");

        if (_currentIndex < 0)
        {
            InfoFilename.Text = L("lbl_no_file");
            SaveButton.Content = L("btn_save");
        }

        if (_suppressLabelEvents || _currentIndex < 0)
            SaveButton.Content = L("btn_save");
        else
            SaveButton.Content = L("btn_save_unsaved");

        SetStatus(L("status_idle"));
        UpdateProgress();

        if (!string.IsNullOrEmpty(_loadedCsvPath))
            Title = Locale.S("title", Path.GetFileName(_loadedCsvPath), _entries.Count);

        _langZhItem.IsChecked = Locale.Language == "zh-CN";
        _langEnItem.IsChecked = Locale.Language == "en-US";
    }

    #region Window Events

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_config.VgmstreamPath))
        {
            var result = MessageBox.Show(this,
                Locale.S("dlg_welcome"),
                Locale.S("dlg_welcome_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
                SetVgmstreamPath();
        }

        if (!string.IsNullOrEmpty(_config.LastCsvPath) && _entries.Count == 0)
        {
            var answer = MessageBox.Show(this,
                Locale.S("dlg_resume", _config.LastCsvPath),
                Locale.S("dlg_resume_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                LoadCsvAsync(_config.LastCsvPath);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            SaveCurrentLabel();
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
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.Up: e.Handled = true; if (_currentIndex > 0) Navigate(-1); break;
            case Key.Down: e.Handled = true; if (_currentIndex < _entries.Count - 1) Navigate(1); break;
            case Key.Space:
                if (LabelTextBox.IsKeyboardFocusWithin) break;
                e.Handled = true;
                if (_vgmstreamProcess != null && !_vgmstreamProcess.HasExited) StopPlayback();
                else PlayCurrent();
                break;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
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
    }

    #endregion

    #region Menu Events

    private void OpenCsv_Click()
    {
        var initDir = !string.IsNullOrEmpty(_loadedCsvPath) ? Path.GetDirectoryName(_loadedCsvPath) : AppDomain.CurrentDomain.BaseDirectory;
        var dlg = new OpenFileDialog { Title = Locale.S("dlg_open_csv"), Filter = Locale.S("filter_csv"), InitialDirectory = initDir };
        if (dlg.ShowDialog() == true) LoadCsvAsync(dlg.FileName);
    }

    private void ReloadCsv_Click() { if (!string.IsNullOrEmpty(_loadedCsvPath)) LoadCsvAsync(_loadedCsvPath); }

    private void OpenWemFolder_Click()
    {
        var wemFolder = PickFolder(Locale.S("dlg_open_wem_folder"));
        if (wemFolder == null) return;
        var jsonFolder = PickFolder(Locale.S("dlg_open_wem_json_folder"));
        if (jsonFolder == null) return;
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
        Task.Run(() =>
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
                writer.WriteLine("WemID,Coord,JsonFile,IsStreaming,WemSize,WemFile,WemPath,FoundInBankRes,TxtpFiles,Label,Duration,Channel");
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
                        // Try to read JSON to get WemID and duration
                        var jsonPath = Path.Combine(jsonFolder, jsonFile);
                        if (File.Exists(jsonPath))
                        {
                            try
                            {
                                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(jsonPath));
                                var root = json.RootElement;
                                if (root.TryGetProperty("WemID", out var wemIdEl))
                                    wemId = wemIdEl.GetInt64().ToString();
                                else
                                    wemId = coord.Replace(":", "");
                                if (root.TryGetProperty("mLengthInSeconds", out var durEl) && durEl.GetDouble() > 0)
                                    duration = durEl.GetDouble();
                            }
                            catch
                            {
                                wemId = coord.Replace(":", "");
                            }
                        }
                        else
                        {
                            wemId = coord.Replace(":", "");
                        }
                    }
                    else
                    {
                        // Fallback: use file hash, try JSON by filename
                        wemId = Math.Abs(wemPath.GetHashCode()).ToString();
                        coord = "";
                        jsonFile = $"{name}.json";
                        wemFile = $"{name}.wem";
                        var jsonPath = Path.Combine(jsonFolder, jsonFile);
                        if (File.Exists(jsonPath))
                        {
                            try
                            {
                                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(jsonPath));
                                var root = json.RootElement;
                                if (root.TryGetProperty("WemID", out var wemIdEl))
                                    wemId = wemIdEl.GetInt64().ToString();
                                if (root.TryGetProperty("mLengthInSeconds", out var durEl) && durEl.GetDouble() > 0)
                                    duration = durEl.GetDouble();
                            }
                            catch { }
                        }
                    }
                    var size = fi.Length.ToString();
                    var isStreaming = "False";
                    var durStr = duration >= 0 ? (duration >= 3600
                        ? $"{(int)(duration / 3600)}:{(int)(duration % 3600 / 60):D2}:{(int)(duration % 60):D2}.{(int)(duration * 1000 % 1000):D3}"
                        : $"{(int)(duration / 60)}:{(int)(duration % 60):D2}.{(int)(duration * 1000 % 1000):D3}") : "";
                    writer.WriteLine($"{EscapeCsv(wemId)},{EscapeCsv(coord)},{EscapeCsv(jsonFile)},{EscapeCsv(isStreaming)},{EscapeCsv(size)},{EscapeCsv(wemFile)},{EscapeCsv(wemPath)},,,,{EscapeCsv(durStr)},");
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
        Task.Run(() =>
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
                    WemEntry? entry = null;
                    if (!string.IsNullOrEmpty(numId) && numId.All(char.IsAsciiDigit))
                        entry = _entries.FirstOrDefault(e => e.WemID == numId);
                    if (entry != null)
                    {
                        resolvedLines.Add($"{leading}{entry.Path}{suffix}");
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
                Dispatcher.Invoke(() =>
                {
                    _resolvedTxtpPath = outPath;
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

    private void PlayTxtpResolved()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;
        if (string.IsNullOrEmpty(_resolvedTxtpPath) || !File.Exists(_resolvedTxtpPath)) return;
        var vgmPath = _config.VgmstreamPath;
        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath)) return;
        StopPlayback();
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
            Directory.CreateDirectory(tempDir);
            var tempWav = Path.Combine(tempDir, $"txtp_preview_{Guid.NewGuid():N}.wav");
            StatusProgress.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            VgmLog("========== txtp decode start ==========");
            VgmLog($"input: {_resolvedTxtpPath}");
            var psi = new ProcessStartInfo
            {
                FileName = vgmPath,
                Arguments = $"-o \"{tempWav}\" -i \"{_resolvedTxtpPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            _vgmstreamProcess = Process.Start(psi);
            if (_vgmstreamProcess == null) { VgmLog("[error] Process.Start returned null"); CleanupPlayback(false); return; }
            var stdOut = _vgmstreamProcess.StandardOutput.ReadToEndAsync();
            var stdErr = _vgmstreamProcess.StandardError.ReadToEndAsync();
            Task.Run(async () =>
            {
                try
                {
                    await _vgmstreamProcess.WaitForExitAsync();
                    var outText = await stdOut;
                    var errText = await stdErr;
                    _vgmstreamProcess.Dispose();
                    _vgmstreamProcess = null;
                    byte[] wavBytes = [];
                    if (File.Exists(tempWav))
                    {
                        var fi = new FileInfo(tempWav);
                        if (fi.Length > 44) wavBytes = File.ReadAllBytes(tempWav);
                    }
                    try { File.Delete(tempWav); } catch { }
                    var finalBytes = wavBytes;
                    Dispatcher.Invoke(() =>
                    {
                        if (finalBytes.Length > 44)
                        {
                            _previewWavBytes = finalBytes;
                            VgmLog("txtp decode success, playing");
                            LoadAndPlay(finalBytes);
                        }
                        else
                        {
                            VgmLog($"[error] txtp decode failed or empty: {errText}");
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

    #endregion

    #endregion

    #region CSV Loading

    private void LoadCsvAsync(string csvPath)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        _previewWavBytes = null;
        var path = csvPath;

        SetBusy(true);
        SetStatus(Locale.S("status_loading"));
        _entries.Clear();
        _currentIndex = -1;
        ResetDetail();

        Task.Run(() =>
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
                    TrySet(colMap, parts, "txtpfiles", v => entry.Banks = v);
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
                    _ = Dispatcher.InvokeAsync(() => StartFetchingDurations(), DispatcherPriority.ApplicationIdle);
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
        InfoWwiseID.Text = "";
        LabelTextBox.Text = "";
        LabelTextBox.IsEnabled = false;
        SaveButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        ExportWavCoordButton.IsEnabled = false;
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

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayCurrent();

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
        InfoWemRes.Text = Locale.S("lbl_wemres", entry.CoordSummary);
        InfoBanks.Text = Locale.S("lbl_banks", entry.BankSummary);
        InfoWwiseID.Text = string.IsNullOrEmpty(entry.WemSize) ? "" : Locale.S("lbl_wwiseid", entry.SizeDisplay);

        LabelTextBox.Text = entry.Label ?? "";
        LabelTextBox.IsEnabled = true;
        SaveButton.IsEnabled = true;
        SaveButton.Content = Locale.S("btn_save");
        PlayButton.IsEnabled = true;
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _entries.Count - 1;
        ExportWavCoordButton.IsEnabled = _entries.Any(e => e.HasLabel);
        _suppressLabelEvents = false;

        if (AutoPlayCheck.IsChecked == true) PlayCurrent();
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

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayCurrent();
    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void PlayCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _entries.Count) return;

        if (_pcmData != null && _pcmFormat != null && _totalSamples > 0)
        {
            StopWavePlayer();
            VgmLog($"resuming from sample {_samplePosition}");
            PlayFromMemory(_samplePosition);
            StartPlaybackTimer();
            StatusProgress.Visibility = Visibility.Collapsed;
            SetStatus(Locale.S("status_playing"));
            return;
        }

        StopPlayback();
        var entry = _entries[_currentIndex];
        var wemPath = entry.Path;
        if (!File.Exists(wemPath))
        {
            VgmLog($"[error] file not found: {wemPath}");
            SetStatus(Locale.S("status_file_not_found_short", wemPath));
            return;
        }
        var vgmPath = _config.VgmstreamPath;
        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath))
        {
            SetStatus(Locale.S("status_vgmstream_not_set"));
            var result = MessageBox.Show(this, Locale.S("dlg_vgmstream_missing"), Locale.S("dlg_vgmstream_missing_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) SetVgmstreamPath();
            return;
        }
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WemLabeler");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"preview_{Guid.NewGuid():N}.wav");

            StatusProgress.Visibility = Visibility.Visible;
            SetStatus(Locale.S("status_decoding", entry.Filename));
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
            Task.Run(async () =>
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
                            LoadAndPlay(finalBytes);
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
            VgmLog($"[exception] PlayCurrent: {ex}");
            SetStatus(Locale.S("status_play_exception", ex.Message));
            CleanupPlayback(false);
        }
    }

    private void LoadAndPlay(byte[] wavBytes)
    {
        try
        {
            ClearWaveform();
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

        _wavePlayer = new WasapiOut();
        _wavePlayer.PlaybackStopped += (_, _) =>
            Dispatcher.Invoke(() => { StopWavePlayer(); CleanupPlayback(true); });
        _wavePlayer.Init(stream);
        _wavePlayer.Play();
        VgmLog($"WASAPI playing from sample {startSample}");
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

    private void WriteCsvFile(string path)
    {
        if (_entries.Count == 0) return;
        var tmpPath = path + ".tmp";
        using (var writer = new StreamWriter(tmpPath, false, Encoding.UTF8))
        {
            var hasLabelInHeader = _originalHeader.Any(h =>
                h.Equals("Label", StringComparison.OrdinalIgnoreCase));
            var hasDurationInHeader = _originalHeader.Any(h =>
                h.Equals("Duration", StringComparison.OrdinalIgnoreCase));
            var hasChannelInHeader = _originalHeader.Any(h =>
                h.Equals("Channel", StringComparison.OrdinalIgnoreCase));

            var headerParts = new List<string>(_originalHeader);
            if (!hasLabelInHeader) headerParts.Add("Label");
            if (!hasDurationInHeader) headerParts.Add("Duration");
            if (!hasChannelInHeader) headerParts.Add("Channel");
            writer.WriteLine(string.Join(",", headerParts));

            foreach (var entry in _entries)
            {
                var parts = new List<string>();
                foreach (var col in _originalHeader)
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

    private void ExportWav()
    {
        var labeled = _entries.Where(e => e.HasLabel).ToList();
        if (labeled.Count == 0)
        {
            MessageBox.Show(this, Locale.S("dlg_export_wav_none"), Locale.S("dlg_no_data_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var vgmPath = _config.VgmstreamPath;
        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath))
        {
            SetStatus(Locale.S("status_vgmstream_not_set"));
            var result = MessageBox.Show(this, Locale.S("dlg_vgmstream_missing"), Locale.S("dlg_vgmstream_missing_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) SetVgmstreamPath();
            return;
        }
        var outDir = PickFolder(Locale.S("dlg_export_wav_choose"));
        if (outDir == null) return;

        SetBusy(true);
        int success = 0, failed = 0;
        Task.Run(() =>
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

    private void ExportWavCoord()
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
        var vgmPath = _config.VgmstreamPath;
        if (string.IsNullOrEmpty(vgmPath) || !File.Exists(vgmPath))
        {
            SetStatus(Locale.S("status_vgmstream_not_set"));
            var result = MessageBox.Show(this, Locale.S("dlg_vgmstream_missing"), Locale.S("dlg_vgmstream_missing_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) SetVgmstreamPath();
            return;
        }
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
        var outDir = PickFolder(Locale.S("dlg_export_wav_choose"));
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
        Task.Run(() =>
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

        Task.Run(async () =>
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
        }
        else { Cursor = null; FileListView.IsEnabled = true; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, IntPtr pszPath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private static string? PickFolder(string title)
    {
        var pathPtr = Marshal.AllocHGlobal(260 * 2);
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = IntPtr.Zero,
                lpszTitle = title,
                ulFlags = 0x00000040 | 0x00000001
            };
            var pidl = SHBrowseForFolder(ref bi);
            if (pidl != IntPtr.Zero)
            {
                SHGetPathFromIDList(pidl, pathPtr);
                Marshal.FreeCoTaskMem(pidl);
                return Marshal.PtrToStringUni(pathPtr);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    #endregion
}
