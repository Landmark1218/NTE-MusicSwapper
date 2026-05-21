using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.UE4.Wwise;
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

        public AudioService(string wwiseConsolePath, string unpackDir, GameService gameService, Action<string> log)
        {
            this.wwiseConsolePath = wwiseConsolePath;
            this.unpackDir = unpackDir;
            this.gameService = gameService;
            this.log = log;
            this.wprojPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wwise", "NTE.wproj");
        }

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
                if (items != null)
                {
                    foreach (var item in items)
                    {
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
                            FullPath = Path.Combine(unpackDir, item.WemName),
                            DisplayName = item.DisplayName,
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
            }
        }

        /// <summary>
        /// CUE4Parse で .bnk をパースし、キー名に "Duration" を含む全フィールドの
        /// (キー名, double値) ペアを収集して返す。
        /// </summary>
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