using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using YoutubeExplode;
using YoutubeExplode.Converter;

namespace NtePakTool
{
    public class AudioService
    {
        private readonly string wprojPath;
        private readonly string wwiseConsolePath;
        private readonly string unpackDir;
        private readonly Action<string> log;
        private readonly GameService gameService;

        public AudioService(string wwiseConsolePath, string unpackDir, GameService gameService, Action<string> log)
        {
            this.wwiseConsolePath = wwiseConsolePath;
            this.unpackDir = unpackDir;
            this.gameService = gameService;
            this.log = log;
            this.wprojPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wwise", "NTE.wproj");
        }

        // Stores the PatchPak mod size from the last BuildModAsync call
        public long LastPatchModSize { get; private set; } = 0;
        private long _lastPatchModSize = 0;

        // WEM list operations
        public void RefreshList(ObservableCollection<WemMapItem> wemList)
        {
            if (!Directory.Exists(unpackDir)) return;
            wemList.Clear();
            var files = Directory.GetFiles(unpackDir, "*.wem", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                wemList.Add(new WemMapItem
                {
                    RelativePath = f.Substring(unpackDir.Length).TrimStart('\\'),
                    FullPath = f
                });
            }
            log($"Found {wemList.Count} total .wem files.");
        }

        public void LoadUiConfig(ObservableCollection<UiWemItem> uiWemList)
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_wem_config.json");
            string imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
            if (!Directory.Exists(imgDir)) Directory.CreateDirectory(imgDir);

