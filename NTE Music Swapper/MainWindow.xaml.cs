using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NtePakTool
{
    public partial class MainWindow : Window
    {
        private readonly string unpackDir;
        private readonly ObservableCollection<WemMapItem> WemList = new ObservableCollection<WemMapItem>();
        private readonly ObservableCollection<UiWemItem> UiWemList = new ObservableCollection<UiWemItem>();
        private readonly GameService gameService;
        private readonly AudioService audioService;
        private System.Timers.Timer monitorTimer = null!;
        private bool isSwapping = false;
        private bool isHtRunningLastState = false;
        private bool _isClosingHandled = false;
        private AppSettingsJson appSettings = new AppSettingsJson();
        private readonly string settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.json");
        private const string CurrentVersion = "1.4";
        private bool isBuildingMod = false;
        //コンストラクタ / 初期化
        public MainWindow()
        {
            InitializeComponent();

            unpackDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unpak", "HT");

            // WwiseConsoleの検出より前に設定を読み込み、キャッシュを活用できるようにする
            LoadSettings();

            string wwiseConsolePath = DetectWwiseConsolePath();

            gameService = new GameService(unpackDir, Log);
            audioService = new AudioService(wwiseConsolePath, unpackDir, gameService, Log);

            Log($"Using WwiseConsole: {wwiseConsolePath}");

            FileList.ItemsSource = WemList;
            UiListControl.ItemsSource = UiWemList;

            _ = InitializeAppAsync();
        }

        private string DetectWwiseConsolePath()
        {
            const string subPath = @"Authoring\x64\Release\bin\WwiseConsole.exe";
            string fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WemTool", "WwiseConsole.exe");

            // キャッシュされたパスが有効かチェック
            if (!string.IsNullOrEmpty(appSettings.WwiseConsolePath) &&
                File.Exists(appSettings.WwiseConsolePath))
            {
                Log($"Cache: Loading WwiseConsole path from settings: {appSettings.WwiseConsolePath}");
                return appSettings.WwiseConsolePath;
            }

            string? foundPath = null;

            string? wwiseRoot = Environment.GetEnvironmentVariable("WWISEROOT");
            if (!string.IsNullOrEmpty(wwiseRoot))
            {
                string path = Path.Combine(wwiseRoot.Trim('"'), subPath);
                if (File.Exists(path)) foundPath = path;
            }

            if (foundPath == null)
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
                foreach (var drive in drives)
                {
                    string prog86 = Path.Combine(drive.Name, "Program Files (x86)");
                    if (!Directory.Exists(prog86)) continue;
                    try
                    {
                        var wwiseDirs = Directory.GetDirectories(prog86, "*Wwise*", SearchOption.TopDirectoryOnly);
                        foreach (var dir in wwiseDirs)
                        {
                            string path = Path.Combine(dir, subPath);
                            if (File.Exists(path)) { foundPath = path; break; }
                        }
                    }
                    catch { }
                    if (foundPath != null) break;
                }
            }

            // 有効なパスが見つかった場合はJSONに保存（フォールバックパスは保存しない）
            if (foundPath != null)
            {
                appSettings.WwiseConsolePath = foundPath;
                SaveSettings();
                Log("Cache: WwiseConsole path saved to settings.");
                return foundPath;
            }

            return fallbackPath;
        }

        private async Task InitializeAppAsync()
        {
            Log("Starting App Initialization...");
            string latestVersionStr = await gameService.GetLatestVersionAsync();
            if (Version.TryParse(CurrentVersion, out Version currentVer) && Version.TryParse(latestVersionStr, out Version latestVer))
            {
                if (latestVer > currentVer)
                {
                    Log($"Update available: v{latestVersionStr}");
                    var result = System.Windows.MessageBox.Show(
                        $"A new version (v{latestVersionStr}) is available!\nDo you want to open the download page?",
                        "Update Available",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.OK)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "https://ayakamods.com/mods/nte-music-swapper.2417/",
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            Log("Browser launch failed: " + ex.Message);
                        }
                    }
                }
            }
            bool htRunning = gameService.IsProcessRunning("HTGame");
            bool nteRunning = gameService.IsProcessRunning("NTEGlobalGame");

            if (htRunning || nteRunning)
            {
                if (htRunning)
                {
                    Log("Startup check: HTGame is running. Forcing termination...");
                    gameService.KillProcessesContainingName("HTGame");
                }
                if (nteRunning)
                {
                    Log("Startup check: NTEGlobalGame is running. Forcing termination...");
                    gameService.KillProcessesContainingName("NTEGlobalGame");
                }

                int retryCount = 0;
                while ((gameService.IsProcessRunning("HTGame") || gameService.IsProcessRunning("NTEGlobalGame")) && retryCount < 10)
                {
                    await Task.Delay(500);
                    retryCount++;
                }

                bool htStillRunning = gameService.IsProcessRunning("HTGame");
                bool nteStillRunning = gameService.IsProcessRunning("NTEGlobalGame");

                if (htStillRunning || nteStillRunning)
                {
                    var failedNames = string.Join(" / ",
                        new[] { htStillRunning ? "HTGame" : null, nteStillRunning ? "NTEGlobalGame" : null }
                        .Where(n => n != null));
                    Log($"Warning: Could not terminate {failedNames} completely.");
                    System.Windows.MessageBox.Show(
                        $"{failedNames} failed to close. Please close the game manually, as files may be locked during MOD operations.",
                        "Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    Log("Startup check: HTGame and NTEGlobalGame terminated successfully.");
                }
            }

            audioService.LoadUiConfig(UiWemList);
            audioService.RefreshList(WemList);

            await gameService.DetectGameRootAsync(appSettings, SaveSettings);
            await gameService.CheckAndStartLauncherOnStartupAsync(appSettings, SaveSettings);

            InitMonitor();
        }

        //設定のロードとセーブ
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettingsJson>(json);
                    if (settings != null) appSettings = settings;
                }
            }
            catch (Exception ex) { Log("Settings Load Error: " + ex.Message); }

            ChkSaveMod.IsChecked = appSettings.SaveModEnabled;
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(appSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsFilePath, json);
            }
            catch (Exception ex) { Log("Settings Save Error: " + ex.Message); }
        }

        //モニター
        private void InitMonitor()
        {
            monitorTimer = new System.Timers.Timer(500);
            monitorTimer.Elapsed += OnMonitorTick;
            monitorTimer.Start();
        }

        private async void OnMonitorTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            monitorTimer.Stop();
            try
            {
                bool isHtCurrentlyRunning = gameService.IsProcessRunning("HTGame");

                if (isHtRunningLastState && !isHtCurrentlyRunning && !isSwapping)
                {
                    isSwapping = true;
                    Log("HTGame closed. Forcing Launcher termination...");

                    gameService.KillNteGlobalLauncher();
                    await Task.Delay(1000);
                    await gameService.PerformBypassSwap();

                    isSwapping = false;
                }
                isHtRunningLastState = isHtCurrentlyRunning;
            }
            finally
            {
                monitorTimer.Start();
            }
        }

        // UIイベントハンドラ
        private void RefreshList_Click(object sender, RoutedEventArgs e)
            => audioService.RefreshList(WemList);

        private void Jacket_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is UiWemItem item)
                item.IsSelected = !item.IsSelected;
        }

        private async void BrowseAudio_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is WemItemBase item)
            {
                var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Audio Files|*.wav;*.mp3;*.ogg" };
                if (ofd.ShowDialog() != true) return;

                string selectedPath = ofd.FileName;
                string ext = Path.GetExtension(selectedPath).ToLowerInvariant();

                if (ext == ".wav")
                {
                    item.SourceAudioPath = selectedPath;
                    item.IsSelected = true;
                }
                else
                {
                    Log($"Non-WAV file detected ({ext}). Converting to WAV in background...");
                    item.SourceAudioPath = "Converting... please wait";
                    item.IsSelected = false;

                    try
                    {
                        string convertedWav = await audioService.ConvertToWavAsync(selectedPath);
                        item.SourceAudioPath = convertedWav;
                        item.IsSelected = true;
                        Log($"Conversion complete: {Path.GetFileName(convertedWav)}");
                    }
                    catch (Exception ex)
                    {
                        item.SourceAudioPath = "No file selected";
                        Log($"WAV conversion failed: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Failed to convert the audio file to WAV:\n{ex.Message}\n\nPlease convert it to WAV manually and try again.",
                            "Conversion Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private async void BuildPak_Click(object sender, RoutedEventArgs e)
        {
            var targets = WemList.Where(x => x.IsSelected && File.Exists(x.SourceAudioPath)).Cast<WemItemBase>()
                .Concat(UiWemList.Where(x => x.IsSelected && File.Exists(x.SourceAudioPath)).Cast<WemItemBase>())
                .GroupBy(x => Path.GetFileName(x.RelativePath))
                .Select(g => g.First())
                .ToList();

            if (!targets.Any())
            {
                System.Windows.MessageBox.Show("Please select an item and specify the audio file.");
                return;
            }

            // 【追加】ビルド中フラグをONにする
            isBuildingMod = true;

            try
            {
                foreach (var t in targets)
                {
                    string fileName = Path.GetFileName(t.RelativePath);
                    var actualFiles = Directory.GetFiles(unpackDir, fileName, SearchOption.AllDirectories);
                    if (actualFiles.Length > 0)
                        t.FullPath = actualFiles.OrderByDescending(p => p.Length).First();
                }

                // 1. HTGameが起動している場合は強制終了
                if (gameService.IsProcessRunning("HTGame"))
                {
                    Log("Build: HTGame is running. Forcing termination...");
                    gameService.KillProcessesContainingName("HTGame");

                    // 終了するまで少し待機 (最大5秒)
                    int retryHt = 0;
                    while (gameService.IsProcessRunning("HTGame") && retryHt < 10)
                    {
                        await Task.Delay(500);
                        retryHt++;
                    }
                }

                // 2. NTEGlobalGameが起動していない場合は起動
                if (!gameService.IsProcessRunning("NTEGlobalGame"))
                {
                    if (!string.IsNullOrEmpty(gameService.LauncherPath) && File.Exists(gameService.LauncherPath))
                    {
                        Log("Build: Starting NTEGlobalGame...");
                        try
                        {
                            Process.Start(new ProcessStartInfo(gameService.LauncherPath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            Log("Build Launcher Start Error: " + ex.Message);
                            System.Windows.MessageBox.Show($"Failed to start launcher:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    else
                    {
                        Log("Build Error: Launcher path not found.");
                        System.Windows.MessageBox.Show("Cannot find NTEGlobalLauncher.exe. Build aborted.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                // 3. NTEGlobalGameのウィンドウが確実に表示されるまで待機
                Log("Build: Waiting for NTEGlobalGame window to appear...");
                int waitCount = 0;
                // 最大30秒(60回 x 500ms)待機する
                while (!gameService.IsLauncherWindowVisible("NTEGlobalGame") && waitCount < 60)
                {
                    await Task.Delay(500);
                    waitCount++;
                }

                if (!gameService.IsLauncherWindowVisible("NTEGlobalGame"))
                {
                    Log("Build Error: Timeout waiting for Launcher window.");
                    System.Windows.MessageBox.Show("Timed out waiting for the game window to appear.", "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Log("Build: Launcher window detected. Starting Pak build process...");
                // ===============================================

                // 音声変換・Pak化処理の開始
                await audioService.BuildModAsync(targets);
            }
            finally
            {
                // 【追加】処理が完了（またはエラーで中断）したら、必ずフラグをOFFにする
                isBuildingMod = false;
            }
        }

        private void ChkSaveMod_Changed(object sender, RoutedEventArgs e)
        {
            appSettings.SaveModEnabled = ChkSaveMod.IsChecked == true;
            SaveSettings();
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 【追加】ビルド中は終了をブロックする
            if (isBuildingMod)
            {
                System.Windows.MessageBox.Show(
                    "A mod build is currently in progress. Please wait until it completes before closing the tool.",
                    "Build in Progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            if (_isClosingHandled) return;
            if (string.IsNullOrEmpty(gameService.OutputDir) || !Directory.Exists(gameService.OutputDir)) return;

            if (monitorTimer != null)
            {
                monitorTimer.Stop();
                monitorTimer.Dispose();
            }

            e.Cancel = true;
            try
            {
                gameService.KillProcessesContainingName("HTGame");
                gameService.KillProcessesContainingName("NTEGlobalGame");

                int retryCount = 0;
                while ((gameService.IsProcessRunning("HTGame") || gameService.IsProcessRunning("NTEGlobalGame")) && retryCount < 20)
                {
                    await Task.Delay(500);
                    retryCount++;
                }

                string bakFile = Path.Combine(gameService.OutputDir, "bak", "pakchunk3-Windows.pak.bak");
                string pakPath = Path.Combine(gameService.OutputDir, "pakchunk3-Windows.pak");

                if (File.Exists(bakFile))
                {
                    bool restoreSuccess = false;
                    int fileRetryCount = 0;

                    while (!restoreSuccess && fileRetryCount < 10)
                    {
                        try
                        {
                            if (File.Exists(pakPath)) File.Delete(pakPath);
                            File.Copy(bakFile, pakPath, true);
                            restoreSuccess = true;
                            Debug.WriteLine("Original PAK restored successfully on closing.");
                        }
                        catch (IOException)
                        {
                            fileRetryCount++;
                            await Task.Delay(500);
                        }
                    }

                    if (!restoreSuccess)
                    {
                        System.Windows.MessageBox.Show(
                            "The restoration of the legitimate PAK file failed because the game process is continuously locking the file.\n" +
                            "We apologize for the inconvenience, but please manually move the pakchunk3-Windows.pak.bak file from the bak folder back to its original paks folder.",
                            "Restore error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"An unexpected error occurred during the termination process.:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isClosingHandled = true;
                this.Close();
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var view = CollectionViewSource.GetDefaultView(WemList);
            if (string.IsNullOrWhiteSpace(TxtSearch.Text))
                view.Filter = null;
            else
            {
                string query = TxtSearch.Text.ToLower();
                view.Filter = item => (item is WemMapItem wemItem) && wemItem.RelativePath.ToLower().Contains(query);
            }
        }

        //ユーティリティ
        private void Log(string m) =>
            Dispatcher.Invoke(() =>
            {
                TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {m}\n";
                LogScroller.ScrollToEnd();
            });
    }
}