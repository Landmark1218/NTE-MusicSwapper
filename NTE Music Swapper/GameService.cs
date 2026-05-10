using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace NtePakTool
{
    public class GameService
    {
        private readonly string AESUrl = "https://github.com/Landmark1218/Trash/raw/refs/heads/main/AES.json";
        private readonly string unpackDir;
        private readonly string VersionUrl = "https://github.com/Landmark1218/Trash/raw/refs/heads/main/Version.json";
        private readonly Action<string> log;

        public string OutputDir { get; private set; } = "";
        public string LauncherPath { get; private set; } = "";
        public string GameRootDir { get; private set; } = "";

        public GameService(string unpackDir, Action<string> log)
        {
            this.unpackDir = unpackDir;
            this.log = log;
        }
        public async Task<string> GetLatestVersionAsync()
        {
            try
            {
                using var hc = new HttpClient();
                hc.Timeout = TimeSpan.FromSeconds(5); // 起動が遅くならないよう5秒でタイムアウトさせる
                var json = await hc.GetStringAsync(VersionUrl);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("LatestVersion").GetString() ?? "0.0";
            }
            catch (Exception ex)
            {
                log("Version Check Error: " + ex.Message);
                return "0.0"; // オフライン時などはエラーにせず0.0を返してチェックをスキップさせる
            }
        }

        //ゲーム検出
        public async Task DetectGameRootAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            // キャッシュされたパスが有効かチェック
            if (!string.IsNullOrEmpty(appSettings.GameRootDir) &&
                Directory.Exists(appSettings.GameRootDir) &&
                File.Exists(Path.Combine(appSettings.GameRootDir, "NTEGlobalLauncher.exe")))
            {
                log($"Cache: Loading game root from settings: {appSettings.GameRootDir}");
                GameRootDir = appSettings.GameRootDir;
            }
            else
            {
                // キャッシュがない or 無効なので全ドライブをスキャン
                log("Scanning drives for Neverness To Everness folder (Fast Search)...");
                GameRootDir = await Task.Run(() => PerformFastGameSearch());

                if (string.IsNullOrEmpty(GameRootDir))
                {
                    log("Warning: Could not automatically detect the game directory. Bypass and Backup functions may not work.");
                    return;
                }

                log($"SUCCESS: Auto-detected game root at: {GameRootDir}");

                // 検出結果をJSONに保存
                appSettings.GameRootDir = GameRootDir;
                saveSettingsCallback?.Invoke();
                log("Cache: Game root path saved to settings.");
            }

            LauncherPath = Path.Combine(GameRootDir, "NTEGlobalLauncher.exe");

            var paksDirs = Directory.GetDirectories(GameRootDir, "Paks", SearchOption.AllDirectories);
            if (paksDirs.Length > 0)
            {
                OutputDir = paksDirs[0];
                log($"SUCCESS: Paks folder detected at: {OutputDir}");
            }
            else
            {
                log("Warning: Paks folder not found inside the game root.");
            }
        }

        private string PerformFastGameSearch()
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName);

            string[] skipFolders = { "Windows", "ProgramData", "System Volume Information", "$RECYCLE.BIN", "AppData", "Recovery" };

            foreach (var drive in drives)
            {
                var queue = new Queue<string>();
                queue.Enqueue(drive);

                while (queue.Count > 0)
                {
                    string currentDir = queue.Dequeue();
                    try
                    {
                        string dirName = Path.GetFileName(currentDir);
                        if (dirName.Equals("Neverness To Everness", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(Path.Combine(currentDir, "NTEGlobal")) &&
                                Directory.Exists(Path.Combine(currentDir, "Client")) &&
                                File.Exists(Path.Combine(currentDir, "NTEGlobalLauncher.exe")) &&
                                File.Exists(Path.Combine(currentDir, "uninst.exe")))
                            {
                                return currentDir;
                            }
                        }
                        foreach (var sub in Directory.GetDirectories(currentDir))
                        {
                            string subName = Path.GetFileName(sub);
                            if (!skipFolders.Contains(subName, StringComparer.OrdinalIgnoreCase))
                                queue.Enqueue(sub);
                        }
                    }
                    catch { }
                }
            }
            return string.Empty;
        }

        //PAK操作
        // 引数に Action saveSettingsCallback を追加
        public async Task CheckAndStartLauncherOnStartupAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            if (string.IsNullOrEmpty(OutputDir) || !Directory.Exists(OutputDir)) return;

            string bakDir = Path.Combine(OutputDir, "bak");
            string bakFile = Path.Combine(bakDir, "pakchunk3-Windows.pak.bak");
            string originalPak = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
            string modBakFile = Path.Combine(OutputDir, "mods", "pakchunk3-Windows.pak.bak");

            try
            {
                if (!Directory.Exists(bakDir)) Directory.CreateDirectory(bakDir);

                if (File.Exists(originalPak))
                {
                    long currentPakSize = new FileInfo(originalPak).Length;
                    long modPakSize = File.Exists(modBakFile) ? new FileInfo(modBakFile).Length : 0;
                    bool isBackupMissing = !File.Exists(bakFile);

                    // 【安全装置】現在のpakサイズが保存されているMODのサイズと完全に一致する場合、
                    // 前回異常終了等でMODが解除されずに残ってしまった可能性が高いため、バックアップ更新をスキップしてクリーンな状態に復元する
                    if (appSettings.OriginalPakSize > 0 && currentPakSize == modPakSize && currentPakSize != appSettings.OriginalPakSize)
                    {
                        log("Warning: paksフォルダのファイルがMODの残骸である可能性を検出しました。クリーンなバックアップの上書きを防止します。");
                        if (!isBackupMissing)
                        {
                            File.Copy(bakFile, originalPak, true);
                            currentPakSize = new FileInfo(originalPak).Length; // 復元後にサイズを再取得
                            log("Startup: MODの残骸を削除し、バックアップから正規のPAKを復元しました。");
                        }
                    }

                    // bakフォルダにあるバックアップファイルのサイズを取得
                    long bakFileSize = isBackupMissing ? 0 : new FileInfo(bakFile).Length;

                    // 【アップデート判定】
                    // paksのpakサイズとbakのbakサイズが異なる場合、またはJSONの記録サイズと異なる場合
                    bool isGameUpdated = false;
                    if (!isBackupMissing && currentPakSize != bakFileSize)
                    {
                        isGameUpdated = true;
                    }
                    else if (appSettings.OriginalPakSize > 0 && appSettings.OriginalPakSize != currentPakSize)
                    {
                        isGameUpdated = true;
                    }

                    if (isBackupMissing || isGameUpdated)
                    {
                        if (isGameUpdated)
                        {
                            log($"Startup: Game update detected! (Current: {currentPakSize}, Backup: {bakFileSize})");

                            // 【追加】アップデートを検知した場合、modsフォルダの古いバックアップを削除する
                            if (File.Exists(modBakFile))
                            {
                                File.Delete(modBakFile);
                                log("Startup: Deleted outdated MOD file from mods folder.");
                            }

                            log("Startup: Recreating clean PAK backup...");
                            System.Windows.MessageBox.Show(
                                "The game version has been updated, so the MOD has been removed.",
                                "Update Detected",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        else
                        {
                            log("Startup: Creating original PAK backup...");
                        }

                        // バックアップを作成（または上書き）
                        File.Copy(originalPak, bakFile, true);

                        // JSONのサイズ情報を更新して保存
                        appSettings.OriginalPakSize = currentPakSize;
                        saveSettingsCallback?.Invoke();

                        log("Startup: Backup creation and size update complete.");
                    }
                }
            }
            catch (Exception ex) { log("Startup Backup Error: " + ex.Message); }

            bool isLauncherRunning = IsProcessRunning("NTEGlobalGame");

            if (appSettings.SaveModEnabled && File.Exists(modBakFile))
            {
                log("Startup: Auto-Save MOD is enabled. Applying saved MOD...");
                if (isLauncherRunning)
                {
                    log("Startup: Launcher is already running. Applying MOD directly.");
                    try
                    {
                        if (File.Exists(originalPak)) File.Delete(originalPak);
                        File.Copy(modBakFile, originalPak, true);
                        log("Startup: Mod applied successfully.");
                    }
                    catch (Exception ex) { log("Startup Mod Apply Error: " + ex.Message); }
                }
                else
                {
                    await PerformBypassSwap();
                }
            }
            else
            {
                if (!isLauncherRunning)
                {
                    if (!string.IsNullOrEmpty(LauncherPath) && File.Exists(LauncherPath))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(LauncherPath) { UseShellExecute = true });
                            log("Startup: Launcher (NTEGlobalGame) started (No MOD).");
                        }
                        catch (Exception ex) { log("Startup Launcher Error: " + ex.Message); }
                    }
                }
            }
        }
        public async Task PerformBypassSwap()
        {
            try
            {
                if (string.IsNullOrEmpty(OutputDir) || string.IsNullOrEmpty(LauncherPath)) return;

                string paksDir = OutputDir;
                string bakFile = Path.Combine(OutputDir, "bak", "pakchunk3-Windows.pak.bak");
                string modBakFile = Path.Combine(OutputDir, "mods", "pakchunk3-Windows.pak.bak");
                string pakPath = Path.Combine(paksDir, "pakchunk3-Windows.pak");

                //異常な名前のファイルを修復
                try
                {
                    var weirdFiles = Directory.GetFiles(paksDir, "pakchunk3-Windows.pak*")
                                              .Where(f => !f.Equals(pakPath, StringComparison.OrdinalIgnoreCase));

                    foreach (var weirdFile in weirdFiles)
                    {
                        log($"異常な名前のファイルを発見しました: {Path.GetFileName(weirdFile)}");
                        if (File.Exists(pakPath)) File.Delete(pakPath);
                        File.Move(weirdFile, pakPath);
                        log($"-> 正常な拡張子 ({Path.GetFileName(pakPath)}) に修復しました。");
                    }
                }
                catch (Exception ex)
                {
                    log("ファイル名の修復中にエラーが発生しました: " + ex.Message);
                }

                if (File.Exists(pakPath))
                {
                    File.Delete(pakPath);
                    log("Bypass: Deleted existing file in paks folder.");
                }

                if (File.Exists(bakFile))
                {
                    File.Copy(bakFile, pakPath, true);
                    log("Bypass: Clean PAK copied to paks folder.");
                }
                else
                {
                    log("Critical Error: Backup original file not found in bak!");
                    return;
                }

                if (!File.Exists(pakPath))
                {
                    log("Error: Verification failed. PAK is missing.");
                    return;
                }
                log("Bypass: File verification success. Starting Launcher...");

                try { Process.Start(new ProcessStartInfo(LauncherPath) { UseShellExecute = true }); }
                catch (Exception ex) { log("Launcher Start Error: " + ex.Message); return; }

                log("Bypass: Waiting for Launcher window to appear...");
                while (!IsLauncherWindowVisible("NTEGlobalGame")) await Task.Delay(100);

                log("Bypass: Launcher window detected! Waiting 0.6s for mod injection...");
                await Task.Delay(600);

                if (File.Exists(modBakFile))
                {
                    if (File.Exists(pakPath)) File.Delete(pakPath);
                    File.Copy(modBakFile, pakPath, true);
                    log("Bypass Complete: Mod applied successfully.");
                }
                else
                {
                    log("Warning: Mod backup not found in mods folder. Bypass aborted.");
                }
            }
            catch (Exception ex) { log("Bypass Swap Error: " + ex.Message); }
        }

        public async Task PackingProcess(string base64Key)
        {
            if (string.IsNullOrEmpty(OutputDir)) return;

            string cryptoPath = Path.Combine(Path.GetTempPath(), "nte_crypto.json");
            var crypto = new
            {
                EncryptionKey = new { Guid = "00000000000000000000000000000000", Name = "NTEKey", Key = base64Key },
                bDataCryptoRequired = false,
                bEnablePakIndexEncryption = true,
                bEnablePakEntryEncryption = false,
                bEnablePakSigning = false
            };
            File.WriteAllText(cryptoPath, JsonSerializer.Serialize(crypto));

            string resPath = Path.Combine(Path.GetTempPath(), "pak_response.txt");
            using (var sw = new StreamWriter(resPath, false, new UTF8Encoding(false)))
            {
                foreach (string f in Directory.GetFiles(unpackDir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(unpackDir.Length).TrimStart('\\').Replace('\\', '/');
                    sw.WriteLine($"\"{f}\" \"../../../HT/{rel}\"");
                }
            }

            string pakPath = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
            string upakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
            await RunProcessAsync(upakExe, $"\"{pakPath}\" -create=\"{resPath}\" -cryptokeys=\"{cryptoPath}\"");

            string modsDir = Path.Combine(OutputDir, "mods");
            string modBakFile = Path.Combine(modsDir, "pakchunk3-Windows.pak.bak");

            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

            if (File.Exists(pakPath))
            {
                File.Copy(pakPath, modBakFile, true);
                log("Build: Created Mod backup in mods folder.");
            }
        }

        public async Task<string> GetAesKeyBase64Async()
        {
            using var hc = new HttpClient();
            var json = await hc.GetStringAsync(AESUrl);
            using var doc = JsonDocument.Parse(json);
            string hex = (doc.RootElement.GetProperty("aes_key").GetString() ?? "").Replace("0x", "");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return Convert.ToBase64String(bytes);
        }

        //プロセス管理
        public bool IsProcessRunning(string keyword) =>
            Process.GetProcesses().Any(p => p.ProcessName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

        public void KillProcessesContainingName(string keyword)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                        log($"Killed process: {p.ProcessName}");
                    }
                }
                catch { }
            }
        }

        public void KillNteGlobalLauncher() => KillProcessesContainingName("NTEGlobalGame");

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public bool IsLauncherWindowVisible(string keyword)
        {
            var processes = Process.GetProcesses()
                .Where(p => p.ProcessName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var p in processes)
            {
                if (p.MainWindowHandle != IntPtr.Zero &&
                    !string.IsNullOrEmpty(p.MainWindowTitle) &&
                    IsWindowVisible(p.MainWindowHandle))
                    return true;
            }
            return false;
        }

        //ユーティリティ
        public Task RunProcessAsync(string exe, string arg)
        {
            var tcs = new TaskCompletionSource<bool>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, arg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) log("> " + e.Data); };
            p.Exited += (s, e) => { tcs.SetResult(true); p.Dispose(); };
            p.Start();
            p.BeginOutputReadLine();
            return tcs.Task;
        }
    }
}
