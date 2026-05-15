using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace NtePakTool
{
    public class VerifierProgressArgs
    {
        public string Message { get; init; } = "";
        public double Percent { get; init; } 
    }

    file sealed class AesConfig
    {
        public string aes_key { get; set; } = "";
        public string mapping { get; set; } = "";
    }
    public sealed class AssetVerifier
    {
        private const string AesJsonUrl =
            "https://github.com/Landmark1218/Trash/raw/refs/heads/main/AES.json"; //i love this free strage♡

        private readonly string _unpackHtDir;   // Unpak/HT
        private readonly Action<VerifierProgressArgs> _progress;

        public AssetVerifier(string unpackHtDir, Action<VerifierProgressArgs> progress)
        {
            _unpackHtDir = unpackHtDir;
            _progress = progress;
        }

        // ─────────────────────────────────────────────
        // Public entry point
        // ─────────────────────────────────────────────
        public async Task RunAsync(CancellationToken ct = default)
        {
            Report("Loading settings...", 0.00);

            // 1. Fetch AES.json
            var (aesKey, usmapUrl) = await FetchAesConfigAsync(ct);

            // 2. Initialize Oodle
            Report("Initializing Oodle...", 0.03);
            await OodleHelper.InitializeAsync(cancellationToken: ct);

            // 3. Download usmap (temporary file)
            string usmapPath = await DownloadUsmapAsync(usmapUrl, ct);

            // 4. Search for game PAK files
            Report("Searching for PAK files...", 0.08);
            string pakPath = FindPakPath();
            if (string.IsNullOrEmpty(pakPath))
            {
                Report("PAK files not found. Skipping...", 1.0);
                return;
            }

            // 5. Move PAK to temporary folder and build provider
            Report("Loading PAK...", 0.10);
            string tempDir = Path.Combine(Path.GetTempPath(), "NteMusicSwapper_tmp");
            string tempPak = Path.Combine(tempDir, Path.GetFileName(pakPath));
            Directory.CreateDirectory(tempDir);
            File.Move(pakPath, tempPak, overwrite: true);

            try
            {
                var provider = BuildProvider(tempDir, usmapPath, aesKey);

                // 6. Get virtual path list
                Report("Retrieving asset list...", 0.15);
                var allFiles = provider.Files
                    .Where(kv => !kv.Value.IsUePackagePayload)
                    .ToList();

                // 7. Check differences with Unpak folder
                Report("Checking for missing assets...", 0.20);
                var missing = GetMissingFiles(allFiles);

                if (missing.Count == 0)
                {
                    Report("All assets are present.", 1.0);
                    return;
                }

                Report($"Extracting {missing.Count:N0} missing assets...", 0.22);
                // 8. Extract only missing files in parallel
                int done = 0;
                int total = missing.Count;

                await Task.Run(() =>
                {
                    Parallel.ForEach(
                        missing,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Environment.ProcessorCount,
                            CancellationToken = ct
                        },
                        kv =>
                        {
                            var gameFile = kv.Value;
                            try
                            {
                                if (gameFile.IsUePackage)
                                {
                                    var package = provider.SavePackage(gameFile);
                                    foreach (var (vPath, data) in package)
                                        WriteFile(vPath, data);
                                }
                                else
                                {
                                    var data = provider.SaveAsset(gameFile);
                                    WriteFile(gameFile.Path, data);
                                }
                            }
                            catch { /* Ignore individual failures and continue */ }

                            int current = Interlocked.Increment(ref done);
                            double pct = 0.22 + (double)current / total * 0.78;
                            Report($"Currently running... {current:N0} / {total:N0}", pct);
                        });
                }, ct);

                Report("Asset extraction completed.", 1.0);
            }
            finally
            {
                // Move PAK back to original location
                if (File.Exists(tempPak))
                    File.Move(tempPak, pakPath, overwrite: true);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);

                // Delete usmap temporary file
                if (File.Exists(usmapPath))
                    File.Delete(usmapPath);
            }
        }

        // ─────────────────────────────────────────────
        // Internal helpers
        // ─────────────────────────────────────────────

        private async Task<(string aesKey, string usmapUrl)> FetchAesConfigAsync(CancellationToken ct)
        {
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var json = await hc.GetStringAsync(AesJsonUrl, ct);
                var cfg = JsonSerializer.Deserialize<AesConfig>(json)
                          ?? throw new InvalidDataException("Failed to parse AES.json.");
                return (cfg.aes_key, cfg.mapping);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to fetch AES.json: " + ex.Message, ex);
            }
        }

        private static async Task<string> DownloadUsmapAsync(string url, CancellationToken ct)
        {
            string path = Path.Combine(Path.GetTempPath(), "NteMusicSwapper_mappings.usmap");
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var bytes = await hc.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(path, bytes, ct);
            }
            catch { /* Continue even without usmap */ }
            return path;
        }

        private static string FindPakPath()
        {
            // Search for pakchunk3 from the game's standard installation directory
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName);

            foreach (var drive in drives)
            {
                // Candidate patterns
                var candidates = new[]
                {
                    Path.Combine(drive, "Neverness To Everness", "Client",
                        "WindowsNoEditor", "HT", "Content", "Paks", "pakchunk3-Windows.pak"),
                };
                foreach (var c in candidates)
                    if (File.Exists(c)) return c;

                // If not found, search via BFS
                var skipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "Windows", "ProgramData", "$RECYCLE.BIN", "AppData", "Recovery" };

                var queue = new Queue<string>();
                queue.Enqueue(drive);
                while (queue.Count > 0)
                {
                    string cur = queue.Dequeue();
                    try
                    {
                        string candidate = Path.Combine(cur, "pakchunk3-Windows.pak");
                        if (File.Exists(candidate) && cur.Contains("Paks"))
                            return candidate;

                        foreach (var sub in Directory.GetDirectories(cur))
                        {
                            string name = Path.GetFileName(sub);
                            if (!skipFolders.Contains(name))
                                queue.Enqueue(sub);
                        }
                    }
                    catch { }
                }
            }
            return string.Empty;
        }

        private static DefaultFileProvider BuildProvider(string dir, string usmapPath, string aesKey)
        {
            var versions = new VersionContainer(EGame.GAME_NevernessToEverness);
            var provider = new DefaultFileProvider(
                directory: dir,
                searchOption: SearchOption.TopDirectoryOnly,
                versions: versions,
                pathComparer: StringComparer.OrdinalIgnoreCase);

            if (File.Exists(usmapPath))
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);

            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(aesKey));
            return provider;
        }
        
        /// Compares the virtual path list from the PAK against the Unpak folder and returns any missing entries
        private List<KeyValuePair<string, CUE4Parse.FileProvider.Objects.GameFile>> GetMissingFiles(
            IEnumerable<KeyValuePair<string, CUE4Parse.FileProvider.Objects.GameFile>> allFiles)
        {
            // Collect existing files under Unpak/HT in lowercase
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_unpackHtDir))
            {
                foreach (var f in Directory.GetFiles(_unpackHtDir, "*", SearchOption.AllDirectories))
                {
                    // Convert to virtual path format "HT/Content/..."
                    string rel = f.Substring(_unpackHtDir.Length - "HT".Length)
                                  .Replace(Path.DirectorySeparatorChar, '/');
                    existing.Add(rel.TrimStart('/'));
                }
            }

            return allFiles
                .Where(kv =>
                {
                    string vp = kv.Key; // e.g. "HT/Content/WwiseAudio/Media/123.wem"
                    // For uasset files, uexp is written out at the same time, so only check by uasset key
                    return !existing.Contains(vp);
                })
                .ToList();
        }

        private void WriteFile(string virtualPath, byte[] data)
        {
            // virtualPath: "HT/Content/..." → Unpak/HT/Content/...
            //   _unpackHtDir = "...Unpak/HT", so combine after stripping the HT portion
            string rel = virtualPath.Replace('/', Path.DirectorySeparatorChar);

            // Virtual path starts with "HT\Content\...", so strip the leading "HT\" before combining
            if (rel.StartsWith("HT" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(3);

            string fullPath = Path.Combine(_unpackHtDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, data);
        }

        private void Report(string message, double percent) =>
            _progress(new VerifierProgressArgs { Message = message, Percent = percent });
    }
}
