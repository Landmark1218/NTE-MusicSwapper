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
        public async Task BuildModAsync(List<WemItemBase> targets)
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
                    return;
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
                await gameService.PackingProcess(base64Key);

                log("Build complete!");
                System.Windows.MessageBox.Show("Pak build complete!\nIf Auto-Save is ON, it will be applied automatically next time.");
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
        }
    }
}
