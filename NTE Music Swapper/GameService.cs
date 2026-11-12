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
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using MessageBoxOptions = System.Windows.MessageBoxOptions;

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
        private const string DisabledTitleModExtension = ".nteoff";

        public GameService(string unpackDir, Action<string> log)
        {
            this.unpackDir = unpackDir;
            this.log = log;
        }
        public Task<bool> WaitForGameFileAccessAndReleaseAsync(string targetPak, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            string targetLower = targetPak.ToLower();

            Thread monitorThread = new Thread(() =>
            {
                try
                {
                    string sessionName = "NtePakMonitorSession";
                    if (TraceEventSession.GetActiveSessionNames().Contains(sessionName))
                    {
                        var oldSession = new TraceEventSession(sessionName);
                        oldSession.Stop();
                    }

                    using var session = new TraceEventSession(sessionName);
                    session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO);

                    bool isAccessed = false;
                    ulong targetFileObject = 0;

                    session.Source.Kernel.FileIORead += data =>
                    {
                        if (!isAccessed && data.ProcessName.IndexOf("NTEGlobalGame", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (data.FileName != null && data.FileName.ToLower().Contains(targetLower))
                            {
                                Log($"[ETW] READ: {data.ProcessName} is scanning {Path.GetFileName(targetPak)}.");
                                isAccessed = true;
                                targetFileObject = data.FileObject;
                            }
                        }
                    };

                    session.Source.Kernel.FileIOCreate += data =>
                    {
                        if (!isAccessed && data.ProcessName.IndexOf("NTEGlobalGame", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (data.FileName != null && data.FileName.ToLower().Contains(targetLower))
                            {
                                Log($"[ETW] OPEN: {data.ProcessName} is opening {Path.GetFileName(targetPak)}.");
                                isAccessed = true;
                                targetFileObject = data.FileObject;
                            }
                        }
                    };

                    session.Source.Kernel.FileIOClose += data =>
                    {
                        if (isAccessed && data.ProcessName.IndexOf("NTEGlobalGame", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // FileObjectハンドルの一致、またはファイル名の一致で解放を検知
                            if ((data.FileObject != 0 && data.FileObject == targetFileObject) ||
                                (data.FileName != null && data.FileName.ToLower().Contains(targetLower)))
                            {
                                Log($"[ETW] CLOSED: {data.ProcessName} has finished accessing {Path.GetFileName(targetPak)}.");
                                session.Stop();
                                tcs.TrySetResult(true);
                            }
                        }
                    };

                    Task.Delay(timeoutMs).ContinueWith(_ =>
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            Log($"[ETW] Timeout reached ({(float)timeoutMs / 1000}s) for {Path.GetFileName(targetPak)}.");
                            try { session.Stop(); } catch { }
                            tcs.TrySetResult(false);
                        }
                    });

                    session.Source.Process(); // ブロッキング処理
                }
                catch (Exception ex)
                {
                    Log($"[ETW] Warning (Admin rights required for real-time monitor): {ex.Message}");
                    tcs.TrySetResult(false);
                }
            });

            monitorThread.IsBackground = true;
            monitorThread.Start();

            return tcs.Task;
        }

        // ── 安全なスワップ処理（ファイル消失＆汚染防止） ──
        public async Task<bool> SafeApplyModAsync(string pakPath, long modSize)
        {
            string bakFile = pakPath + ".bak";
            string modFile = pakPath + ".mod";

            if (!File.Exists(pakPath)) return false;
            if (!File.Exists(modFile))
            {
                Log($"Apply Error: MOD file not found -> {Path.GetFileName(modFile)}");
                return false;
            }

            long currentSize = new FileInfo(pakPath).Length;

            // .bak が存在する = 現在の .pak は前回のMOD なので、サイズが同じでも必ず上書きする
            bool isAlreadyBaked = File.Exists(bakFile);

            if (!isAlreadyBaked && modSize > 0 && currentSize == modSize)
            {
                Log($"Apply: File is already MOD state. ({Path.GetFileName(pakPath)})");
                return true;
            }

            int retryCount = 0;
            while (retryCount < 10)
            {
                try
                {
                    if (!File.Exists(bakFile))
                    {
                        File.Move(pakPath, bakFile);
                    }
                    else
                    {
                        Log($"Apply: Backup already exists. Discarding current pak (previous MOD).");
                        File.Delete(pakPath);
                    }

                    try
                    {
                        File.Move(modFile, pakPath);
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(pakPath) && File.Exists(bakFile))
                            File.Move(bakFile, pakPath);
                        throw;
                    }
                    Log($"Apply Success: Swap completed for {Path.GetFileName(pakPath)}");
                    return true;
                }
                catch (IOException)
                {
                    retryCount++;
                    await Task.Delay(500);
                }
            }
            Log($"Apply Error: File locked by process. Swap failed for {Path.GetFileName(pakPath)}");
            return false;
        }

        public async Task<bool> SafeRestoreVanillaAsync(string pakPath, long modSize)
        {
            string bakFile = pakPath + ".bak";
            string modFile = pakPath + ".mod";

            if (!File.Exists(pakPath)) return false;

            long currentSize = new FileInfo(pakPath).Length;
            if (modSize > 0 && currentSize != modSize)
            {
                Log($"Restore: File is not MOD state. Likely already Vanilla. ({Path.GetFileName(pakPath)})");
                return true;
            }

            if (!File.Exists(bakFile))
            {
                Log($"Restore Error: Original backup not found -> {Path.GetFileName(bakFile)}");
                return false;
            }

            int retryCount = 0;
            while (retryCount < 10)
            {
                try
                {
                    if (File.Exists(modFile)) File.Delete(modFile);
                    File.Move(pakPath, modFile);

                    try
                    {
                        File.Move(bakFile, pakPath);
                    }
                    catch (IOException)
                    {
                        File.Move(modFile, pakPath); // ロールバック
                        throw;
                    }
                    Log($"Restore Success: Vanilla restored for {Path.GetFileName(pakPath)}");
                    return true;
                }
                catch (IOException)
                {
                    retryCount++;
                    await Task.Delay(500);
                }
            }
            Log($"Restore Error: File locked by process. Restore failed for {Path.GetFileName(pakPath)}");
            return false;
        }
        // ────────────────────────────────────────

        public async Task<string> GetLatestVersionAsync()
        {
            try
            {
                using var hc = new HttpClient();
                hc.Timeout = TimeSpan.FromSeconds(5);
                var json = await hc.GetStringAsync(VersionUrl);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("LatestVersion").GetString() ?? "0.0";
            }
            catch (Exception ex)
            {
                Log("Version Check Error: " + ex.Message);
                return "0.0";
            }
        }

        public async Task DetectGameRootAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            if (!string.IsNullOrEmpty(appSettings.GameRootDir) &&
                Directory.Exists(appSettings.GameRootDir) &&
                File.Exists(Path.Combine(appSettings.GameRootDir, "NTEGlobalLauncher.exe")))
            {
                Log($"Cache: Loading game root from settings: {appSettings.GameRootDir}");
                GameRootDir = appSettings.GameRootDir;
            }
            else
            {
                Log("Scanning drives for Neverness To Everness folder (Fast Search)...");
                GameRootDir = await Task.Run(() => PerformFastGameSearch());

                if (string.IsNullOrEmpty(GameRootDir))
                {
                    Log("Warning: Could not automatically detect the game directory. Bypass and Backup functions may not work.");
                    return;
                }

                Log($"SUCCESS: Auto-detected game root at: {GameRootDir}");
                appSettings.GameRootDir = GameRootDir;
                saveSettingsCallback?.Invoke();
                Log("Cache: Game root path saved to settings.");
            }

            LauncherPath = Path.Combine(GameRootDir, "NTEGlobalLauncher.exe");

            var paksDirs = Directory.GetDirectories(GameRootDir, "Paks", SearchOption.AllDirectories);
            if (paksDirs.Length > 0)
            {
                OutputDir = paksDirs[0];
                Log($"SUCCESS: Paks folder detected at: {OutputDir}");
            }
            else
            {
                Log("Warning: Paks folder not found inside the game root.");
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

        public async Task CheckAndStartLauncherOnStartupAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            if (string.IsNullOrEmpty(OutputDir) || !Directory.Exists(OutputDir)) return;

            string originalPak = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
            string bakFile = originalPak + ".bak";
            string modFile = originalPak + ".mod";

            try
            {
                if (!File.Exists(originalPak) && File.Exists(modFile) && !File.Exists(bakFile))
                {
                    try { File.Delete(modFile); } catch { }
                    Log("Startup: Original PAK is missing but MOD file exists. Cleared corrupted MOD file.");

                    System.Windows.MessageBox.Show(
                        "mod file is crack.\n\n" +
                        "Please run the official launcher, proceed with [Game Update] or [Repair], and then rebuild the mod.\n" +
                        "Or recover using the file located in the paks path.",
                        "Mod File Corruption Notice",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (File.Exists(originalPak))
                {
                    long currentPakSize = new FileInfo(originalPak).Length;

                    if (appSettings.ModSize > 0 && currentPakSize == appSettings.ModSize)
                    {
                        Log("Warning: The tool was closed abnormally last time. Restoring to a clean state via Rename.");
                        await SafeRestoreVanillaAsync(originalPak, appSettings.ModSize);
                        currentPakSize = new FileInfo(originalPak).Length;
                    }

                    bool isGameUpdated = false;
                    if (appSettings.OriginalPakSize > 0 && appSettings.OriginalPakSize != currentPakSize)
                    {
                        isGameUpdated = true;
                    }

                    if (isGameUpdated)
                    {
                        Log($"Startup: Game update detected! (Current: {currentPakSize}, Old: {appSettings.OriginalPakSize})");

                        if (File.Exists(modFile))
                        {
                            File.Delete(modFile);
                            Log("Startup: Deleted outdated MOD file.");
                        }
                        RemoveGeneratedTitleModsFromGame();
                        ClearGeneratedTitleModCache();

                        System.Windows.MessageBox.Show(
                            "The game version has been updated, so the MOD has been removed.",
                            "Update Detected",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    if (appSettings.OriginalPakSize != currentPakSize)
                    {
                        appSettings.OriginalPakSize = currentPakSize;
                        saveSettingsCallback?.Invoke();
                    }
                }
            }
            catch (Exception ex) { Log("Startup Backup Error: " + ex.Message); }

            bool isLauncherRunning = IsProcessRunning("NTEGlobalGame");

            if (appSettings.SaveModEnabled && File.Exists(modFile))
            {
                Log("Startup: Auto-Save MOD is enabled. Applying saved MOD...");
                if (isLauncherRunning)
                {
                    Log("Startup: Launcher is already running. Applying MOD directly.");
                    await SafeApplyModAsync(originalPak, appSettings.ModSize);
                    ApplyGeneratedTitleModsToGame();
                }
                else
                {
                    await PerformBypassSwap(appSettings.ModApplyDelay, appSettings.ModSize);
                }
            }
            else
            {
                RemoveGeneratedTitleModsFromGame();
                if (!isLauncherRunning)
                {
                    if (!string.IsNullOrEmpty(LauncherPath) && File.Exists(LauncherPath))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(LauncherPath) { UseShellExecute = true });
                            Log("Startup: Launcher (NTEGlobalGame) started (No MOD).");
                        }
                        catch (Exception ex) { Log("Startup Launcher Error: " + ex.Message); }
                    }
                }
            }
        }

        public async Task PerformBypassSwap(int modApplyDelay, long modSize)
        {
            try
            {
                if (string.IsNullOrEmpty(OutputDir) || string.IsNullOrEmpty(LauncherPath)) return;

                string pakPath = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
                string bakFile = pakPath + ".bak";
                string modFile = pakPath + ".mod";

                try
                {
                    var weirdFiles = Directory.GetFiles(OutputDir, "pakchunk3-Windows.pak*")
                                              .Where(f => !f.Equals(pakPath, StringComparison.OrdinalIgnoreCase) &&
                                                          !f.Equals(bakFile, StringComparison.OrdinalIgnoreCase) &&
                                                          !f.Equals(modFile, StringComparison.OrdinalIgnoreCase));

                    foreach (var weirdFile in weirdFiles)
                    {
                        Log($"Found a file with an abnormal name: {Path.GetFileName(weirdFile)}");
                        if (File.Exists(pakPath)) File.Delete(pakPath);
                        File.Move(weirdFile, pakPath);
                        Log($"-> Repaired to correct extension ({Path.GetFileName(pakPath)}).");
                    }
                }
                catch (Exception ex)
                {
                    Log("An error occurred while repairing the filename: " + ex.Message);
                }

                if (!File.Exists(pakPath))
                {
                    Log("Error: Verification failed. PAK is missing.");
                    return;
                }
                Log("Bypass: File verification success. Starting Launcher...");

                // 【修正】ランチャーを起動する「直前」にETW監視を開始し、起動時のアクセスを確実に捉える
                Task<bool> monitorTask = WaitForGameFileAccessAndReleaseAsync(pakPath, 30000); // 最大30秒待機

                try { Process.Start(new ProcessStartInfo(LauncherPath) { UseShellExecute = true }); }
                catch (Exception ex) { Log("Launcher Start Error: " + ex.Message); return; }

                Log("Bypass: Waiting for Launcher window to appear...");
                while (!IsLauncherWindowVisible("NTEGlobalGame")) await Task.Delay(100);

                Log($"Bypass: Launcher window detected! Waiting for ETW verification to finish...");

                // 監視タスクの結果を待つ
                bool monitorSuccess = await monitorTask;
                if (!monitorSuccess)
                {
                    Log("Bypass Warning: ETW monitor timed out or failed (Admin mode?). Applying safe delay fallback...");
                    await Task.Delay(modApplyDelay);
                }

                if (File.Exists(modFile))
                {
                    if (File.Exists(pakPath))
                    {
                        bool applied = await SafeApplyModAsync(pakPath, modSize);
                        if (applied)
                        {
                            ApplyGeneratedTitleModsToGame();
                            System.Windows.MessageBox.Show("Success Mod Apply\nYou can now run the game from the launcher.", "Success Mod Apply",
                                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
                        }
                    }
                    else
                    {
                        File.Move(modFile, pakPath);
                        ApplyGeneratedTitleModsToGame();
                        System.Windows.MessageBox.Show(".pak is missing. you are maybe game repair", ".pak is missing.",
                            MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
                        Log("Warning: originFile is missing. you are maybe game repair");
                    }
                }
                else
                {
                    Log("Warning: Mod file not found. Bypass aborted.");
                }

            }
            catch (Exception ex) { Log("Bypass Swap Error: " + ex.Message); }
        }

        public async Task<long> PackingProcess(string base64Key)
        {
            if (string.IsNullOrEmpty(OutputDir)) return 0;

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
            string modFile = pakPath + ".mod";

            string upakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
            await RunProcessAsync(upakExe, $"\"{modFile}\" -create=\"{resPath}\" -cryptokeys=\"{cryptoPath}\"");

            if (File.Exists(modFile))
            {
                long modSize = new FileInfo(modFile).Length;

                if (File.Exists(pakPath))
                {
                    await SafeApplyModAsync(pakPath, modSize);
                    Log("Build: Created Mod and applied safely.");
                }
                return modSize;
            }
            return 0;
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
                        Log($"Killed process: {p.ProcessName}");
                    }
                }
                catch { }
            }
        }

        public void KillNteGlobalLauncher() => KillProcessesContainingName("NTEGlobalGame");

        public string PatchPaksDir { get; private set; } = "";

        public void SetPatchPaksDir(string gameRootDir)
        {
            if (string.IsNullOrEmpty(gameRootDir)) return;
            PatchPaksDir = Path.Combine(gameRootDir, "Client", "WindowsNoEditor", "HT", "Content", "PatchPaks");
        }

        public async Task CheckPatchPakOnStartupAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            if (string.IsNullOrEmpty(PatchPaksDir) || !Directory.Exists(PatchPaksDir)) return;

            string originalPak = Path.Combine(PatchPaksDir, "pakchunk3-Windows_0_P.pak");
            string bakFile = originalPak + ".bak";
            string modFile = originalPak + ".mod";

            try
            {
                if (!File.Exists(originalPak) && File.Exists(modFile) && !File.Exists(bakFile))
                {
                    try { File.Delete(modFile); } catch { }
                    Log("PatchPak Startup: Original PAK is missing but MOD file exists. Cleared corrupted MOD file.");
                    System.Windows.MessageBox.Show(
                        "PatchPak mod file is corrupted.\n\n" +
                        "Please run the official launcher, proceed with [Game Update] or [Repair], and then rebuild the mod.\n" +
                        "Or recover using the file located in the PatchPaks path.",
                        "PatchPak Mod File Corruption Notice",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (File.Exists(originalPak))
                {
                    long currentPakSize = new FileInfo(originalPak).Length;

                    if (appSettings.PatchModSize > 0 && currentPakSize == appSettings.PatchModSize)
                    {
                        Log("PatchPak Startup: Tool closed abnormally last time. Restoring via SafeRename.");
                        await SafeRestoreVanillaAsync(originalPak, appSettings.PatchModSize);
                        currentPakSize = new FileInfo(originalPak).Length;
                    }

                    bool isGameUpdated = false;
                    if (appSettings.OriginalPatchPakSize > 0 && appSettings.OriginalPatchPakSize != currentPakSize)
                        isGameUpdated = true;

                    if (isGameUpdated)
                    {
                        Log($"PatchPak Startup: Game update detected! (Current: {currentPakSize}, Old: {appSettings.OriginalPatchPakSize})");
                        if (File.Exists(modFile))
                        {
                            File.Delete(modFile);
                            Log("PatchPak Startup: Deleted outdated MOD file.");
                        }
                        RemoveGeneratedTitleModsFromGame();
                        ClearGeneratedTitleModCache();
                        System.Windows.MessageBox.Show(
                            "The game version has been updated, so the PatchPak MOD has been removed.",
                            "PatchPak Update Detected",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    if (appSettings.OriginalPatchPakSize != currentPakSize)
                    {
                        appSettings.OriginalPatchPakSize = currentPakSize;
                        saveSettingsCallback?.Invoke();
                    }
                }
            }
            catch (Exception ex) { Log("PatchPak Startup Backup Error: " + ex.Message); }

            bool isLauncherRunning = IsProcessRunning("NTEGlobalGame");
            if (appSettings.SaveModEnabled && File.Exists(modFile))
            {
                Log("PatchPak Startup: Auto-Save MOD is enabled. Applying saved PatchPak MOD...");
                if (isLauncherRunning)
                {
                    Log("PatchPak Startup: Launcher is already running. Applying PatchPak MOD directly.");
                    await SafeApplyModAsync(originalPak, appSettings.PatchModSize);
                    ApplyGeneratedTitleModsToGame();
                }
                else
                {
                    await PerformPatchPakBypassSwap(appSettings.ModApplyDelay, appSettings.PatchModSize);
                }
            }
        }

        public async Task PerformPatchPakBypassSwap(int modApplyDelay, long modSize)
        {
            try
            {
                if (string.IsNullOrEmpty(PatchPaksDir) || string.IsNullOrEmpty(LauncherPath)) return;

                string pakPath = Path.Combine(PatchPaksDir, "pakchunk3-Windows_0_P.pak");
                string bakFile = pakPath + ".bak";
                string modFile = pakPath + ".mod";

                try
                {
                    var weirdFiles = Directory.GetFiles(PatchPaksDir, "pakchunk3-Windows_0_P.pak*")
                                              .Where(f => !f.Equals(pakPath, StringComparison.OrdinalIgnoreCase) &&
                                                          !f.Equals(bakFile, StringComparison.OrdinalIgnoreCase) &&
                                                          !f.Equals(modFile, StringComparison.OrdinalIgnoreCase));
                    foreach (var weirdFile in weirdFiles)
                    {
                        Log($"PatchPak: Found a file with an abnormal name: {Path.GetFileName(weirdFile)}");
                        if (File.Exists(pakPath)) File.Delete(pakPath);
                        File.Move(weirdFile, pakPath);
                        Log($"PatchPak: -> Repaired to correct extension ({Path.GetFileName(pakPath)}).");
                    }
                }
                catch (Exception ex) { Log("PatchPak: An error occurred while repairing the filename: " + ex.Message); }

                if (!File.Exists(pakPath))
                {
                    Log("PatchPak Bypass: File verification failed. PAK is missing.");
                    return;
                }

                Task<bool> monitorTask = WaitForGameFileAccessAndReleaseAsync(pakPath, 5000); // 5秒タイムアウト

                Log("PatchPak Bypass: Waiting for Launcher window to appear...");
                while (!IsLauncherWindowVisible("NTEGlobalGame")) await Task.Delay(100);

                Log($"PatchPak Bypass: Launcher window detected! Waiting for ETW verification to finish...");

                bool monitorSuccess = await monitorTask;
                if (!monitorSuccess)
                {
                    Log("PatchPak Bypass Warning: ETW monitor timed out (likely already verified). Applying safe delay fallback...");
                    await Task.Delay(modApplyDelay);
                }

                if (File.Exists(modFile))
                {
                    if (File.Exists(pakPath))
                    {
                        bool applied = await SafeApplyModAsync(pakPath, modSize);
                        if (applied)
                            ApplyGeneratedTitleModsToGame();
                    }
                    else
                    {
                        File.Move(modFile, pakPath);
                        ApplyGeneratedTitleModsToGame();
                        Log("PatchPak Bypass Warning: originFile was missing. PatchPak MOD moved to paks.");
                    }
                }
                else
                {
                    Log("PatchPak Bypass Warning: PatchPak MOD file not found. Bypass aborted.");
                }
            }
            catch (Exception ex) { Log("PatchPak Bypass Swap Error: " + ex.Message); }
        }

        public async Task<long> PackingProcessPatchPak(string base64Key, string unpak1Dir)
        {
            if (string.IsNullOrEmpty(PatchPaksDir)) return 0;

            string cryptoPath = Path.Combine(Path.GetTempPath(), "nte_crypto_patch.json");
            var crypto = new
            {
                EncryptionKey = new { Guid = "00000000000000000000000000000000", Name = "NTEKey", Key = base64Key },
                bDataCryptoRequired = false,
                bEnablePakIndexEncryption = true,
                bEnablePakEntryEncryption = false,
                bEnablePakSigning = false
            };
            File.WriteAllText(cryptoPath, JsonSerializer.Serialize(crypto));

            string resPath = Path.Combine(Path.GetTempPath(), "pak_response_patch.txt");
            using (var sw = new StreamWriter(resPath, false, new UTF8Encoding(false)))
            {
                foreach (string f in Directory.GetFiles(unpak1Dir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(unpak1Dir.Length).TrimStart('\\').Replace('\\', '/');
                    sw.WriteLine($"\"{f}\" \"../../../HT/{rel}\"");
                }
            }

            string pakPath = Path.Combine(PatchPaksDir, "pakchunk3-Windows_0_P.pak");
            string modFile = pakPath + ".mod";

            string upakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
            await RunProcessAsync(upakExe, $"\"{modFile}\" -create=\"{resPath}\" -cryptokeys=\"{cryptoPath}\"");

            if (File.Exists(modFile))
            {
                long modSize = new FileInfo(modFile).Length;

                if (File.Exists(pakPath))
                {
                    await SafeApplyModAsync(pakPath, modSize);
                    Log("PatchPak Build: Created Mod and applied safely.");
                }
                return modSize;
            }
            return 0;
        }

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
            p.OutputDataReceived += (s, e) => { if (e.Data != null) log?.Invoke("> " + e.Data); };
            p.Exited += (s, e) => { tcs.SetResult(true); p.Dispose(); };
            p.Start();
            p.BeginOutputReadLine();
            return tcs.Task;
        }

        public string GetGeneratedTitleModCacheDir(string modBaseName)
        {
            string modsRoot = string.IsNullOrWhiteSpace(OutputDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TitleMods")
                : Path.Combine(OutputDir, "~mods");
            return Path.Combine(modsRoot, modBaseName);
        }

        public void ClearGeneratedTitleModCache()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDir) || !Directory.Exists(OutputDir))
                    return;

                string modsRoot = Path.Combine(OutputDir, "~mods");
                if (!Directory.Exists(modsRoot))
                    return;

                foreach (string dir in EnumerateGeneratedTitleModDirs(modsRoot))
                {
                    Directory.Delete(dir, true);
                    Log($"TitleMods: Deleted generated title mod cache: {Path.GetFileName(dir)}");
                }
            }
            catch (Exception ex)
            {
                Log("TitleMods: Failed to clear generated title mod cache: " + ex.Message);
            }
        }

        public void ApplyGeneratedTitleModsToGame()
        {
            if (string.IsNullOrWhiteSpace(OutputDir) || !Directory.Exists(OutputDir))
                return;

            string modsRoot = Path.Combine(OutputDir, "~mods");
            SetGeneratedTitleModsActive(modsRoot, active: true);
        }

        public void RemoveGeneratedTitleModsFromGame()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDir) || !Directory.Exists(OutputDir))
                    return;

                string modsRoot = Path.Combine(OutputDir, "~mods");
                if (!Directory.Exists(modsRoot))
                    return;

                SetGeneratedTitleModsActive(modsRoot, active: false);
            }
            catch (Exception ex)
            {
                Log("TitleMods: Failed to deactivate generated title mods in game ~mods: " + ex.Message);
            }
        }

        private void SetGeneratedTitleModsActive(string modsRoot, bool active)
        {
            foreach (string dir in EnumerateGeneratedTitleModDirs(modsRoot))
            {
                try
                {
                    int changed = 0;
                    foreach (string file in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(file);
                        bool isActiveContainer =
                            ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase);
                        bool isDisabledContainer = file.EndsWith(DisabledTitleModExtension, StringComparison.OrdinalIgnoreCase);

                        if (active && isDisabledContainer)
                        {
                            string activePath = file[..^DisabledTitleModExtension.Length];
                            if (!File.Exists(activePath))
                            {
                                File.Move(file, activePath);
                                changed++;
                            }
                        }
                        else if (!active && isActiveContainer)
                        {
                            string disabledPath = file + DisabledTitleModExtension;
                            if (!File.Exists(disabledPath))
                            {
                                File.Move(file, disabledPath);
                                changed++;
                            }
                        }
                    }

                    if (changed > 0)
                    {
                        string state = active ? "Enabled" : "Disabled";
                        Log($"TitleMods: {state} generated title mod: {Path.GetFileName(dir)}");
                    }
                }
                catch (Exception ex)
                {
                    string action = active ? "enable" : "disable";
                    Log($"TitleMods: Failed to {action} {Path.GetFileName(dir)}: {ex.Message}");
                }
            }
        }

        private static IEnumerable<string> EnumerateGeneratedTitleModDirs(string modsRoot)
        {
            if (!Directory.Exists(modsRoot))
                return [];

            return Directory.GetDirectories(modsRoot, "NteMusicSwapper_*", SearchOption.TopDirectoryOnly)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void Log(string m) => log?.Invoke(m);
    }
}
