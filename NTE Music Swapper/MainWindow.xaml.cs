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
        private const string CurrentVersion = "1.5";
        private bool isBuildingMod = false;
        
        //Constructor / Initialization
        public MainWindow()
        {
            InitializeComponent();

            unpackDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unpak", "HT");

            // Load the settings before WwiseConsole detects them, allowing you to leverage the cache.
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

            // Check if the cached path is valid.
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

            // If a valid path is found, save it to JSON (do not save the fallback path).
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

        // Loading and saving settings
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
            ChkUseYouTube.IsChecked = appSettings.UseYouTubeLink;
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

        //Monitor
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

        // UI event handler
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
                if (appSettings.UseYouTubeLink)
                {
                    // Display YouTube URL input dialog
                    var dialog = new YouTubeInputWindow { Owner = this };
                    if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.YouTubeUrl))
                        return;

                    string url = dialog.YouTubeUrl.Trim();
                    item.SourceAudioPath = "YouTube: Downloading... please wait";
                    item.IsSelected = false;
                    BtnBuildMod.IsEnabled = false;

                    try
                    {
                        string convertedWav = await audioService.DownloadFromYouTubeAsync(url);
                        item.SourceAudioPath = convertedWav;
                        item.IsSelected = true;
                        Log($"YouTube download complete: {Path.GetFileName(convertedWav)}");
                    }
                    catch (Exception ex)
                    {
                        item.SourceAudioPath = "No file selected";
                        Log($"YouTube download failed: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"Download from YouTube failed.:\n{ex.Message}",
                            "Download Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    finally
                    {
                        BtnBuildMod.IsEnabled = true;
                    }
                }
                else
                {
                    // Normal file selection dialog
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
        }

        private void ChkUseYouTube_Changed(object sender, RoutedEventArgs e)
        {
            appSettings.UseYouTubeLink = ChkUseYouTube.IsChecked == true;
            SaveSettings();
            Log($"Use YouTube Link: {(appSettings.UseYouTubeLink ? "ON" : "OFF")}");
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

            // Turn on the build flag.
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

                // 1. If HTGame is running, force quit it.
                if (gameService.IsProcessRunning("HTGame"))
                {
                    Log("Build: HTGame is running. Forcing termination...");
                    gameService.KillProcessesContainingName("HTGame");

                    // Wait briefly until it finishes (up to 5 seconds)
                    int retryHt = 0;
                    while (gameService.IsProcessRunning("HTGame") && retryHt < 10)
                    {
                        await Task.Delay(500);
                        retryHt++;
                    }
                }

                // 2. If NTEGlobalGame is not running, start it.
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

                // 3. Wait until the NTEGlobalGame window is clearly displayed.
                Log("Build: Waiting for NTEGlobalGame window to appear...");
                int waitCount = 0;
                // Wait for a maximum of 30 seconds (60 times x 500ms)
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

                // Start of speech conversion and Pak formatting process.
                await audioService.BuildModAsync(targets);
            }
            finally
            {
                // Once the process is complete (or interrupted due to an error), be sure to turn the flag OFF.
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
            // Blocks termination during the build process.
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

        //Utility
        private void Log(string m) =>
            Dispatcher.Invoke(() =>
            {
                TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {m}\n";
                LogScroller.ScrollToEnd();
            });
    }
}
