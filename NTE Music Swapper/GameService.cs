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
                hc.Timeout = TimeSpan.FromSeconds(5); // Timeout after 5 seconds to avoid slow startup
                var json = await hc.GetStringAsync(VersionUrl);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("LatestVersion").GetString() ?? "0.0";
            }
            catch (Exception ex)
            {
                log("Version Check Error: " + ex.Message);
                return "0.0"; // Return 0.0 on failure (e.g. offline) to skip the version check without error
            }
        }

        // Game detection
        public async Task DetectGameRootAsync(AppSettingsJson appSettings, Action saveSettingsCallback)
        {
            // Check if the cached path is still valid
            if (!string.IsNullOrEmpty(appSettings.GameRootDir) &&
                Directory.Exists(appSettings.GameRootDir) &&
                File.Exists(Path.Combine(appSettings.GameRootDir, "NTEGlobalLauncher.exe")))
            {
                log($"Cache: Loading game root from settings: {appSettings.GameRootDir}");
                GameRootDir = appSettings.GameRootDir;
            }
            else
            {
                // No cache or cache is invalid — scan all drives
                log("Scanning drives for Neverness To Everness folder (Fast Search)...");
                GameRootDir = await Task.Run(() => PerformFastGameSearch());

                if (string.IsNullOrEmpty(GameRootDir))
                {
                    log("Warning: Could not automatically detect the game directory. Bypass and Backup functions may not work.");
                    return;
                }

                log($"SUCCESS: Auto-detected game root at: {GameRootDir}");

                // Save detection result to JSON
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

        // PAK operations
        // Added Action saveSettingsCallback parameter
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

                // sym_update : for file swap system
                // 원본 폴더에 .pak가 없는데 mods 폴더에만 파일이 남아있는 경우 (파일 증발 버그 상태)
                // Case where the .pak file does not exist in the source folder but remains only in the mods folder (file disappearance bug)
                if (!File.Exists(originalPak) && File.Exists(modBakFile))
                {
                    try { File.Delete(modBakFile); } catch { } // 깨진(남겨진) 모드 파일 찌꺼기 제거
                    log("Startup: Original PAK is missing but MOD file(but it's vanilna file) exists. Cleared corrupted MOD file.");
            
                    System.Windows.MessageBox.Show(
                        "mod file is crack.\n\n" +
                        "Please run the official launcher, proceed with [Game Update] or [Repair], and then rebuild the mod.\n" +
                        "Or recover using the file located in the /bak path.",
                        "Mod File Corruption Notice",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return; // 꼬임 방지를 위해 더 이상 아래 코드를 실행하지 않고 툴 로직 중단
                }
                
                if (File.Exists(originalPak))
                {
                    long currentPakSize = new FileInfo(originalPak).Length;
                    long modPakSize = File.Exists(modBakFile) ? new FileInfo(modBakFile).Length : 0;
                    bool isBackupMissing = !File.Exists(bakFile);

                    
                    // [Safety guard] If the current PAK size exactly matches the saved MOD size,
                    // it is likely that the MOD was not removed due to an abnormal exit last time.
                    // Skip updating the backup and restore to a clean state instead.
                    
                    // sym_update : change safety guard logic for file swap system
                    #region origin safety guard code

                    
                    // if (appSettings.OriginalPakSize > 0 && currentPakSize == modPakSize && currentPakSize != appSettings.OriginalPakSize)
                    // {
                    //     log("Warning: The file in the paks folder may be a MOD remnant. Preventing overwrite of the clean backup.");
                    //     if (!isBackupMissing)
                    //     {
                    //         File.Copy(bakFile, originalPak, true);
                    //         currentPakSize = new FileInfo(originalPak).Length; // Re-fetch size after restoration
                    //         log("Startup: Removed MOD remnant and restored the original PAK from backup.");
                    //     }
                    // }

                    #endregion
                    if (appSettings.OriginalPakSize > 0 && modPakSize == appSettings.OriginalPakSize && currentPakSize != appSettings.OriginalPakSize)
                    {
                        log("Warning: The tool was closed abnormally last time. Restoring to a clean state via Swap.");
    
                        // 복사(Copy) 교환(Swap)으로 원상복구
                        string tempPak = originalPak + ".tmp";
                        if (File.Exists(originalPak) && File.Exists(modBakFile))
                        {
                            if (File.Exists(tempPak)) File.Delete(tempPak);
                            File.Move(originalPak, tempPak);       // 1. paks(모드) -> 임시
                            File.Move(modBakFile, originalPak);    // 2. mods(순정) -> paks (순정 복구!)
                            File.Move(tempPak, modBakFile);        // 3. 임시(모드) -> mods (모드 백업 복구!)
        
                            currentPakSize = new FileInfo(originalPak).Length; // 복구 후 용량 갱신
                            log("Startup: Recovered safely from abnormal exit.");
                        }
                    }

                    // Get the size of the backup file in the bak folder
                    long bakFileSize = isBackupMissing ? 0 : new FileInfo(bakFile).Length;

                    // [Update detection]
                    // Triggered when the PAK size in paks differs from the .bak size, or differs from the size recorded in JSON
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

                            // [Added] When an update is detected, delete the outdated backup from the mods folder
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

                        // Create (or overwrite) the backup
                        File.Copy(originalPak, bakFile, true);

                        // Update and save the size info in JSON
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
                        // sym_update : for file swap system
                        #region origin code

                        // if (File.Exists(originalPak)) File.Delete(originalPak);
                        // File.Copy(modBakFile, originalPak, true);
                        // log("Startup: Mod applied successfully.");

                        #endregion
                        string tempPak = originalPak + ".tmp";
                        if (File.Exists(originalPak))
                        {
                            if (File.Exists(tempPak)) File.Delete(tempPak);
                            File.Move(originalPak, tempPak);       // 1. 원본(순정) -> 임시
                            File.Move(modBakFile, originalPak);    // 2. mods(모드) -> 원본 (적용 완료!)
                            File.Move(tempPak, modBakFile);        // 3. 임시(순정) -> mods (순정 보관 완료!)
                        }
                        log("Startup: Mod applied successfully via Swap.");
                    }
                    catch (Exception ex) { log("Startup Mod Apply Error: " + ex.Message); }
                }
                else
                {
                    await PerformBypassSwap(appSettings.ModApplyDelay);
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
        public async Task PerformBypassSwap(int modApplyDelay)
        {
            try
            {
                if (string.IsNullOrEmpty(OutputDir) || string.IsNullOrEmpty(LauncherPath)) return;

                string paksDir = OutputDir;
                string bakFile = Path.Combine(OutputDir, "bak", "pakchunk3-Windows.pak.bak");
                string modBakFile = Path.Combine(OutputDir, "mods", "pakchunk3-Windows.pak.bak");
                string pakPath = Path.Combine(paksDir, "pakchunk3-Windows.pak");
                string tempPak = pakPath + ".tmp"; // sym_update : tempPath for file swap

                // Repair files with abnormal names
                try
                {
                    var weirdFiles = Directory.GetFiles(paksDir, "pakchunk3-Windows.pak*")
                                              .Where(f => !f.Equals(pakPath, StringComparison.OrdinalIgnoreCase));

                    foreach (var weirdFile in weirdFiles)
                    {
                        log($"Found a file with an abnormal name: {Path.GetFileName(weirdFile)}");
                        if (File.Exists(pakPath)) File.Delete(pakPath);
                        File.Move(weirdFile, pakPath);
                        log($"-> Repaired to correct extension ({Path.GetFileName(pakPath)}).");
                    }
                }
                catch (Exception ex)
                {
                    log("An error occurred while repairing the filename: " + ex.Message);
                }

                // sym_update : Replaced the file copy and paste function with a file swap function.
                #region origin code

                // if (File.Exists(pakPath))
                // {
                //     File.Delete(pakPath);
                //     log("Bypass: Deleted existing file in paks folder.");
                // }
                //
                // if (File.Exists(bakFile))
                // {
                //     File.Copy(bakFile, pakPath, true);
                //     log("Bypass: Clean PAK copied to paks folder.");
                // }
                // else
                // {
                //     log("Critical Error: Backup original file not found in bak!");
                //     return;
                // }

                #endregion
                /* sym_review : why delete this logic?
                It was replaced with a vanilla file before exiting, or it is not needed because it is already a vanilla file due to the safety guard.*/
                

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

                
                // mod injection process
                // sym_update : Replaced the file copy and paste function with a file swap function.
                log($"Bypass: Launcher window detected! Waiting {(float)modApplyDelay/1000}s for mod injection...");
                await Task.Delay(modApplyDelay); // sym_TODO : Save the time variable desired by the user to appSettings. : Complete

                #region origin code
                // if (File.Exists(modBakFile))
                // {
                //     if (File.Exists(pakPath)) File.Delete(pakPath);
                //     File.Copy(modBakFile, pakPath, true);
                //     log("Bypass Complete: Mod applied successfully.");
                // }
                // else
                // {
                //     log("Warning: Mod backup not found in mods folder. Bypass aborted.");
                // }
                #endregion
                
                if (File.Exists(modBakFile))
                {
                    // pakPath(현재 순정)가 존재하는 정상적인 상황에서의 1:1 스왑
                    // 1:1 swap in a normal situation where pakPath(current vanilla file) exists
                    if (File.Exists(pakPath)) 
                    {
                        if (File.Exists(tempPak)) File.Delete(tempPak); // 혹시 모를 찌꺼기 제거
                        File.Move(pakPath, tempPak);       // 1. 순정 -> 임시 대기소
                        File.Move(modBakFile, pakPath);    // 2. 모드 -> 게임 폴더 (적용 완료!)
                        File.Move(tempPak, modBakFile);    // 3. 임시 대기소(순정) -> mods 폴더

                        log("Bypass Complete: Swap applied successfully.");
                        System.Windows.MessageBox.Show("Success Mod Apply\nYou can now run the game from the launcher.", "Success Mod Apply",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            MessageBoxResult.OK,
                            MessageBoxOptions.DefaultDesktopOnly);
                    }
                    else
                    {
                        // 만약 모종의 이유로 게임 폴더가 비어있다면 그냥 이동만 시킴
                        // If the game folder is empty for some reason, just move the files.
                        File.Move(modBakFile, pakPath);
                        System.Windows.MessageBox.Show(".pak is missing. you are maybe game repair", ".pak is missing.",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            MessageBoxResult.OK,
                            MessageBoxOptions.DefaultDesktopOnly);
                        log("Warning: originFile is missing. you are maybe game repair");
                    }
                }
                else
                {
                    log("Warning: Mod backup not found in mods folder. Bypass aborted.");
                }
                
            }
            catch (Exception ex) { log("Bypass Swap Error: " + ex.Message); }
        }

        // sym_update : save mod size when mod build.
        #region origin code
        // public async Task PackingProcess(string base64Key)
        // {
        //     if (string.IsNullOrEmpty(OutputDir)) return;
        //
        //     string cryptoPath = Path.Combine(Path.GetTempPath(), "nte_crypto.json");
        //     var crypto = new
        //     {
        //         EncryptionKey = new { Guid = "00000000000000000000000000000000", Name = "NTEKey", Key = base64Key },
        //         bDataCryptoRequired = false,
        //         bEnablePakIndexEncryption = true,
        //         bEnablePakEntryEncryption = false,
        //         bEnablePakSigning = false
        //     };
        //     File.WriteAllText(cryptoPath, JsonSerializer.Serialize(crypto));
        //
        //     string resPath = Path.Combine(Path.GetTempPath(), "pak_response.txt");
        //     using (var sw = new StreamWriter(resPath, false, new UTF8Encoding(false)))
        //     {
        //         foreach (string f in Directory.GetFiles(unpackDir, "*", SearchOption.AllDirectories))
        //         {
        //             string rel = f.Substring(unpackDir.Length).TrimStart('\\').Replace('\\', '/');
        //             sw.WriteLine($"\"{f}\" \"../../../HT/{rel}\"");
        //         }
        //     }
        //
        //     string pakPath = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
        //     string upakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
        //     await RunProcessAsync(upakExe, $"\"{pakPath}\" -create=\"{resPath}\" -cryptokeys=\"{cryptoPath}\"");
        //
        //     string modsDir = Path.Combine(OutputDir, "mods");
        //     string modBakFile = Path.Combine(modsDir, "pakchunk3-Windows.pak.bak");
        //
        //     if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);
        //
        //     if (File.Exists(pakPath))
        //     {
        //         File.Copy(pakPath, modBakFile, true);
        //         log("Build: Created Mod backup in mods folder.");
        //     }
        // }
        #endregion
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

            
            string modsDir = Path.Combine(OutputDir, "mods");
            string modBakFile = Path.Combine(modsDir, "pakchunk3-Windows.pak.bak");
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);
            
            // [핵심 수정] 언리얼팩 생성 경로를 게임 폴더가 아닌 mods 폴더로 다이렉트 지정
            // [Fix] Directly set the Unreal Pack creation path to the mods folder instead of the game folder
            string upakExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealPak", "UnrealPak.exe");
            await RunProcessAsync(upakExe, $"\"{modBakFile}\" -create=\"{resPath}\" -cryptokeys=\"{cryptoPath}\"");


            if (File.Exists(modBakFile))
            {
                long modSize = new FileInfo(modBakFile).Length;
                string pakPath = Path.Combine(OutputDir, "pakchunk3-Windows.pak");
                string tempPak = pakPath + ".tmp";
                if (File.Exists(tempPak)) File.Delete(tempPak);

                File.Move(pakPath, tempPak);       // 1. 원본 순정 파일 -> 임시 대기소로
                File.Move(modBakFile, pakPath);    // 2. 따끈따끈한 새 모드 파일 -> 원본 경로로 (적용 완료!)
                File.Move(tempPak, modBakFile);    // 3. 임시 대기소(순정) -> mods 폴더로 백업 보관
                log("Build: Created Mod backup in mods folder and Swap.");
                // sym_update : return mod size to AudioService for save mod size
                return modSize;
            }
            return 0; // return 0 when fail
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

        // Process management
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

        // Utilities
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
