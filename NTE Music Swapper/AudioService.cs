using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private readonly string retocPath;
        private const string AesJsonUrl = "https://github.com/Landmark1218/Trash/raw/refs/heads/main/AES.json";

        private sealed record MusicMetadata(string JsonName, string DisplayName);
        private sealed record TitlePatchTarget(string TablePackage, string Key, string CustomName, string JsonName);
        private sealed record MusicTitlePatchMapping(string JsonName, string TablePackage, string Key, string? DisplayName);
        private Dictionary<uint, List<string>>? bnkReferenceCache;

        private async Task WaitForFileUnlockAsync(string filePath, int timeoutMs = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    await Task.Delay(500);
                }
            }
            log("Warning: Timed out waiting for FFmpeg to release the file. Duration might be inaccurate.");
        }

        public AudioService(string wwiseConsolePath, string unpackDir, GameService gameService, Action<string> log, string retocPath = "")
        {
            this.wwiseConsolePath = wwiseConsolePath;
            this.unpackDir = unpackDir;
            this.gameService = gameService;
            this.log = log;
            this.retocPath = retocPath;
            this.wprojPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wwise", "NTE.wproj");
        }

        public long LastPatchModSize { get; private set; } = 0;
        private long _lastPatchModSize = 0;

        // WEM list operations
        public void RefreshList(ObservableCollection<WemMapItem> wemList)
        {
            wemList.Clear();
            foreach (var root in GetUnpackRoots())
            {
                var files = Directory.GetFiles(root.Path, "*.wem", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    string rel = f.Substring(root.Path.Length).TrimStart('\\', '/');
                    wemList.Add(new WemMapItem
                    {
                        RelativePath = root.Label == "Unpak" ? rel : Path.Combine(root.Label, rel),
                        FullPath = f
                    });
                }
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
                    new UiConfigJson { WemName = "example_12345.wem", DisplayName = "Main Theme (Sample)", CustomName = "Main Theme (Sample)", ImageName = "main_theme.png" }
                };
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(sample,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            try
            {
                string jsonString = File.ReadAllText(jsonPath);
                var items = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonString);
                uiWemList.Clear();
                // sym_update : Add LoadPath func
                #region origin code

                // if (items != null)
                // {
                //     foreach (var item in items)
                //     {
                //         uiWemList.Add(new UiWemItem
                //         {
                //             RelativePath = item.WemName,
                //             FullPath = Path.Combine(unpackDir, item.WemName),
                //             DisplayName = item.DisplayName,
                //             ImagePath = Path.Combine(imgDir, item.ImageName)
                //         });
                //     }
                // }

                #endregion
                bool needsSave = false; // 파일이 없어져서 JSON을 수정했는지 체크
                // [Fix] 원본 텍스트에 "SavedPath"라는 단어가 아예 없다면
                if (!jsonString.Contains("\"SavedPath\""))
                {
                    needsSave = true;
                }
                if (!jsonString.Contains("\"CustomName\""))
                {
                    needsSave = true;
                }
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.CustomName))
                        {
                            item.CustomName = item.DisplayName;
                            needsSave = true;
                        }

                        // 저장된 경로가 있고 파일이 진짜 존재하는지 검사
                        string sourcePath = "No file selected";
                        bool isSelected = false;

                        if (!string.IsNullOrEmpty(item.SavedPath))
                        {
                            if (File.Exists(item.SavedPath))
                            {
                                sourcePath = item.SavedPath; // 파일이 있으면 그 경로를 그대로 할당
                                isSelected = true;           // 체크박스도 자동으로 ON
                            }
                            else
                            {
                                item.SavedPath = "";         // 파일이 사라졌다면 쿨하게 초기화
                                needsSave = true;            // JSON 다시 저장해야 한다고 플래그 세움
                            }
                        }

                        uiWemList.Add(new UiWemItem
                        {
                            RelativePath = item.WemName,
                            FullPath = ResolveWemFullPath(item.WemName) ?? Path.Combine(unpackDir, item.WemName),
                            DisplayName = item.DisplayName,
                            CustomName = item.CustomName,
                            ImagePath = Path.Combine(imgDir, item.ImageName),
                            SourceAudioPath = sourcePath,    // 찾은 경로 꽂아주기
                            IsSelected = isSelected          // 상태 꽂아주기
                        });
                    }

                    // 파일 유실 등으로 값이 비워진 녀석이 있다면 즉시 JSON 덮어쓰기
                    if (needsSave)
                    {
                        SaveUiConfig(items);
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
            // sym_update : Fixed a bug where already downloaded YouTube songs could not be retrieved using hash values.
            //string outputWav = Path.Combine(cacheDir, baseName + "_" + Math.Abs(inputPath.GetHashCode()) + ".wav");
            string outputWav = Path.Combine(cacheDir, baseName + "_" + GetStableStringHash(inputPath) + ".wav");

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

            // sym_update : Fixed a bug where already downloaded YouTube songs could not be retrieved using hash values.
            //string safeId = Math.Abs(videoUrl.GetHashCode()).ToString();
            //string safeId = GetStableStringHash(videoUrl);
            
            // sym_update : Fixed a bug where the hash value occasionally didn't match due to URL parameters (like ?si=), by directly using the YouTube 11-character unique ID instead of a hash.
            // [Fix] YoutubeExplode가 분석해둔 '11자리 고유 ID'만 쏙 빼서 씁니다.
            // 링크 뒤에 ?si=어쩌구 가 아무리 붙어도 고유 ID는 절대 변하지 않습니다.
            string safeId = video.Id.Value;
            string outputWav = Path.Combine(cacheDir, $"yt_{safeId}.wav");

            if (!File.Exists(outputWav))
            {
                log("YouTube: Downloading audio and converting to WAV...");
                await youtube.Videos.DownloadAsync(videoUrl, outputWav, builder => builder
                    .SetFFmpegPath(ffmpegLocal)
                    .SetPreset(ConversionPreset.UltraFast));
                log($"YouTube: Conversion complete → {Path.GetFileName(outputWav)}");

                log("YouTube: Verifying file integrity and waiting for unlock...");
                await WaitForFileUnlockAsync(outputWav);
            }
            else
            {
                log($"YouTube: Using cached file → {Path.GetFileName(outputWav)}");
            }

            return outputWav;
        }

        // MOD build
        #region origin code

        // public async Task BuildModAsync(List<WemItemBase> targets)
        // {
        //     (省略 - 旧コード)
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
                long modSize = await gameService.PackingProcess(base64Key);

                long patchModSize = await BuildPatchPakAsync(targets, base64Key);

                log("Build complete!");
                System.Windows.MessageBox.Show("Pak build complete!\nIf Auto-Save is ON, it will be applied automatically next time.");
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

        // ── PatchPak: CUE4Parse で BNK を解析し Duration フィールドを書き換えてビルド ──
        private async Task<long> BuildPatchPakAsync(List<WemItemBase> targets, string base64Key)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string unpak1Dir = Path.Combine(exeDir, "Unpak1", "HT");
            string eventDir = Path.Combine(unpak1Dir, "Content", "WwiseAudio", "Event");
            string baseEventDir = Path.Combine(exeDir, "Unpak", "HT", "Content", "WwiseAudio", "Event");

            if (!Directory.Exists(eventDir))
                Directory.CreateDirectory(eventDir);

            string jsonPath = Path.Combine(exeDir, "ui_wem_config.json");
            List<UiConfigJson>? uiConfig = null;
            try
            {
                if (File.Exists(jsonPath))
                {
                    string jsonStr = File.ReadAllText(jsonPath);
                    uiConfig = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonStr);
                    log($"PatchPak: Loaded ui_wem_config.json ({jsonPath}). Found {uiConfig?.Count ?? 0} entries.");
                }
                else
                {
                    log($"PatchPak: [WARNING] ui_wem_config.json not found at {jsonPath}. BNK patch will be skipped.");
                }
            }
            catch (Exception ex) { log("PatchPak: Failed to load ui_wem_config.json: " + ex.Message); }

            var bnkBackupMap = new Dictionary<string, string>();
            var bnkToDelete = new List<string>();
            string bnkBackupDir = Path.Combine(exeDir, "BnkTempBak_" + DateTime.Now.Ticks);
            var titlePatchBackupMap = new Dictionary<string, string?>();
            string titlePatchBackupDir = Path.Combine(exeDir, "TitleTempBak_" + DateTime.Now.Ticks);

            try
            {
                if (uiConfig != null)
                {
                    foreach (var t in targets)
                    {
                        string wemFileName = Path.GetFileName(t.RelativePath);
                        log($"PatchPak: ── Processing WEM: {wemFileName} ──");

                        var cfg = uiConfig.FirstOrDefault(c =>
                            string.Equals(c.WemName, wemFileName, StringComparison.OrdinalIgnoreCase));

                        if (cfg == null)
                        {
                            log($"PatchPak: [SKIP] No UI config entry found for WEM: {wemFileName}");
                            continue;
                        }

                        if (string.IsNullOrEmpty(cfg.JsonName))
                        {
                            log($"PatchPak: [SKIP] JsonName is empty for WEM: {wemFileName}");
                            continue;
                        }

                        // JsonName から .bnk のパスを解決
                        string bnkBaseName = Path.GetFileNameWithoutExtension(cfg.JsonName);
                        string? bnkPath = null;
                        bool isCopiedFromBase = false;

                        log($"PatchPak: Searching for BNK [{bnkBaseName}.bnk] in {eventDir}");
                        var bnkFiles = Directory.GetFiles(eventDir, bnkBaseName + ".bnk", SearchOption.AllDirectories);
                        if (bnkFiles.Length > 0)
                        {
                            bnkPath = bnkFiles[0];
                            log($"PatchPak: Found BNK in Unpak1: {bnkPath}");
                        }
                        else if (Directory.Exists(baseEventDir))
                        {
                            log($"PatchPak: Not found in Unpak1. Searching base Unpak: {baseEventDir}");
                            var baseBnkFiles = Directory.GetFiles(baseEventDir, bnkBaseName + ".bnk", SearchOption.AllDirectories);
                            if (baseBnkFiles.Length > 0)
                            {
                                string sourceBnk = baseBnkFiles[0];
                                string relPath = sourceBnk.Substring(baseEventDir.Length).TrimStart('\\', '/');
                                bnkPath = Path.Combine(eventDir, relPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(bnkPath)!);
                                File.Copy(sourceBnk, bnkPath, true);
                                isCopiedFromBase = true;
                                log($"PatchPak: Found BNK in base Unpak. Copied to Unpak1: {bnkPath}");
                            }
                        }

                        if (string.IsNullOrEmpty(bnkPath))
                        {
                            log($"PatchPak: [FAIL] BNK not found anywhere (searched: {bnkBaseName}.bnk). Skipping.");
                            continue;
                        }

                        // WAV ファイルの長さをミリ秒 (double) で取得
                        log($"PatchPak: Reading WAV duration from: {t.SourceAudioPath}");
                        byte[]? newDurationBytes = GetWavDurationAsFloat64LE(t.SourceAudioPath);
                        if (newDurationBytes == null)
                        {
                            log($"PatchPak: [FAIL] Could not read WAV duration from {t.SourceAudioPath}. Skipping BNK patch.");
                            continue;
                        }
                        double newDurationMs = BitConverter.ToDouble(newDurationBytes, 0);
                        log($"PatchPak: WAV duration = {newDurationMs:F6} ms");

                        // CUE4Parse で BNK を解析して Duration 系フィールドの現在値を収集
                        byte[] bnkData = File.ReadAllBytes(bnkPath);
                        var durationValues = ExtractDurationValuesFromBnk(bnkData, bnkBaseName, bnkPath, log);

                        if (durationValues.Count == 0)
                        {
                            log($"PatchPak: [WARNING] No Duration fields found in [{bnkPath}]. Skipping patch.");
                            continue;
                        }

                        // バックアップ
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
                            log($"PatchPak: Backed up BNK to {bkpPath}");
                        }

                        // バイナリ内で収集した double 値をすべて新しい値に書き換える
                        int totalPatched = PatchDurationValuesInBnk(bnkData, durationValues, newDurationBytes, newDurationMs, log);
                        File.WriteAllBytes(bnkPath, bnkData);

                        if (totalPatched == 0)
                            log($"PatchPak: [WARNING] All Duration values were found by CUE4Parse but 0 bytes matched in binary. File unchanged: {bnkPath}");
                        else
                            log($"PatchPak: [SUCCESS] {bnkPath} → {totalPatched} byte location(s) patched to {newDurationMs:F6} ms.");
                    }
                }

                if (uiConfig != null)
                    await ApplyCustomRadioTitlePatchAsync(targets, uiConfig, unpak1Dir, titlePatchBackupMap, titlePatchBackupDir);

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
                        try { if (File.Exists(file)) File.Delete(file); } catch { }
                    log("PatchPak: Cleaned up temporary BNK files copied from base Unpak.");
                }

                if (titlePatchBackupMap.Count > 0)
                {
                    log("PatchPak: Restoring title patch staging files in Unpak1...");
                    foreach (var kvp in titlePatchBackupMap)
                    {
                        string stagedPath = kvp.Key;
                        string? backupPath = kvp.Value;
                        try
                        {
                            if (File.Exists(stagedPath))
                                File.Delete(stagedPath);
                            if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                                File.Move(backupPath, stagedPath);
                        }
                        catch (Exception ex)
                        {
                            log($"PatchPak: [WARNING] Failed to restore title patch file {stagedPath}: {ex.Message}");
                        }
                    }
                    try { Directory.Delete(titlePatchBackupDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// CUE4Parse で .bnk をパースし、キー名に "Duration" を含む全フィールドの
        /// (キー名, double値) ペアを収集して返す。
        /// </summary>
        private async Task ApplyCustomRadioTitlePatchAsync(
            List<WemItemBase> targets,
            List<UiConfigJson> uiConfig,
            string unpak1Dir,
            Dictionary<string, string?> backupMap,
            string backupDir)
        {
            var requestedConfigs = targets
                .Select(t => Path.GetFileName(t.RelativePath))
                .Select(wemName => uiConfig.FirstOrDefault(c => string.Equals(c.WemName, wemName, StringComparison.OrdinalIgnoreCase)))
                .Where(cfg => cfg != null)
                .Cast<UiConfigJson>()
                .Where(cfg => !string.IsNullOrWhiteSpace(cfg.JsonName) && !string.IsNullOrWhiteSpace(cfg.CustomName))
                .ToList();

            try
            {
                gameService.RemoveGeneratedTitleModsFromGame();
                gameService.ClearGeneratedTitleModCache();

                var requested = requestedConfigs
                    .Where(cfg => !string.Equals(
                        cfg.CustomName.Trim(),
                        (cfg.DisplayName ?? "").Trim(),
                        StringComparison.Ordinal))
                    .ToList();

                if (requested.Count == 0)
                {
                    log("TitlePatch: No CustomName changes selected. Previous generated title mods were removed.");
                    return;
                }

                var provider = await CreateGameAssetProviderAsync();
                var titleMappings = BuildMusicTitlePatchCache(provider, log);
                var patchTargets = new List<TitlePatchTarget>();

                foreach (var cfg in requested)
                {
                    var foundTargets = FindTitlePatchTargets(titleMappings, cfg, log);
                    if (foundTargets.Count == 0)
                    {
                        log($"TitlePatch: [SKIP] No MusicTitle string-table key found for {cfg.JsonName}.");
                        continue;
                    }

                    foreach (var target in foundTargets)
                    {
                        log($"TitlePatch: found title target {target.TablePackage} / {target.Key} ({target.JsonName})");
                        patchTargets.Add(target);
                    }
                }

                patchTargets = patchTargets
                    .GroupBy(t => $"{t.TablePackage}|{t.Key}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var group in patchTargets.GroupBy(t => t.TablePackage, StringComparer.OrdinalIgnoreCase))
                {
                    var packageFiles = provider.SavePackage(group.Key)
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                    bool packageChanged = false;

                    var patchFileOrder = packageFiles.Keys
                        .Where(path => !path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(path => path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(path => path)
                        .ToList();

                    if (patchFileOrder.Count == 0)
                    {
                        log($"TitlePatch: [WARNING] {group.Key} has no patchable payload file (.uexp/.ubulk). " +
                            "The game likely stores this StringTable in IoStore; a normal .pak override will not update it.");
                        if (await TryApplyTitleIoStoreWithRetocAsync(group.Key, group.ToList(), packageFiles))
                            continue;
                        log("TitlePatch: [WARNING] retoc title patch failed. Skipping title patch to avoid template title pollution.");
                        continue;
                    }

                    foreach (var target in group)
                    {
                        bool patched = false;
                        foreach (var filePath in patchFileOrder)
                        {
                            if (TryPatchStringTableValue(packageFiles[filePath], target.Key, target.CustomName, out var patchedBytes, out string detail))
                            {
                                int sizeDelta = patchedBytes.Length - packageFiles[filePath].Length;
                                packageFiles[filePath] = patchedBytes;
                                if (sizeDelta != 0)
                                    PatchCompanionUassetExportSize(packageFiles, filePath, sizeDelta, log);

                                log($"TitlePatch: [PATCH] {target.Key} -> \"{target.CustomName}\" ({target.JsonName}) in {filePath}");
                                patched = true;
                                packageChanged = true;
                                break;
                            }

                            if (!string.IsNullOrWhiteSpace(detail))
                                log($"TitlePatch: [SKIP] {target.Key}: {detail}");
                        }

                        if (!patched)
                            log($"TitlePatch: [MISS] Could not patch string-table key {target.Key} for {target.JsonName}.");
                    }

                    if (!packageChanged)
                        continue;

                    foreach (var kvp in packageFiles)
                    {
                        string stagedPath = ToUnpak1Path(unpak1Dir, kvp.Key);
                        StageFileBackup(stagedPath, backupMap, backupDir);
                        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                        File.WriteAllBytes(stagedPath, kvp.Value);
                        log($"TitlePatch: Staged {kvp.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                log($"TitlePatch: [WARNING] Failed to apply custom names: {ex.Message}");
            }
        }

        private async Task<DefaultFileProvider> CreateGameAssetProviderAsync()
        {
            string paksDir = FindGamePaksDir();
            if (string.IsNullOrEmpty(paksDir))
                throw new DirectoryNotFoundException("Game Paks folder not found.");

            var (aesKey, mappingUrl) = await FetchAesConfigAsync();
            string usmapPath = await DownloadUsmapAsync(mappingUrl);

            await OodleHelper.InitializeAsync();

            var provider = new DefaultFileProvider(
                paksDir,
                SearchOption.TopDirectoryOnly,
                new VersionContainer(EGame.GAME_UE5_6),
                StringComparer.OrdinalIgnoreCase);

            if (File.Exists(usmapPath))
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);

            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(aesKey));
            return provider;
        }

        private static List<TitlePatchTarget> FindTitlePatchTargets(
            IReadOnlyDictionary<string, List<MusicTitlePatchMapping>> titleMappings,
            UiConfigJson cfg,
            Action<string> log)
        {
            string jsonName = Path.GetFileNameWithoutExtension(cfg.JsonName);
            if (titleMappings.TryGetValue(jsonName, out var mappings) && mappings.Count > 0)
            {
                return mappings
                    .Select(mapping => new TitlePatchTarget(mapping.TablePackage, mapping.Key, cfg.CustomName, jsonName))
                    .GroupBy(t => $"{t.TablePackage}|{t.Key}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }

            log($"TitlePatch: direct radio title row not found for {jsonName}. Title patch skipped.");
            return [];
        }

        private static Dictionary<string, List<MusicTitlePatchMapping>> BuildMusicTitlePatchCache(
            DefaultFileProvider provider,
            Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            var result = new Dictionary<string, List<MusicTitlePatchMapping>>(StringComparer.OrdinalIgnoreCase);
            int rowCount = 0;
            int targetCount = 0;

            foreach (string tablePackage in GetDataTablePackages(provider, musicOnly: true))
            {
                try
                {
                    var exports = provider.LoadPackage(tablePackage).GetExports();
                    var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));

                    foreach (JObject row in FindMusicRowsWithTitles(root))
                    {
                        string? assetPath = row["MusicEvent"]?["AssetPathName"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(assetPath))
                            continue;

                        string jsonName = ExtractAssetObjectName(assetPath);
                        if (string.IsNullOrWhiteSpace(jsonName))
                            continue;

                        JToken? titleToken = row["MusicTitle"];
                        if (titleToken == null)
                            continue;

                        rowCount++;
                        string? displayName = FindFirstFTextString(titleToken);
                        foreach (var textRef in FindTextReferences(titleToken))
                        {
                            string stringTablePackage = ConvertGameObjectPathToPackagePath(textRef.TableId);
                            if (string.IsNullOrWhiteSpace(stringTablePackage))
                                continue;

                            if (!result.TryGetValue(jsonName, out var mappings))
                            {
                                mappings = [];
                                result[jsonName] = mappings;
                            }

                            mappings.Add(new MusicTitlePatchMapping(jsonName, stringTablePackage, textRef.Key, displayName));
                            targetCount++;
                        }
                    }
                }
                catch
                {
                    // Skip unreadable tables and keep building the cache.
                }
            }

            foreach (var key in result.Keys.ToList())
            {
                result[key] = result[key]
                    .GroupBy(m => $"{m.TablePackage}|{m.Key}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }

            log($"TitlePatch: Cached {targetCount} title target(s) from {rowCount} music row(s) in {sw.Elapsed.TotalSeconds:0.0}s.");
            return result;
        }

        private static List<string> GetDataTablePackages(DefaultFileProvider provider, bool musicOnly)
        {
            string prefix = musicOnly ? "HT/Content/DataTable/Music/" : "HT/Content/DataTable/";
            return provider.Files.Values
                .Select(file => file.Path)
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Select(path => path[..^".uasset".Length])
                .OrderBy(path => path.Equals("HT/Content/DataTable/Music/DT_PlayerMusic", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path)
                .ToList();
        }

        private static List<TitlePatchTarget> FindDirectMusicTitlePatchTargets(
            DefaultFileProvider provider,
            UiConfigJson cfg,
            string jsonName,
            string eventAssetPath,
            string eventName)
        {
            var result = new List<TitlePatchTarget>();
            foreach (string tablePackage in GetDataTablePackages(provider, musicOnly: true))
            {
                try
                {
                    var exports = provider.LoadPackage(tablePackage).GetExports();
                    var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));

                    foreach (JObject row in FindMusicRowsByEventPath(root, eventAssetPath, eventName))
                    {
                        JToken? titleToken = row["MusicTitle"];
                        if (titleToken == null)
                            continue;

                        foreach (var textRef in FindTextReferences(titleToken))
                        {
                            string stringTablePackage = ConvertGameObjectPathToPackagePath(textRef.TableId);
                            if (string.IsNullOrWhiteSpace(stringTablePackage))
                                continue;

                            result.Add(new TitlePatchTarget(stringTablePackage, textRef.Key, cfg.CustomName, jsonName));
                        }
                    }
                }
                catch
                {
                    // Skip unreadable tables and keep searching.
                }
            }

            return result
                .GroupBy(t => $"{t.TablePackage}|{t.Key}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static List<TitlePatchTarget> FindFallbackTitlePatchTargets(
            DefaultFileProvider provider,
            UiConfigJson cfg,
            string jsonName,
            string eventName,
            Action<string> log)
        {
            var result = new List<TitlePatchTarget>();
            var tokens = BuildTitleFallbackTokens(cfg, jsonName, eventName);
            if (tokens.Count == 0)
                return result;

            foreach (string tablePackage in GetDataTablePackages(provider, musicOnly: false))
            {
                try
                {
                    var exports = provider.LoadPackage(tablePackage).GetExports();
                    var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));

                    foreach (JObject row in FindRowsWithMusicTitleAndTokens(root, tokens))
                    {
                        foreach (var textRef in FindTextReferences(row["MusicTitle"]!))
                        {
                            string stringTablePackage = ConvertGameObjectPathToPackagePath(textRef.TableId);
                            if (string.IsNullOrWhiteSpace(stringTablePackage))
                                continue;

                            log($"TitlePatch: fallback title candidate {tablePackage} -> {stringTablePackage} / {textRef.Key} ({jsonName})");
                            result.Add(new TitlePatchTarget(stringTablePackage, textRef.Key, cfg.CustomName, jsonName));
                        }
                    }
                }
                catch
                {
                    // Skip unreadable tables and keep searching.
                }
            }

            return result
                .GroupBy(t => $"{t.TablePackage}|{t.Key}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static void StageFileBackup(string stagedPath, Dictionary<string, string?> backupMap, string backupDir)
        {
            if (backupMap.ContainsKey(stagedPath))
                return;

            if (File.Exists(stagedPath))
            {
                Directory.CreateDirectory(backupDir);
                string backupPath = Path.Combine(backupDir, Guid.NewGuid() + "_" + Path.GetFileName(stagedPath));
                File.Copy(stagedPath, backupPath, true);
                backupMap[stagedPath] = backupPath;
            }
            else
            {
                backupMap[stagedPath] = null;
            }
        }

        private static string ToUnpak1Path(string unpak1Dir, string gamePath)
        {
            string rel = gamePath.StartsWith("HT/", StringComparison.OrdinalIgnoreCase) ? gamePath["HT/".Length..] : gamePath;
            return Path.Combine(unpak1Dir, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool TryPatchStringTableValue(byte[] data, string key, string newValue, out byte[] patchedData, out string detail)
        {
            patchedData = data;
            detail = "";
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            int index = 0;

            while ((index = IndexOfBytes(data, keyBytes, index)) >= 0)
            {
                if (!LooksLikeAnsiFStringAt(data, index, keyBytes.Length))
                {
                    index += keyBytes.Length;
                    continue;
                }

                int valueLenOffset = index + keyBytes.Length + 1;

                if (valueLenOffset + 4 > data.Length)
                {
                    index += keyBytes.Length;
                    continue;
                }

                int serializedLength = BitConverter.ToInt32(data, valueLenOffset);
                if (serializedLength > 0 && serializedLength < 1024)
                {
                    int valueOffset = valueLenOffset + 4;
                    if (valueOffset + serializedLength > data.Length)
                    {
                        index += keyBytes.Length;
                        continue;
                    }

                    patchedData = ReplaceByteRange(data, valueLenOffset, 4 + serializedLength, BuildUnrealFStringBytes(newValue));
                    return true;
                }

                if (serializedLength < 0 && serializedLength > -1024)
                {
                    int charCount = -serializedLength;
                    int byteCount = charCount * 2;
                    int valueOffset = valueLenOffset + 4;
                    if (valueOffset + byteCount > data.Length)
                    {
                        index += keyBytes.Length;
                        continue;
                    }

                    patchedData = ReplaceByteRange(data, valueLenOffset, 4 + byteCount, BuildUnrealFStringBytes(newValue));
                    return true;
                }

                index += keyBytes.Length;
            }

            detail = $"key {key} not found in package bytes.";
            return false;
        }

        private static bool LooksLikeAnsiFStringAt(byte[] data, int stringOffset, int byteLength)
        {
            if (stringOffset < 4 || stringOffset + byteLength >= data.Length)
                return false;

            int lengthOffset = stringOffset - 4;
            int serializedLength = BitConverter.ToInt32(data, lengthOffset);
            return serializedLength == byteLength + 1 &&
                   data[stringOffset + byteLength] == 0;
        }

        private static byte[] BuildUnrealFStringBytes(string value)
        {
            bool useUtf16 = value.Any(ch => ch > 0x7f);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

            if (useUtf16)
            {
                bw.Write(-(value.Length + 1));
                bw.Write(Encoding.Unicode.GetBytes(value));
                bw.Write((ushort)0);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                bw.Write(bytes.Length + 1);
                bw.Write(bytes);
                bw.Write((byte)0);
            }

            return ms.ToArray();
        }

        private static byte[] ReplaceByteRange(byte[] data, int offset, int oldLength, byte[] replacement)
        {
            var result = new byte[data.Length - oldLength + replacement.Length];
            Buffer.BlockCopy(data, 0, result, 0, offset);
            Buffer.BlockCopy(replacement, 0, result, offset, replacement.Length);
            Buffer.BlockCopy(data, offset + oldLength, result, offset + replacement.Length, data.Length - offset - oldLength);
            return result;
        }

        private static void PatchCompanionUassetExportSize(
            Dictionary<string, byte[]> packageFiles,
            string changedFilePath,
            int sizeDelta,
            Action<string> log)
        {
            if (!changedFilePath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                return;

            string? uassetPath = packageFiles.Keys.FirstOrDefault(path =>
                path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
            if (uassetPath == null)
            {
                log("TitlePatch: [WARNING] .uexp size changed, but companion .uasset was not found.");
                return;
            }

            byte[] uasset = packageFiles[uassetPath];
            byte[] changedFile = packageFiles[changedFilePath];
            int tagBytes = HasPackageFileTag(changedFile) ? 4 : 0;
            long oldFileSize = changedFile.Length - sizeDelta;
            long newFileSize = changedFile.Length;
            var sizeCandidates = new[]
            {
                (OldSize: oldFileSize - tagBytes, NewSize: newFileSize - tagBytes),
                (OldSize: oldFileSize, NewSize: newFileSize)
            };

            foreach (var candidate in sizeCandidates.Where(c => c.OldSize > 0 && c.NewSize > 0).Distinct())
            {
                if (TryPatchLittleEndianInt64(uasset, candidate.OldSize, candidate.NewSize))
                {
                    log($"TitlePatch: Adjusted .uasset export size ({candidate.OldSize}->{candidate.NewSize}).");
                    return;
                }

                if (candidate.OldSize <= int.MaxValue &&
                    candidate.NewSize <= int.MaxValue &&
                    TryPatchLittleEndianInt32(uasset, (int)candidate.OldSize, (int)candidate.NewSize))
                {
                    log($"TitlePatch: Adjusted .uasset export size ({candidate.OldSize}->{candidate.NewSize}).");
                    return;
                }
            }

            log("TitlePatch: [WARNING] .uexp size changed, but export size field was not located.");
        }

        private static bool HasPackageFileTag(byte[] data)
        {
            if (data.Length < 4)
                return false;

            uint tail = BitConverter.ToUInt32(data, data.Length - 4);
            return tail is 0x9E2A83C1U or 0xC1832A9EU;
        }

        private async Task<bool> TryApplyTitleIoStoreWithRetocAsync(
            string tablePackage,
            List<TitlePatchTarget> targets,
            Dictionary<string, byte[]> packageFiles)
        {
            if (string.IsNullOrWhiteSpace(retocPath) || !File.Exists(retocPath))
            {
                log("TitlePatch retoc: retoc.exe not configured. Title patch requires retoc.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(gameService.OutputDir) || !Directory.Exists(gameService.OutputDir))
            {
                log("TitlePatch retoc: [WARNING] Game Paks folder is not available.");
                return false;
            }

            try
            {
                string modBaseName = BuildTitleModBaseName(tablePackage);
                string modsDir = gameService.GetGeneratedTitleModCacheDir(modBaseName);
                if (!TryDeleteOwnTitleModFolder(modsDir, log))
                    return false;

                string tempRoot = Path.Combine(Path.GetTempPath(), "NteMusicSwapper_Retoc_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(tempRoot);

                try
                {
                    var (aesKey, _) = await FetchAesConfigAsync();
                    string legacyRoot = Path.Combine(tempRoot, "Legacy");
                    string assetName = Path.GetFileName(tablePackage) + ".uasset";
                    string? legacyVersion = null;
                    Dictionary<string, string> legacyLocalPaths = [];

                    foreach (string version in new[] { "UE5_6", "UE5_5", "UE5_4" })
                    {
                        if (Directory.Exists(legacyRoot))
                            Directory.Delete(legacyRoot, true);
                        Directory.CreateDirectory(legacyRoot);

                        string toLegacyArgs = $"--aes-key {aesKey} to-legacy \"{gameService.OutputDir}\" \"{legacyRoot}\" --filter \"{assetName}\" --version {version} --no-shaders --no-script-objects";
                        int toLegacyExit = await RunProcessForTitlePatchAsync(retocPath, toLegacyArgs, $"TitlePatch retoc to-legacy {version}");
                        if (toLegacyExit != 0)
                            continue;

                        legacyLocalPaths = FindLegacyPackageFiles(legacyRoot, tablePackage);
                        if (legacyLocalPaths.Count > 0)
                        {
                            legacyVersion = version;
                            break;
                        }
                    }

                    if (legacyVersion == null)
                    {
                        log($"TitlePatch retoc: [WARNING] Failed to extract {assetName} from IoStore.");
                        return false;
                    }

                    log($"TitlePatch retoc: Extracted {assetName} as legacy package with {legacyVersion}.");

                    var patchedFiles = legacyLocalPaths.ToDictionary(
                        kvp => kvp.Key,
                        kvp => File.ReadAllBytes(kvp.Value),
                        StringComparer.OrdinalIgnoreCase);
                    bool changed = false;

                    foreach (var target in targets)
                    {
                        bool patched = false;
                        foreach (string filePath in GetStringTablePatchFileOrder(patchedFiles.Keys))
                        {
                            if (TryPatchStringTableValue(patchedFiles[filePath], target.Key, target.CustomName, out var patchedBytes, out string detail))
                            {
                                int sizeDelta = patchedBytes.Length - patchedFiles[filePath].Length;
                                patchedFiles[filePath] = patchedBytes;
                                if (sizeDelta != 0)
                                    PatchCompanionUassetExportSize(patchedFiles, filePath, sizeDelta, log);

                                log($"TitlePatch retoc: [PATCH] {target.Key} -> \"{target.CustomName}\" ({target.JsonName}) in {filePath}");
                                patched = true;
                                changed = true;
                                break;
                            }

                            if (!string.IsNullOrWhiteSpace(detail))
                                log($"TitlePatch retoc: [SKIP] {target.Key}: {detail}");
                        }

                        if (!patched)
                            log($"TitlePatch retoc: [MISS] Could not patch string-table key {target.Key} for {target.JsonName}.");
                    }

                    if (!changed)
                        return false;

                    foreach (var kvp in patchedFiles)
                    {
                        if (legacyLocalPaths.TryGetValue(kvp.Key, out string? localPath))
                            File.WriteAllBytes(localPath, kvp.Value);
                    }

                    string responseFile = Path.Combine(tempRoot, "retoc_legacy_response.txt");
                    using (var sw = new StreamWriter(responseFile, false, new UTF8Encoding(false)))
                    {
                        foreach (string file in Directory.GetFiles(legacyRoot, "*", SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(legacyRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                            sw.WriteLine($"\"{file}\" \"../../../{rel}\"");
                        }
                    }

                    string legacyPak = Path.Combine(tempRoot, $"{modBaseName}_Legacy.pak");
                    string unrealPakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
                    if (!File.Exists(unrealPakExe))
                    {
                        log("TitlePatch retoc: [WARNING] UnrealPak.exe not found; cannot build legacy pak for retoc.");
                        return false;
                    }

                    int pakExit = await RunProcessForTitlePatchAsync(unrealPakExe, $"\"{legacyPak}\" -create=\"{responseFile}\"", "TitlePatch UnrealPak");
                    if (pakExit != 0 || !File.Exists(legacyPak))
                    {
                        log($"TitlePatch retoc: [WARNING] Failed to create legacy pak for {tablePackage}.");
                        return false;
                    }

                    string outputBaseName = modBaseName + "_P";
                    string outputUtoc = Path.Combine(tempRoot, outputBaseName + ".utoc");
                    bool converted = false;
                    foreach (string version in new[] { "UE5_6", "UE5_5", "UE5_4" })
                    {
                        DeleteRetocOutput(outputUtoc);
                        string args = $"--aes-key {aesKey} to-zen \"{legacyPak}\" \"{outputUtoc}\" --version {version}";
                        int retocExit = await RunProcessForTitlePatchAsync(retocPath, args, $"TitlePatch retoc {version}");
                        if (retocExit == 0 && File.Exists(outputUtoc) && File.Exists(Path.ChangeExtension(outputUtoc, ".ucas")))
                        {
                            log($"TitlePatch retoc: Converted IoStore container with {version}.");
                            converted = true;
                            break;
                        }
                    }

                    if (!converted)
                    {
                        log("TitlePatch retoc: [WARNING] retoc to-zen failed.");
                        return false;
                    }

                    if (Directory.Exists(modsDir))
                        Directory.Delete(modsDir, true);
                    Directory.CreateDirectory(modsDir);

                    foreach (string file in Directory.GetFiles(tempRoot, outputBaseName + ".*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(file, Path.Combine(modsDir, Path.GetFileName(file)), true);
                        }
                    }

                    log($"TitlePatch retoc: Cached generated title IoStore mod at {modsDir}");
                    return true;
                }
                finally
                {
                    try { Directory.Delete(tempRoot, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                log($"TitlePatch retoc: [WARNING] {ex.Message}");
                return false;
            }
        }

        private static void DeleteRetocOutput(string utocPath)
        {
            foreach (string path in new[]
            {
                utocPath,
                Path.ChangeExtension(utocPath, ".ucas"),
                Path.ChangeExtension(utocPath, ".pak")
            })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static Dictionary<string, string> FindLegacyPackageFiles(string legacyRoot, string tablePackage)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string packageFileName = Path.GetFileName(tablePackage);
            string expectedUasset = Path.Combine(
                legacyRoot,
                (tablePackage + ".uasset").Replace('/', Path.DirectorySeparatorChar));

            string? uassetPath = File.Exists(expectedUasset)
                ? expectedUasset
                : Directory.GetFiles(legacyRoot, packageFileName + ".uasset", SearchOption.AllDirectories)
                    .FirstOrDefault();

            if (uassetPath == null)
                return result;

            string packageDir = Path.GetDirectoryName(uassetPath)!;
            foreach (string localPath in Directory.GetFiles(packageDir, packageFileName + ".*", SearchOption.TopDirectoryOnly))
            {
                string ext = Path.GetExtension(localPath);
                if (!ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".uexp", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".ubulk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string rel = Path.GetRelativePath(legacyRoot, localPath).Replace('\\', '/');
                result[rel] = localPath;
            }

            return result;
        }

        private static bool TryDeleteOwnTitleModFolder(string modsDir, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(modsDir))
                    return true;

                Directory.Delete(modsDir, true);
                log($"TitlePatch retoc: Removed previous generated title mod before extraction: {modsDir}");
                return true;
            }
            catch (Exception ex)
            {
                log($"TitlePatch retoc: [WARNING] Failed to remove previous title mod before extraction: {ex.Message}");
                return false;
            }
        }

        private static bool TryDeleteGeneratedTitleModFolders(string outputDir, Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                    return true;

                string modsRoot = Path.Combine(outputDir, "~mods");
                if (!Directory.Exists(modsRoot))
                    return true;

                var dirs = Directory.GetDirectories(modsRoot, "NteMusicSwapper_Title_*", SearchOption.TopDirectoryOnly)
                    .Append(Path.Combine(modsRoot, "NteMusicSwapper_ST_UI_C"))
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string dir in dirs)
                {
                    Directory.Delete(dir, true);
                    log($"TitlePatch retoc: Removed previous generated title mod before extraction: {dir}");
                }

                return true;
            }
            catch (Exception ex)
            {
                log($"TitlePatch retoc: [WARNING] Failed to remove generated title mods before extraction: {ex.Message}");
                return false;
            }
        }

        private static string BuildTitleModBaseName(string tablePackage)
        {
            string assetName = SanitizeFileName(Path.GetFileName(tablePackage));
            return assetName.Equals("ST_UI_C", StringComparison.OrdinalIgnoreCase)
                ? "NteMusicSwapper_ST_UI_C"
                : "NteMusicSwapper_Title_" + assetName;
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            return sb.Length == 0 ? "Unknown" : sb.ToString();
        }

        private static List<string> GetStringTablePatchFileOrder(IEnumerable<string> paths)
        {
            return paths
                .OrderBy(path => path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase) ? 0 :
                                 path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                .ThenBy(path => path)
                .ToList();
        }

        private async Task<int> RunProcessForTitlePatchAsync(string exe, string args, string label)
        {
            var tcs = new TaskCompletionSource<int>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log($"{label}: {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log($"{label}: {e.Data}"); };
            p.Exited += (_, _) =>
            {
                int exitCode = p.ExitCode;
                p.Dispose();
                tcs.TrySetResult(exitCode);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return await tcs.Task;
        }

        private void ApplyTitleIoStoreTemplatePatch(List<TitlePatchTarget> targets)
        {
            string? templateDir = FindBtrStringTableTemplateDir();
            if (templateDir == null)
            {
                log("TitlePatch IoStore: [WARNING] BTRRadio ST_UI_C template not found. Cannot create IoStore title mod.");
                return;
            }

            if (string.IsNullOrWhiteSpace(gameService.OutputDir) || !Directory.Exists(gameService.OutputDir))
            {
                log("TitlePatch IoStore: [WARNING] Game Paks folder is not available.");
                return;
            }

            string sourceUcas = Directory.GetFiles(templateDir, "*.ucas", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "";
            string sourceUtoc = Directory.GetFiles(templateDir, "*.utoc", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "";
            string sourcePak = Directory.GetFiles(templateDir, "*.pak", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "";
            if (!File.Exists(sourceUcas) || !File.Exists(sourceUtoc) || !File.Exists(sourcePak))
            {
                log($"TitlePatch IoStore: [WARNING] Template is incomplete: {templateDir}");
                return;
            }

            byte[] ucasData = File.ReadAllBytes(sourceUcas);
            bool changed = false;
            foreach (var target in targets)
            {
                if (TryPatchFixedSizeStringTableValue(ucasData, target.Key, target.CustomName, out string detail))
                {
                    log($"TitlePatch IoStore: [PATCH] {target.Key} -> \"{target.CustomName}\" ({target.JsonName})");
                    changed = true;
                }
                else
                {
                    log($"TitlePatch IoStore: [SKIP] {target.Key}: {detail}");
                }
            }

            if (!changed)
                return;

            string modsDir = Path.Combine(gameService.OutputDir, "~mods", "NteMusicSwapper_ST_UI_C");
            Directory.CreateDirectory(modsDir);

            string pakName = Path.GetFileName(sourcePak);
            string utocName = Path.GetFileName(sourceUtoc);
            string ucasName = Path.GetFileName(sourceUcas);
            File.Copy(sourcePak, Path.Combine(modsDir, pakName), true);
            File.Copy(sourceUtoc, Path.Combine(modsDir, utocName), true);
            File.WriteAllBytes(Path.Combine(modsDir, ucasName), ucasData);

            log($"TitlePatch IoStore: Installed ST_UI_C title mod to {modsDir}");
            log("TitlePatch IoStore: Restart the launcher/game if the title was already loaded.");
        }

        private static string? FindBtrStringTableTemplateDir()
        {
            foreach (string root in EnumerateSearchRoots())
            {
                string candidate = Path.Combine(root, "BTRRadio", "HT", "Content", "Paks", "~mods", "YHBGM_ST_ST_UI_C");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IEnumerable<string> EnumerateSearchRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (seen.Add(dir.FullName))
                        yield return dir.FullName;
                    dir = dir.Parent;
                }
            }
        }

        private static bool TryPatchFixedSizeStringTableValue(byte[] data, string key, string newValue, out string detail)
        {
            detail = "";
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            int index = 0;

            while ((index = IndexOfBytes(data, keyBytes, index)) >= 0)
            {
                if (!LooksLikeAnsiFStringAt(data, index, keyBytes.Length))
                {
                    index += keyBytes.Length;
                    continue;
                }

                int valueLenOffset = index + keyBytes.Length + 1;
                if (valueLenOffset + 4 > data.Length)
                {
                    index += keyBytes.Length;
                    continue;
                }

                int oldLength = BitConverter.ToInt32(data, valueLenOffset);
                int oldByteLength = oldLength > 0 ? oldLength : -oldLength * 2;
                int oldTotalLength = 4 + oldByteLength;
                if (oldLength == 0 || oldByteLength <= 0 || valueLenOffset + oldTotalLength > data.Length)
                {
                    index += keyBytes.Length;
                    continue;
                }

                if (!TryBuildFixedSizeFString(newValue, oldTotalLength, out byte[] replacement, out detail))
                    return false;

                Buffer.BlockCopy(replacement, 0, data, valueLenOffset, oldTotalLength);
                return true;
            }

            detail = $"key {key} not found in IoStore template.";
            return false;
        }

        private static bool TryBuildFixedSizeFString(string value, int totalLength, out byte[] bytes, out string detail)
        {
            bytes = [];
            detail = "";
            bool useUtf16 = value.Any(ch => ch > 0x7f);

            if (useUtf16)
            {
                if ((totalLength - 4) % 2 != 0)
                {
                    detail = "existing slot cannot hold UTF-16 FString.";
                    return false;
                }

                int charCapacity = ((totalLength - 4) / 2) - 1;
                if (value.Length > charCapacity)
                {
                    detail = $"CustomName is too long for template slot ({value.Length}>{charCapacity} UTF-16 chars).";
                    return false;
                }

                string padded = value.PadRight(charCapacity);
                bytes = new byte[totalLength];
                Buffer.BlockCopy(BitConverter.GetBytes(-(charCapacity + 1)), 0, bytes, 0, 4);
                Buffer.BlockCopy(Encoding.Unicode.GetBytes(padded), 0, bytes, 4, charCapacity * 2);
                return true;
            }

            byte[] valueBytes = Encoding.UTF8.GetBytes(value);
            int byteCapacity = totalLength - 5;
            if (valueBytes.Length > byteCapacity)
            {
                detail = $"CustomName is too long for template slot ({valueBytes.Length}>{byteCapacity} bytes).";
                return false;
            }

            bytes = new byte[totalLength];
            Buffer.BlockCopy(BitConverter.GetBytes(byteCapacity + 1), 0, bytes, 0, 4);
            Buffer.BlockCopy(valueBytes, 0, bytes, 4, valueBytes.Length);
            for (int i = 4 + valueBytes.Length; i < 4 + byteCapacity; i++)
                bytes[i] = 0x20;
            return true;
        }

        private static bool TryPatchLittleEndianInt64(byte[] data, long oldValue, long newValue)
        {
            byte[] oldBytes = BitConverter.GetBytes(oldValue);
            int offset = IndexOfBytes(data, oldBytes, 0);
            if (offset < 0)
                return false;

            byte[] newBytes = BitConverter.GetBytes(newValue);
            Buffer.BlockCopy(newBytes, 0, data, offset, newBytes.Length);
            return true;
        }

        private static bool TryPatchLittleEndianInt32(byte[] data, int oldValue, int newValue)
        {
            byte[] oldBytes = BitConverter.GetBytes(oldValue);
            int offset = IndexOfBytes(data, oldBytes, 0);
            if (offset < 0)
                return false;

            byte[] newBytes = BitConverter.GetBytes(newValue);
            Buffer.BlockCopy(newBytes, 0, data, offset, newBytes.Length);
            return true;
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int startIndex)
        {
            for (int i = Math.Max(0, startIndex); i <= haystack.Length - needle.Length; i++)
            {
                bool matched = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return i;
            }

            return -1;
        }

        private static List<(string Key, double Value)> ExtractDurationValuesFromBnk(
            byte[] bnkData, string bnkName, string bnkFullPath, Action<string> log)
        {
            var result = new List<(string Key, double Value)>();
            try
            {
                log($"PatchPak: Parsing BNK with CUE4Parse: {bnkFullPath}");
                var wwiseAr = new FWwiseArchive(bnkName, bnkData);
                var reader = new WwiseReader(wwiseAr, new WwiseGameFileSource(null!));

                string json = JsonConvert.SerializeObject(reader, Formatting.None);
                var root = JObject.Parse(json);

                log($"PatchPak: Scanning parsed JSON tree for keys containing \"Duration\"...");
                CollectDurationValues(root, result, log);
                log($"PatchPak: Scan complete. {result.Count} unique Duration value(s) collected.");
            }
            catch (Exception ex)
            {
                log($"PatchPak: [WARNING] CUE4Parse failed to parse [{bnkFullPath}]: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// JToken ツリーを再帰走査し、プロパティ名に "Duration"（大文字小文字不問）を含む
        /// 数値トークンをすべて収集する。
        /// </summary>
        private static void CollectDurationValues(JToken token, List<(string Key, double Value)> result, Action<string> log)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        if (prop.Name.IndexOf("Duration", StringComparison.OrdinalIgnoreCase) >= 0
                            && prop.Value.Type == JTokenType.Float)
                        {
                            double val = prop.Value.Value<double>();
                            if (val != 0.0 && !result.Any(r => r.Value == val))
                            {
                                result.Add((prop.Name, val));
                                log($"PatchPak:   [FOUND] Key=\"{prop.Name}\" Value={val:F6} ms");
                            }
                        }
                        CollectDurationValues(prop.Value, result, log);
                    }
                    break;

                case JTokenType.Array:
                    foreach (var item in (JArray)token)
                        CollectDurationValues(item, result, log);
                    break;
            }
        }

        /// <summary>
        /// bnkData バイト配列内に存在する、収集した Duration double 値をすべて
        /// newDurationBytes（8バイト LE double）で上書きする。
        /// 上書きした総箇所数を返す。
        /// </summary>
        public async Task<string?> FindAndAddUiConfigForWemAsync(string wemFileName)
        {
            wemFileName = NormalizeWemFileName(wemFileName);
            if (string.IsNullOrWhiteSpace(wemFileName))
                return null;

            if (!uint.TryParse(Path.GetFileNameWithoutExtension(wemFileName), out uint wemId))
            {
                log($"Music search: Invalid WEM file name: {wemFileName}");
                return null;
            }

            var musicMetadata = await FindMusicMetadataFromGameAssetsAsync(wemId);
            if (musicMetadata != null)
            {
                AddOrUpdateUiConfigEntry(wemFileName, musicMetadata.JsonName, musicMetadata.DisplayName);
                log($"Music search: ui_wem_config.json saved: {wemFileName} -> {musicMetadata.JsonName} / {musicMetadata.DisplayName}");
                return musicMetadata.JsonName;
            }

            log("Music search: Player music asset lookup failed. Falling back to BNK scan.");

            return await Task.Run(() =>
            {
                var matchedBnks = FindBnkFilesReferencingWem(wemId);
                if (matchedBnks.Count == 0)
                {
                    log($"BNK search: No BNK references {wemFileName}.");
                    return null;
                }

                foreach (var match in matchedBnks)
                    log($"BNK search: Candidate for {wemFileName}: {match}");

                string jsonName = Path.GetFileNameWithoutExtension(matchedBnks[0]);
                string displayName = BuildDisplayNameFallback(jsonName);
                AddOrUpdateUiConfigEntry(wemFileName, jsonName, displayName);
                log($"BNK search: ui_wem_config.json saved: {wemFileName} -> {jsonName} / {displayName}");
                return jsonName;
            });
        }

        private async Task<MusicMetadata?> FindMusicMetadataFromGameAssetsAsync(uint wemId)
        {
            try
            {
                string paksDir = FindGamePaksDir();
                if (string.IsNullOrEmpty(paksDir))
                {
                    log("Music search: Game Paks folder not found.");
                    return null;
                }

                var (aesKey, mappingUrl) = await FetchAesConfigAsync();
                string usmapPath = await DownloadUsmapAsync(mappingUrl);

                log($"Music search: Loading game assets from {paksDir}");
                await OodleHelper.InitializeAsync();

                var provider = new DefaultFileProvider(
                    paksDir,
                    SearchOption.TopDirectoryOnly,
                    new VersionContainer(EGame.GAME_UE5_6),
                    StringComparer.OrdinalIgnoreCase);

                if (File.Exists(usmapPath))
                    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);

                provider.Initialize();
                provider.SubmitKey(new FGuid(), new FAesKey(aesKey));

                string? eventPackage = FindMusicEventPackageByWemId(provider, wemId);
                if (eventPackage == null)
                {
                    log($"Music search: No Music_Vehicle event asset has MediaId={wemId}.");
                    return null;
                }

                string jsonName = Path.GetFileName(eventPackage);
                string? displayName = FindDisplayNameInMusicDataTables(provider, eventPackage);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = BuildDisplayNameFallback(jsonName);
                    log($"Music search: Display name table row not found for {jsonName}. Using fallback name: {displayName}");
                }

                return new MusicMetadata(jsonName, displayName);
            }
            catch (Exception ex)
            {
                log($"Music search: [WARNING] Failed to read game assets: {ex.Message}");
                return null;
            }
        }

        private string FindGamePaksDir()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameService.GameRootDir))
            {
                candidates.Add(Path.Combine(gameService.GameRootDir, "Client", "WindowsNoEditor", "HT", "Content", "Paks"));
            }

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Neverness To Everness", "Client", "WindowsNoEditor", "HT", "Content", "Paks"));

            return candidates.FirstOrDefault(Directory.Exists) ?? "";
        }

        private static async Task<(string AesKey, string MappingUrl)> FetchAesConfigAsync()
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string json = await hc.GetStringAsync(AesJsonUrl);
            var root = JObject.Parse(json);
            string aesKey = root.Value<string>("aes_key") ?? "";
            string mappingUrl = root.Value<string>("mapping") ?? "";

            if (string.IsNullOrWhiteSpace(aesKey))
                throw new InvalidDataException("AES key is empty.");

            return (aesKey, mappingUrl);
        }

        private static async Task<string> DownloadUsmapAsync(string mappingUrl)
        {
            string path = Path.Combine(Path.GetTempPath(), "NteMusicSwapper_mappings.usmap");
            if (File.Exists(path) || string.IsNullOrWhiteSpace(mappingUrl))
                return path;

            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            byte[] bytes = await hc.GetByteArrayAsync(mappingUrl);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }

        private static string? FindMusicEventPackageByWemId(DefaultFileProvider provider, uint wemId)
        {
            var eventPackages = provider.Files.Values
                .Select(file => file.Path)
                .Where(path => path.StartsWith("HT/Content/WwiseAudio/Events/Music/", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetFileName(path).StartsWith("Play_Music_Radio_", StringComparison.OrdinalIgnoreCase))
                .Select(path => path[..^".uasset".Length])
                .OrderBy(path => path);

            foreach (string packagePath in eventPackages)
            {
                try
                {
                    var exports = provider.LoadPackage(packagePath).GetExports();
                    var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));
                    if (ContainsUintProperty(root, "MediaId", wemId))
                        return packagePath;
                }
                catch
                {
                    // Skip unreadable event assets and continue scanning.
                }
            }

            return null;
        }

        private static string? FindDisplayNameInMusicDataTables(DefaultFileProvider provider, string eventPackage)
        {
            string eventAssetPath = "/Game/" + eventPackage["HT/Content/".Length..] + "." + Path.GetFileName(eventPackage);
            string eventName = Path.GetFileName(eventPackage);
            var tablePackages = provider.Files.Values
                .Select(file => file.Path)
                .Where(path => path.StartsWith("HT/Content/DataTable/Music/", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Select(path => path[..^".uasset".Length])
                .OrderBy(path => path.Equals("HT/Content/DataTable/Music/DT_PlayerMusic", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path);

            foreach (string tablePackage in tablePackages)
            {
                try
                {
                    var exports = provider.LoadPackage(tablePackage).GetExports();
                    var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));

                    // Music DataTable rows store the UI title next to MusicEvent.AssetPathName.
                    JObject? row = FindMusicRowByEventPath(root, eventAssetPath, eventName);
                    if (row == null)
                        continue;

                    JToken titleToken = row["MusicTitle"] ?? row;
                    string? directText = FindFirstFTextString(titleToken);
                    if (!string.IsNullOrWhiteSpace(directText))
                        return directText;

                    var textRef = FindFirstTextReference(titleToken);
                    if (textRef != null)
                        return ResolveStringTableText(provider, textRef.Value.TableId, textRef.Value.Key);
                }
                catch
                {
                    // Skip unreadable tables and keep looking for a title row.
                }
            }

            return null;
        }

        private static List<JObject> FindMusicRowsWithTitles(JToken token)
        {
            var rows = new List<JObject>();
            CollectMusicRowsWithTitles(token, rows);
            return rows;
        }

        private static void CollectMusicRowsWithTitles(JToken token, List<JObject> rows)
        {
            if (token is JObject obj &&
                obj["MusicEvent"]?["AssetPathName"] != null &&
                obj["MusicTitle"] != null)
            {
                rows.Add(obj);
            }

            foreach (var child in token.Children())
                CollectMusicRowsWithTitles(child, rows);
        }

        private static string ExtractAssetObjectName(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return "";

            string name = assetPath;
            int dotIndex = name.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex + 1 < name.Length)
                name = name[(dotIndex + 1)..];

            int slashIndex = name.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex + 1 < name.Length)
                name = name[(slashIndex + 1)..];

            return Path.GetFileNameWithoutExtension(name.Trim());
        }

        private static JObject? FindMusicRowByEventPath(JToken token, string eventAssetPath, string eventName)
        {
            if (token is JObject obj)
            {
                string? assetPath = obj["MusicEvent"]?["AssetPathName"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(assetPath) &&
                    (assetPath.Equals(eventAssetPath, StringComparison.OrdinalIgnoreCase) ||
                     assetPath.Contains(eventName, StringComparison.OrdinalIgnoreCase)))
                {
                    return obj;
                }
            }

            foreach (var child in token.Children())
            {
                var match = FindMusicRowByEventPath(child, eventAssetPath, eventName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static List<JObject> FindMusicRowsByEventPath(JToken token, string eventAssetPath, string eventName)
        {
            var rows = new List<JObject>();
            CollectMusicRowsByEventPath(token, eventAssetPath, eventName, rows);
            return rows;
        }

        private static void CollectMusicRowsByEventPath(
            JToken token,
            string eventAssetPath,
            string eventName,
            List<JObject> rows)
        {
            if (token is JObject obj)
            {
                string? assetPath = obj["MusicEvent"]?["AssetPathName"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(assetPath) &&
                    (assetPath.Equals(eventAssetPath, StringComparison.OrdinalIgnoreCase) ||
                     assetPath.Contains(eventName, StringComparison.OrdinalIgnoreCase)))
                {
                    rows.Add(obj);
                }
            }

            foreach (var child in token.Children())
                CollectMusicRowsByEventPath(child, eventAssetPath, eventName, rows);
        }

        private static List<JObject> FindRowsWithMusicTitleAndTokens(JToken token, List<string> tokens)
        {
            var rows = new List<JObject>();
            CollectRowsWithMusicTitleAndTokens(token, tokens, rows);
            return rows;
        }

        private static void CollectRowsWithMusicTitleAndTokens(JToken token, List<string> tokens, List<JObject> rows)
        {
            if (token is JObject obj &&
                obj["MusicTitle"] != null &&
                ObjectContainsAnyToken(obj, tokens))
            {
                rows.Add(obj);
            }

            foreach (var child in token.Children())
                CollectRowsWithMusicTitleAndTokens(child, tokens, rows);
        }

        private static bool ObjectContainsAnyToken(JToken token, List<string> tokens)
        {
            if (token.Type == JTokenType.String)
            {
                string? value = token.Value<string>();
                return !string.IsNullOrWhiteSpace(value) &&
                       tokens.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var child in token.Children())
            {
                if (ObjectContainsAnyToken(child, tokens))
                    return true;
            }

            return false;
        }

        private static List<string> BuildTitleFallbackTokens(UiConfigJson cfg, string jsonName, string eventName)
        {
            var tokens = new List<string> { jsonName, eventName };

            const string radioPrefix = "Play_Music_Radio_";
            if (jsonName.StartsWith(radioPrefix, StringComparison.OrdinalIgnoreCase))
                tokens.Add(jsonName[radioPrefix.Length..]);

            string wemId = Path.GetFileNameWithoutExtension(cfg.WemName);
            if (!string.IsNullOrWhiteSpace(wemId))
                tokens.Add(wemId);

            return tokens
                .Select(t => t.Trim())
                .Where(t => t.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<(string TableId, string Key)> FindTextReferences(JToken token)
        {
            var result = new List<(string TableId, string Key)>();
            CollectTextReferences(token, result);
            return result
                .Where(item => !string.IsNullOrWhiteSpace(item.TableId) && !string.IsNullOrWhiteSpace(item.Key))
                .Distinct()
                .ToList();
        }

        private static void CollectTextReferences(JToken token, List<(string TableId, string Key)> result)
        {
            if (token is JObject obj)
            {
                string? tableId = obj.Value<string>("TableId");
                string? key = obj.Value<string>("Key");
                if (!string.IsNullOrWhiteSpace(tableId) && !string.IsNullOrWhiteSpace(key))
                    result.Add((tableId, key));
            }

            foreach (var child in token.Children())
                CollectTextReferences(child, result);
        }

        private static bool ContainsUintProperty(JToken token, string propertyName, uint expectedValue)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (!prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (prop.Value.Type == JTokenType.Integer && prop.Value.Value<uint>() == expectedValue)
                        return true;

                    if (uint.TryParse(prop.Value.ToString(), out uint parsed) && parsed == expectedValue)
                        return true;
                }
            }

            return token.Children().Any(child => ContainsUintProperty(child, propertyName, expectedValue));
        }

        private static string? FindFirstFTextString(JToken token)
        {
            if (token is JObject obj)
            {
                string? source = obj.Value<string>("SourceString");
                if (!string.IsNullOrWhiteSpace(source))
                    return source;

                string? localized = obj.Value<string>("LocalizedString");
                if (!string.IsNullOrWhiteSpace(localized))
                    return localized;
            }

            foreach (var child in token.Children())
            {
                string? value = FindFirstFTextString(child);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static (string TableId, string Key)? FindFirstTextReference(JToken token)
        {
            if (token is JObject obj)
            {
                string? tableId = obj.Value<string>("TableId");
                string? key = obj.Value<string>("Key");
                if (!string.IsNullOrWhiteSpace(tableId) && !string.IsNullOrWhiteSpace(key))
                    return (tableId, key);
            }

            foreach (var child in token.Children())
            {
                var value = FindFirstTextReference(child);
                if (value != null)
                    return value;
            }

            return null;
        }

        private static string? ResolveStringTableText(DefaultFileProvider provider, string tableId, string key)
        {
            string packagePath = ConvertGameObjectPathToPackagePath(tableId);
            if (string.IsNullOrWhiteSpace(packagePath))
                return null;

            var exports = provider.LoadPackage(packagePath).GetExports();
            var root = JToken.Parse(JsonConvert.SerializeObject(exports, Formatting.None));
            return FindStringTableValue(root, key);
        }

        private static string ConvertGameObjectPathToPackagePath(string objectPath)
        {
            if (!objectPath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
                return "";

            string package = objectPath[6..];
            int dotIndex = package.IndexOf('.');
            if (dotIndex >= 0)
                package = package[..dotIndex];

            return "HT/Content/" + package;
        }

        private static string? FindStringTableValue(JToken token, string key)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.Type == JTokenType.String)
                    {
                        return prop.Value.Value<string>();
                    }
                }
            }

            foreach (var child in token.Children())
            {
                string? value = FindStringTableValue(child, key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        public bool WemFileExists(string wemFileName)
        {
            return ResolveWemFullPath(wemFileName) != null;
        }

        public string? ResolveWemFullPath(string wemFileName)
        {
            wemFileName = NormalizeWemFileName(wemFileName);
            if (string.IsNullOrWhiteSpace(wemFileName))
                return null;

            foreach (var root in GetUnpackRoots())
            {
                var files = Directory.GetFiles(root.Path, wemFileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files.OrderByDescending(path => path.Length).First();
            }

            return null;
        }

        public static string NormalizeWemFileName(string wemFileName)
        {
            wemFileName = Path.GetFileName(wemFileName.Trim());
            if (!string.IsNullOrWhiteSpace(wemFileName) &&
                !wemFileName.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            {
                wemFileName += ".wem";
            }

            return wemFileName;
        }

        private static string BuildDisplayNameFallback(string jsonName)
        {
            string name = Path.GetFileNameWithoutExtension(jsonName);
            const string prefix = "Play_Music_Radio_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name[prefix.Length..];

            name = name.Replace('_', ' ').Trim();
            return string.IsNullOrWhiteSpace(name) ? "Default" : name;
        }

        private IEnumerable<(string Label, string Path)> GetUnpackRoots()
        {
            if (Directory.Exists(unpackDir))
                yield return ("Unpak", unpackDir);

            string unpak1Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unpak1", "HT");
            if (Directory.Exists(unpak1Dir))
                yield return ("Unpak1", unpak1Dir);
        }

        private List<string> FindBnkFilesReferencingWem(uint wemId)
        {
            bnkReferenceCache ??= BuildBnkReferenceCache();
            return bnkReferenceCache.TryGetValue(wemId, out var matches)
                ? matches.OrderBy(path => path).ToList()
                : [];
        }

        private Dictionary<uint, List<string>> BuildBnkReferenceCache()
        {
            var sw = Stopwatch.StartNew();
            var roots = new[]
            {
                Path.Combine(unpackDir, "Content", "WwiseAudio", "Event"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Unpak1", "HT", "Content", "WwiseAudio", "Event")
            };

            var cache = new Dictionary<uint, List<string>>();
            int bnkCount = 0;
            int parsedCount = 0;

            foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                log($"BNK search: Building first-use index from {root}");
                foreach (var bnkPath in Directory.GetFiles(root, "*.bnk", SearchOption.AllDirectories).OrderBy(p => p))
                {
                    bnkCount++;
                    try
                    {
                        byte[] bnkData = File.ReadAllBytes(bnkPath);
                        string bnkName = Path.GetFileNameWithoutExtension(bnkPath);
                        using var wwiseAr = new FWwiseArchive(bnkName, bnkData);
                        var reader = new WwiseReader(wwiseAr, new WwiseArchiveSource());

                        foreach (uint sourceId in ExtractSourceIdsFromBnk(reader))
                        {
                            if (!cache.TryGetValue(sourceId, out var paths))
                            {
                                paths = [];
                                cache[sourceId] = paths;
                            }

                            paths.Add(bnkPath);
                        }

                        parsedCount++;
                    }
                    catch (Exception ex)
                    {
                        log($"BNK search: [WARNING] Failed to parse {bnkPath}: {ex.Message}");
                    }
                }
            }

            log($"BNK search: Cached {cache.Count} source id(s) from {parsedCount}/{bnkCount} BNK file(s) in {sw.Elapsed.TotalSeconds:0.0}s.");
            return cache;
        }

        private bool BnkReferencesWem(string bnkPath, uint wemId)
        {
            try
            {
                byte[] bnkData = File.ReadAllBytes(bnkPath);
                string bnkName = Path.GetFileNameWithoutExtension(bnkPath);
                using var wwiseAr = new FWwiseArchive(bnkName, bnkData);
                var reader = new WwiseReader(wwiseAr, new WwiseArchiveSource());

                var sourceIds = ExtractSourceIdsFromBnk(reader);
                return sourceIds.Contains(wemId);
            }
            catch (Exception ex)
            {
                log($"BNK search: [WARNING] Failed to parse {bnkPath}: {ex.Message}");
                return false;
            }
        }

        private static HashSet<uint> ExtractSourceIdsFromBnk(WwiseReader reader)
        {
            var ids = new HashSet<uint>();
            if (reader.Hierarchies == null)
                return ids;

            foreach (var hierarchy in reader.Hierarchies)
            {
                // Wwise BNK points at WEM files by numeric source ids.
                switch (hierarchy.Data)
                {
                    case HierarchySoundSfxVoice sound:
                        AddNonZero(ids, sound.Source.SourceId);
                        AddNonZero(ids, sound.Source.FileId);
                        AddNonZero(ids, sound.Source.CacheId);
                        break;

                    case HierarchyMusicTrack track:
                        foreach (var source in track.Sources)
                        {
                            AddNonZero(ids, source.SourceId);
                            AddNonZero(ids, source.FileId);
                            AddNonZero(ids, source.CacheId);
                        }

                        foreach (var item in track.Playlist)
                        {
                            AddNonZero(ids, item.SourceId);
                            AddNonZero(ids, item.CacheId);
                        }
                        break;
                }
            }

            return ids;
        }

        private static void AddNonZero(HashSet<uint> ids, uint id)
        {
            if (id != 0)
                ids.Add(id);
        }

        private void AddOrUpdateUiConfigEntry(string wemFileName, string jsonName, string displayName)
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_wem_config.json");
            var items = new List<UiConfigJson>();

            if (File.Exists(jsonPath))
            {
                string jsonString = File.ReadAllText(jsonPath);
                items = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonString) ?? [];
            }

            var existing = items.FirstOrDefault(item =>
                string.Equals(item.WemName, wemFileName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                string resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Default" : displayName;
                bool customNameShouldFollowDisplay =
                    string.IsNullOrWhiteSpace(existing.CustomName) ||
                    string.Equals(existing.CustomName, existing.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.CustomName, "Default", StringComparison.OrdinalIgnoreCase);

                existing.JsonName = jsonName;
                existing.DisplayName = resolvedDisplayName;
                if (customNameShouldFollowDisplay)
                    existing.CustomName = resolvedDisplayName;
                if (string.IsNullOrWhiteSpace(existing.ImageName))
                    existing.ImageName = "OST.png";
            }
            else
            {
                string resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Default" : displayName;
                items.Add(new UiConfigJson
                {
                    WemName = wemFileName,
                    DisplayName = resolvedDisplayName,
                    CustomName = resolvedDisplayName,
                    ImageName = "OST.png",
                    JsonName = jsonName,
                    SavedPath = ""
                });
            }

            SaveUiConfig(items);
        }

        public void UpdateCustomName(string wemFileName, string customName)
        {
            wemFileName = NormalizeWemFileName(wemFileName);
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_wem_config.json");
            if (string.IsNullOrWhiteSpace(wemFileName) || !File.Exists(jsonPath))
                return;

            var items = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(File.ReadAllText(jsonPath)) ?? [];
            var item = items.FirstOrDefault(x => string.Equals(x.WemName, wemFileName, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            item.CustomName = string.IsNullOrWhiteSpace(customName) ? item.DisplayName : customName.Trim();
            SaveUiConfig(items);
        }

        private static int PatchDurationValuesInBnk(
            byte[] bnkData,
            List<(string Key, double Value)> durationValues,
            byte[] newDurationBytes,
            double newDurationMs,
            Action<string> log)
        {
            int totalPatched = 0;

            foreach (var (key, oldVal) in durationValues)
            {
                byte[] searchBytes = BitConverter.GetBytes(oldVal);
                int hitCount = 0;

                for (int i = 0; i <= bnkData.Length - 8; i++)
                {
                    bool match = true;
                    for (int b = 0; b < 8; b++)
                    {
                        if (bnkData[i + b] != searchBytes[b]) { match = false; break; }
                    }
                    if (match)
                    {
                        Array.Copy(newDurationBytes, 0, bnkData, i, 8);
                        hitCount++;
                        totalPatched++;
                        log($"PatchPak:   [PATCH] Key=\"{key}\" OldValue={oldVal:F6} ms → NewValue={newDurationMs:F6} ms (offset=0x{i:X8})");
                        i += 7;
                    }
                }

                if (hitCount == 0)
                    log($"PatchPak:   [MISS]  Key=\"{key}\" Value={oldVal:F6} ms — bytes not found in binary.");
            }

            return totalPatched;
        }

        /// <summary>
        /// WAV ファイルの長さをミリ秒単位の float64 LE バイト列として返す。
        /// 失敗時は null を返す。
        /// </summary>
        private static byte[]? GetWavDurationAsFloat64LE(string wavPath)
        {
            try
            {
                using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                string ReadFourCC()
                {
                    byte[] bytes = br.ReadBytes(4);
                    return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : "";
                }

                string riff = ReadFourCC();
                if (riff != "RIFF") return null;
                br.ReadInt32(); // file size
                string wave = ReadFourCC();
                if (wave != "WAVE") return null;

                int sampleRate = 0, numChannels = 0, bitsPerSample = 0;
                long dataSize = 0;

                while (fs.Position < fs.Length - 8)
                {
                    string chunkId = ReadFourCC();
                    if (string.IsNullOrEmpty(chunkId)) break;

                    int chunkSize = br.ReadInt32();
                    long paddedChunkSize = chunkSize + (chunkSize & 1);
                    long chunkEnd = fs.Position + paddedChunkSize;

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

                if (sampleRate == 0 || numChannels == 0 || bitsPerSample == 0 || dataSize == 0) return null;

                long totalSamples = dataSize / (numChannels * (bitsPerSample / 8));
                double durationMilliseconds = ((double)totalSamples / sampleRate) * 1000.0;

                return BitConverter.GetBytes(durationMilliseconds); // float64 LE
            }
            catch { return null; }
        }
        // sym_update : Fixed a bug where already downloaded YouTube songs could not be retrieved using hash values.
        // [Fix] 문자열을 절대 변하지 않는 고유 번호로 바꿔주는 함수
        private string GetStableStringHash(string str)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(str));
                return BitConverter.ToString(bytes).Replace("-", "").Substring(0, 10);
            }
        }
        // sym_update : Add LoadPath func
        // [Fix] JSON 파일을 덮어쓰기 저장하는 메서드
        public void SaveUiConfig(List<UiConfigJson> items)
        {
            try
            {
                string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_wem_config.json");
                string jsonString = System.Text.Json.JsonSerializer.Serialize(items, 
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, jsonString);
            }
            catch (Exception ex) { log("UiConfig Save Error: " + ex.Message); }
        }
        // [Fix] 빌드 성공 시, 사용된 경로들을 JSON에 업데이트하는 메서드
        public void UpdateSavedPaths(List<WemItemBase> targets)
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_wem_config.json");
            if (!File.Exists(jsonPath)) return;

            try
            {
                string jsonString = File.ReadAllText(jsonPath);
                var items = System.Text.Json.JsonSerializer.Deserialize<List<UiConfigJson>>(jsonString);
                if (items == null) return;

                bool updated = false;
                foreach (var t in targets)
                {
                    string targetName = Path.GetFileName(t.RelativePath);
                    // UI 항목 중에 일치하는 놈이 있으면 찾음
                    var cfg = items.FirstOrDefault(c => string.Equals(c.WemName, targetName, StringComparison.OrdinalIgnoreCase));
                    
                    // 기존에 저장된 경로랑 방금 빌드한 경로가 다르면 업데이트!
                    if (cfg != null && cfg.SavedPath != t.SourceAudioPath)
                    {
                        cfg.SavedPath = t.SourceAudioPath;
                        updated = true;
                    }
                }

                // 바뀐 게 하나라도 있으면 싹 저장
                if (updated)
                {
                    SaveUiConfig(items);
                    log("UiConfig: Saved paths updated successfully.");
                }
            }
            catch (Exception ex) { log("UpdateSavedPaths Error: " + ex.Message); }
        }
    }
}