            if (!File.Exists(jsonPath))
            {
                var sample = new[]
                {
                    new UiConfigJson { WemName = "example_12345.wem", DisplayName = "Main Theme (Sample)", ImageName = "main_theme.png" }
                };
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(sample,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            try
            {
                string jsonString = File.ReadAllText(jsonPath);
                var items = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonString);
                uiWemList.Clear();
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        uiWemList.Add(new UiWemItem
                        {
                            RelativePath = item.WemName,
                            FullPath = Path.Combine(unpackDir, item.WemName),
                            DisplayName = item.DisplayName,
                            ImagePath = Path.Combine(imgDir, item.ImageName)
                        });
                    }
                }
            }
            catch (Exception ex) { log("Error loading UI config: " + ex.Message); }
        }

        // Audio conversion
        public async Task<string> ConvertToWavAsync(string inputPath)
        {
            string ffmpegLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            string ffmpegExe = File.Exists(ffmpegLocal) ? ffmpegLocal : "ffmpeg";

            string cacheDir = Path.Combine(Path.GetTempPath(), "NteWavCache");
            Directory.CreateDirectory(cacheDir);

            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            string outputWav = Path.Combine(cacheDir, baseName + "_" + Math.Abs(inputPath.GetHashCode()) + ".wav");

            if (File.Exists(outputWav)) return outputWav;

            var tcs = new TaskCompletionSource<bool>();
            var psi = new ProcessStartInfo(ffmpegExe,
                $"-y -i \"{inputPath}\" -ar 44100 -ac 2 -sample_fmt s16 \"{outputWav}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var errorOutput = new StringBuilder();
            p.ErrorDataReceived += (s, ev) => { if (ev.Data != null) errorOutput.AppendLine(ev.Data); };
            p.Exited += (s, ev) =>
            {
                tcs.SetResult(p.ExitCode == 0);
                p.Dispose();
            };

            p.Start();
            p.BeginErrorReadLine();
            bool success = await tcs.Task;

            if (!success || !File.Exists(outputWav))
                throw new Exception($"ffmpeg exited with error:\n{errorOutput}");

            return outputWav;
        }

        // YouTube audio download
        public async Task<string> DownloadFromYouTubeAsync(string videoUrl)
        {
            string ffmpegLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpegLocal))
                throw new FileNotFoundException(
                    $"ffmpeg.exe could not be found. Please place it in the following location:\n{ffmpegLocal}");

            string cacheDir = Path.Combine(Path.GetTempPath(), "NteWavCache");
            Directory.CreateDirectory(cacheDir);

            var youtube = new YoutubeClient();

            log("YouTube: Retrieving video information...");
            var video = await youtube.Videos.GetAsync(videoUrl);
            log($"YouTube: Title: {video.Title} / Duration: {video.Duration}");

            // Use URL hash as filename and reuse if cached
            string safeId = Math.Abs(videoUrl.GetHashCode()).ToString();
            string outputWav = Path.Combine(cacheDir, $"yt_{safeId}.wav");

            if (!File.Exists(outputWav))
            {
                log("YouTube: Downloading audio and converting to WAV...");
                await youtube.Videos.DownloadAsync(videoUrl, outputWav, builder => builder
                    .SetFFmpegPath(ffmpegLocal)
                    .SetPreset(ConversionPreset.UltraFast));
                log($"YouTube: Conversion complete → {Path.GetFileName(outputWav)}");
            }
            else
            {
                log($"YouTube: Using cached file → {Path.GetFileName(outputWav)}");
            }

            return outputWav;
        }
        // MOD build
        // sym_update : save mod size when mod build.
        #region origin code

        // public async Task BuildModAsync(List<WemItemBase> targets)
        // {
        //     string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        //     string wemBackupDir = Path.Combine(exeDir, "WemTempBak_" + DateTime.Now.Ticks);
        //     var backupMap = new Dictionary<string, string>();
        //
        //     try
        //     {
        //         if (!File.Exists(wwiseConsolePath))
        //         {
        //             try
        //             {
        //                 Process.Start(new ProcessStartInfo
        //                 {
        //                     FileName = "https://www.audiokinetic.com/en/download-launcher/?platform=1",
        //                     UseShellExecute = true
        //                 });
        //             }
        //             catch (Exception ex) { log("Browser failed to launch: " + ex.Message); }
        //
        //             System.Windows.MessageBox.Show(
        //                 "WwiseConsole.exe could not be found.\n\n" +
        //                 "The Wwise Launcher download page has been opened automatically.\n" +
        //                 "Please run the downloaded installer (exe) and\n" +
        //                 "install version [ 2025.1.7.9143 ].",
        //                 "Wwise Installation Required",
        //                 MessageBoxButton.OK,
        //                 MessageBoxImage.Information);
        //             return;
        //         }
        //
        //         log($"Build started: Converting {targets.Count} files...");
        //
        //         string tempDir = Path.Combine(Path.GetTempPath(), "NteWemTemp");
        //         string audioWorkDir = Path.Combine(Path.GetTempPath(), "AudioWork");
        //         if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        //         if (Directory.Exists(audioWorkDir)) Directory.Delete(audioWorkDir, true);
        //         Directory.CreateDirectory(tempDir);
        //         Directory.CreateDirectory(audioWorkDir);
        //
        //         var xml = new StringBuilder(
        //             "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        //             "<ExternalSourcesList SchemaVersion=\"1\" Root=\"" + audioWorkDir + "\">\n");
        //
        //         foreach (var t in targets)
        //         {
        //             string targetName = Path.GetFileNameWithoutExtension(t.FullPath);
        //             string renamedSource = Path.Combine(audioWorkDir, targetName + Path.GetExtension(t.SourceAudioPath));
        //             File.Copy(t.SourceAudioPath, renamedSource, true);
        //             xml.AppendLine($"<Source Path=\"{Path.GetFileName(renamedSource)}\" Conversion=\"Vorbis Quality High\"/>");
        //         }
        //         xml.AppendLine("</ExternalSourcesList>");
        //
        //         string wsources = Path.Combine(Path.GetTempPath(), "list.wsources");
        //         File.WriteAllText(wsources, xml.ToString(), Encoding.UTF8);
        //
        //         await gameService.RunProcessAsync(wwiseConsolePath,
        //             $"convert-external-source \"{wprojPath}\" --source-file \"{wsources}\" --output \"{tempDir}\"");
        //
        //         string windowsDir = Path.Combine(tempDir, "Windows");
        //         if (Directory.Exists(windowsDir))
        //         {
        //             foreach (var file in Directory.GetFiles(windowsDir))
        //             {
        //                 string destFile = Path.Combine(tempDir, Path.GetFileName(file));
        //                 if (File.Exists(destFile)) File.Delete(destFile);
        //                 File.Move(file, destFile);
        //             }
        //             Directory.Delete(windowsDir, true);
        //         }
        //
        //         foreach (var t in targets)
        //         {
        //             string expectedWem = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(t.FullPath) + ".wem");
        //             if (!File.Exists(expectedWem))
        //                 throw new Exception($"File '{Path.GetFileName(t.FullPath)}' WEM conversion failed.");
        //         }
        //
        //         log("Temporarily backing up the original WEM files...");
        //         Directory.CreateDirectory(wemBackupDir);
        //         foreach (var t in targets)
        //         {
        //             if (File.Exists(t.FullPath))
        //             {
        //                 string bkpPath = Path.Combine(wemBackupDir, Guid.NewGuid().ToString() + "_" + Path.GetFileName(t.FullPath));
        //                 File.Move(t.FullPath, bkpPath);
        //                 backupMap.Add(t.FullPath, bkpPath);
        //             }
        //         }
        //
        //         log("The MOD's WEM files are placed in the correct location.");
        //         foreach (var t in targets)
        //         {
        //             string modWemPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(t.FullPath) + ".wem");
        //             if (File.Exists(modWemPath)) File.Copy(modWemPath, t.FullPath, true);
        //         }
        //
        //         var strayWems = Directory.GetFiles(unpackDir, "*.wem", SearchOption.TopDirectoryOnly);
        //         foreach (var stray in strayWems) { try { File.Delete(stray); } catch { } }
        //
        //         log("The Pak conversion process will begin...");
        //         string base64Key = await gameService.GetAesKeyBase64Async();
        //         await gameService.PackingProcess(base64Key);
        //
        //         log("Build complete!");
        //         System.Windows.MessageBox.Show("Pak build complete!\nIf Auto-Save is ON, it will be applied automatically next time.");
        //     }
        //     catch (Exception ex)
        //     {
        //         log("Error: " + ex.Message);
        //         System.Windows.MessageBox.Show($"An error occurred during the build:\n{ex.Message}\nRestoring original files.");
        //     }
        //     finally
        //     {
        //         if (Directory.Exists(wemBackupDir))
        //         {
        //             log("Deleting MOD files and restoring original files...");
        //             foreach (var kvp in backupMap)
        //             {
        //                 string originalLocation = kvp.Key;
        //                 string backupLocation = kvp.Value;
        //                 bool restoreSuccess = false;
        //                 int retryCount = 0;
        //
        //                 while (!restoreSuccess && retryCount < 10)
        //                 {
        //                     try
        //                     {
        //                         if (File.Exists(originalLocation)) File.Delete(originalLocation);
        //                         if (File.Exists(backupLocation)) File.Move(backupLocation, originalLocation);
        //                         restoreSuccess = true;
        //                     }
        //                     catch (IOException)
        //                     {
        //                         retryCount++;
        //                         await Task.Delay(500);
        //                     }
        //                 }
        //             }
        //             try { Directory.Delete(wemBackupDir, true); } catch { }
        //             log("Restoration process complete. The Unpak folder is now clean.");
        //         }
        //     }
        // }

        #endregion
        public async Task<long> BuildModAsync(List<WemItemBase> targets)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string wemBackupDir = Path.Combine(exeDir, "WemTempBak_" + DateTime.Now.Ticks);
            var backupMap = new Dictionary<string, string>();

            try
            {
                if (!File.Exists(wwiseConsolePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://www.audiokinetic.com/en/download-launcher/?platform=1",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { log("Browser failed to launch: " + ex.Message); }

                    System.Windows.MessageBox.Show(
                        "WwiseConsole.exe could not be found.\n\n" +
                        "The Wwise Launcher download page has been opened automatically.\n" +
                        "Please run the downloaded installer (exe) and\n" +
                        "install version [ 2025.1.7.9143 ].",
                        "Wwise Installation Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return 0;
                }

                log($"Build started: Converting {targets.Count} files...");

                string tempDir = Path.Combine(Path.GetTempPath(), "NteWemTemp");
                string audioWorkDir = Path.Combine(Path.GetTempPath(), "AudioWork");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                if (Directory.Exists(audioWorkDir)) Directory.Delete(audioWorkDir, true);
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(audioWorkDir);

                var xml = new StringBuilder(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<ExternalSourcesList SchemaVersion=\"1\" Root=\"" + audioWorkDir + "\">\n");

                foreach (var t in targets)
                {
                    string targetName = Path.GetFileNameWithoutExtension(t.FullPath);
                    string renamedSource = Path.Combine(audioWorkDir, targetName + Path.GetExtension(t.SourceAudioPath));
                    File.Copy(t.SourceAudioPath, renamedSource, true);
                    xml.AppendLine($"<Source Path=\"{Path.GetFileName(renamedSource)}\" Conversion=\"Vorbis Quality High\"/>");
                }
                xml.AppendLine("</ExternalSourcesList>");

                string wsources = Path.Combine(Path.GetTempPath(), "list.wsources");
                File.WriteAllText(wsources, xml.ToString(), Encoding.UTF8);

                await gameService.RunProcessAsync(wwiseConsolePath,
                    $"convert-external-source \"{wprojPath}\" --source-file \"{wsources}\" --output \"{tempDir}\"");

                string windowsDir = Path.Combine(tempDir, "Windows");
                if (Directory.Exists(windowsDir))
                {
                    foreach (var file in Directory.GetFiles(windowsDir))
                    {
                        string destFile = Path.Combine(tempDir, Path.GetFileName(file));
                        if (File.Exists(destFile)) File.Delete(destFile);
                        File.Move(file, destFile);
                    }
                    Directory.Delete(windowsDir, true);
                }

                foreach (var t in targets)
                {
                    string expectedWem = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(t.FullPath) + ".wem");
                    if (!File.Exists(expectedWem))
                        throw new Exception($"File '{Path.GetFileName(t.FullPath)}' WEM conversion failed.");
                }

                log("Temporarily backing up the original WEM files...");
                Directory.CreateDirectory(wemBackupDir);
                foreach (var t in targets)
                {
                    if (File.Exists(t.FullPath))
                    {
                        string bkpPath = Path.Combine(wemBackupDir, Guid.NewGuid().ToString() + "_" + Path.GetFileName(t.FullPath));
                        File.Move(t.FullPath, bkpPath);
                        backupMap.Add(t.FullPath, bkpPath);
                    }
                }

                log("The MOD's WEM files are placed in the correct location.");
                foreach (var t in targets)
                {
                    string modWemPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(t.FullPath) + ".wem");
                    if (File.Exists(modWemPath)) File.Copy(modWemPath, t.FullPath, true);
                }

                var strayWems = Directory.GetFiles(unpackDir, "*.wem", SearchOption.TopDirectoryOnly);
                foreach (var stray in strayWems) { try { File.Delete(stray); } catch { } }

                log("The Pak conversion process will begin...");
                string base64Key = await gameService.GetAesKeyBase64Async();
                // [Fix] return modSize to MainWindow
                long modSize = await gameService.PackingProcess(base64Key);

                // ── PatchPak BKN duration patch + pakchunk3-Windows_0_P.pak build ──
                long patchModSize = await BuildPatchPakAsync(targets, base64Key);

                log("Build complete!");
                System.Windows.MessageBox.Show("Pak build complete!\nIf Auto-Save is ON, it will be applied automatically next time.");
                // Return both sizes packed as a tuple via out-of-band field (stored before return)
                _lastPatchModSize = patchModSize;
                LastPatchModSize = _lastPatchModSize;
                return modSize;
            }
            catch (Exception ex)
            {
                log("Error: " + ex.Message);
                System.Windows.MessageBox.Show($"An error occurred during the build:\n{ex.Message}\nRestoring original files.");
            }
            finally
            {
                if (Directory.Exists(wemBackupDir))
                {
                    log("Deleting MOD files and restoring original files...");
                    foreach (var kvp in backupMap)
                    {
                        string originalLocation = kvp.Key;
                        string backupLocation = kvp.Value;
                        bool restoreSuccess = false;
                        int retryCount = 0;

                        while (!restoreSuccess && retryCount < 10)
                        {
                            try
                            {
                                if (File.Exists(originalLocation)) File.Delete(originalLocation);
                                if (File.Exists(backupLocation)) File.Move(backupLocation, originalLocation);
                                restoreSuccess = true;
                            }
                            catch (IOException)
                            {
                                retryCount++;
                                await Task.Delay(500);
                            }
                        }
                    }
                    try { Directory.Delete(wemBackupDir, true); } catch { }
                    log("Restoration process complete. The Unpak folder is now clean.");
                }
            }
            return 0;
        }

        // ── PatchPak: patch BKN duration and build pakchunk3-Windows_0_P.pak ────────
        private async Task<long> BuildPatchPakAsync(List<WemItemBase> targets, string base64Key)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string unpak1Dir = Path.Combine(exeDir, "Unpak1", "HT");
            string eventDir = Path.Combine(unpak1Dir, "Content", "WwiseAudio", "Event");
            string baseEventDir = Path.Combine(exeDir, "Unpak", "HT", "Content", "WwiseAudio", "Event");

            if (!Directory.Exists(eventDir))
            {
                Directory.CreateDirectory(eventDir);
            }

            string jsonPath = Path.Combine(exeDir, "ui_wem_config.json");
            List<UiConfigJson>? uiConfig = null;
            try
            {
                if (File.Exists(jsonPath))
                {
                    string jsonStr = File.ReadAllText(jsonPath);
                    uiConfig = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonStr);
                    log($"PatchPak: Loaded ui_wem_config.json successfully. Found {uiConfig?.Count ?? 0} entries.");
                }
                else
                {
                    log("PatchPak: [WARNING] ui_wem_config.json not found. BNK patch will be skipped.");
                }
            }
            catch (Exception ex) { log("PatchPak: Failed to load ui_wem_config.json: " + ex.Message); }

            var bnkBackupMap = new Dictionary<string, string>();
            var bnkToDelete = new List<string>();
            string bnkBackupDir = Path.Combine(exeDir, "BnkTempBak_" + DateTime.Now.Ticks);

            try
            {
                if (uiConfig != null)
                {
                    foreach (var t in targets)
                    {
                        string wemFileName = Path.GetFileName(t.RelativePath);
                        var cfg = uiConfig.FirstOrDefault(c =>
                            string.Equals(c.WemName, wemFileName, StringComparison.OrdinalIgnoreCase));

                        if (cfg == null)
                        {
                            log($"PatchPak: [SKIP] No UI config entry found for WEM: {wemFileName}");
                            continue;
                        }

                        if (string.IsNullOrEmpty(cfg.JsonName) || string.IsNullOrEmpty(cfg.DurationHex))
                        {
                            log($"PatchPak: [SKIP] JsonName or DurationHex is empty for WEM: {wemFileName}");
                            continue;
                        }

                        // JsonNameから拡張子を除いた名前に ".bnk" を付けて検索
                        string bnkBaseName = Path.GetFileNameWithoutExtension(cfg.JsonName);

                        string[] bnkFiles = Directory.GetFiles(eventDir, bnkBaseName + ".bnk", SearchOption.AllDirectories);
                        string? bnkPath = null;
                        bool isCopiedFromBase = false;

                        if (bnkFiles.Length > 0)
                        {
                            bnkPath = bnkFiles[0];
                        }
                        else
                        {
                            if (Directory.Exists(baseEventDir))
                            {
                                string[] baseBnkFiles = Directory.GetFiles(baseEventDir, bnkBaseName + ".bnk", SearchOption.AllDirectories);
                                if (baseBnkFiles.Length > 0)
                                {
                                    string sourceBnk = baseBnkFiles[0];
                                    string relPath = sourceBnk.Substring(baseEventDir.Length).TrimStart('\\', '/');
                                    bnkPath = Path.Combine(eventDir, relPath);

                                    Directory.CreateDirectory(Path.GetDirectoryName(bnkPath)!);
                                    File.Copy(sourceBnk, bnkPath, true);
                                    isCopiedFromBase = true;
                                    log($"PatchPak: Found BNK in base Unpak. Copied to Unpak1 for patching: {bnkBaseName}.bnk");
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(bnkPath))
                        {
                            log($"PatchPak: [FAIL] BNK file not found in Unpak1 OR Unpak for {cfg.JsonName} (Searched: {bnkBaseName}.bnk). Skipping.");
                            continue;
                        }

                        string[] hexParts = cfg.DurationHex.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (hexParts.Length != 8)
                        {
                            log($"PatchPak: [FAIL] DurationHex for {cfg.WemName} is not exactly 8 bytes. Value: '{cfg.DurationHex}'. Skipping.");
                            continue;
                        }
                        byte[] searchBytes = hexParts.Select(h => Convert.ToByte(h, 16)).ToArray();

                        byte[]? newDurationBytes = GetWavDurationAsFloat64LE(t.SourceAudioPath);
                        if (newDurationBytes == null)
                        {
                            log($"PatchPak: [FAIL] Could not read WAV duration or invalid RIFF header for {t.SourceAudioPath}. Skipping BNK patch.");
                            continue;
                        }

                        if (isCopiedFromBase)
                        {
                            bnkToDelete.Add(bnkPath);
                        }
                        else
                        {
                            if (!Directory.Exists(bnkBackupDir)) Directory.CreateDirectory(bnkBackupDir);
                            string bkpPath = Path.Combine(bnkBackupDir, Guid.NewGuid().ToString() + "_" + Path.GetFileName(bnkPath));
                            File.Copy(bnkPath, bkpPath, true);
                            bnkBackupMap[bnkPath] = bkpPath;
                        }

                        byte[] bnkData = File.ReadAllBytes(bnkPath);
                        int patchCount = 0;
                        for (int i = 0; i <= bnkData.Length - 8; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < 8; j++)
                            {
                                if (bnkData[i + j] != searchBytes[j]) { match = false; break; }
                            }
                            if (match)
                            {
                                Array.Copy(newDurationBytes, 0, bnkData, i, 8);
                                patchCount++;
                            }
                        }
                        File.WriteAllBytes(bnkPath, bnkData);

                        if (patchCount == 0)
                        {
                            log($"PatchPak: [WARNING] DurationHex ({cfg.DurationHex}) was NOT found inside [{Path.GetFileName(bnkPath)}]. 0 occurrences replaced! Value remains default.");
                        }
                        else
                        {
                            log($"PatchPak: [SUCCESS] Patched BNK [{Path.GetFileName(bnkPath)}] duration ({patchCount} occurrence(s) replaced).");
                        }
                    }
                }

                log("PatchPak: Starting pakchunk3-Windows_0_P.pak build...");
                long patchModSize = await gameService.PackingProcessPatchPak(base64Key, unpak1Dir);
                return patchModSize;
            }
            finally
            {
                if (bnkBackupMap.Count > 0)
                {
                    log("PatchPak: Restoring original BNK files in Unpak1 for cleanliness...");
                    foreach (var kvp in bnkBackupMap)
                    {
                        string orig = kvp.Key;
                        string bkp = kvp.Value;
                        bool restored = false;
                        int retry = 0;
                        while (!restored && retry < 10)
                        {
                            try
                            {
                                if (File.Exists(orig)) File.Delete(orig);
                                if (File.Exists(bkp)) File.Move(bkp, orig);
                                restored = true;
                            }
                            catch (IOException) { retry++; await Task.Delay(500); }
                        }
                    }
                    try { Directory.Delete(bnkBackupDir, true); } catch { }
                    log("PatchPak: BNK restoration complete.");
                }

                if (bnkToDelete.Count > 0)
                {
                    foreach (var file in bnkToDelete)
                    {
                        try { if (File.Exists(file)) File.Delete(file); } catch { }
                    }
                    log("PatchPak: Cleaned up temporary BNK files copied from base Unpak.");
                }
            }
        }

        // Read WAV duration in seconds and return as float64 (double) LE bytes
        private static byte[]? GetWavDurationAsFloat64LE(string wavPath)
        {
            try
            {
                using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                // RIFF header
                string riff = new string(br.ReadChars(4));
                if (riff != "RIFF") return null;
                br.ReadInt32(); // file size
                string wave = new string(br.ReadChars(4));
                if (wave != "WAVE") return null;

                int sampleRate = 0, numChannels = 0, bitsPerSample = 0;
                long dataSize = 0;

                while (fs.Position < fs.Length - 8)
                {
                    string chunkId = new string(br.ReadChars(4));
                    int chunkSize = br.ReadInt32();
                    long chunkEnd = fs.Position + chunkSize;

                    if (chunkId == "fmt ")
                    {
                        br.ReadInt16(); // audio format
                        numChannels = br.ReadInt16();
                        sampleRate = br.ReadInt32();
                        br.ReadInt32(); // byte rate
                        br.ReadInt16(); // block align
                        bitsPerSample = br.ReadInt16();
                        fs.Seek(chunkEnd, SeekOrigin.Begin);
                    }
                    else if (chunkId == "data")
                    {
                        dataSize = chunkSize;
                        break;
                    }
                    else
                    {
                        fs.Seek(chunkEnd, SeekOrigin.Begin);
                    }
                }

                if (sampleRate == 0 || numChannels == 0 || bitsPerSample == 0) return null;

                long totalSamples = dataSize / (numChannels * (bitsPerSample / 8));

                // [修正] 秒(Seconds)ではなく、ミリ秒(Milliseconds)に変換する
                double durationMilliseconds = ((double)totalSamples / sampleRate) * 1000.0;

                return BitConverter.GetBytes(durationMilliseconds); // float64 LE
            }
            catch { return null; }
        }
    }
}

