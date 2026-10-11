using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace GamePinned.GUI.Services
{
    /// <summary>
    /// Checks the GamePinned API for updates and installs them.
    /// Background auto-update is handled by the service (SYSTEM).
    /// This class handles: startup flag consumption, manual check, and manual install.
    /// </summary>
    public class UpdateChecker
    {
        private const string UPDATE_API_URL = "https://api.example.com/version";
        private const string UPDATE_FLAG_PATH = @"C:\ProgramData\GamePinned\autoupdated.flag";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public UpdateChecker() { }

        /// <summary>
        /// Check if the service performed a silent auto-update since last launch.
        /// Returns the updated version string if so, null otherwise.
        /// </summary>
        public string? ConsumeUpdateFlag()
        {
            try
            {
                if (!File.Exists(UPDATE_FLAG_PATH)) return null;
                var version = File.ReadAllText(UPDATE_FLAG_PATH).Trim();
                File.Delete(UPDATE_FLAG_PATH);
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get current version from assembly
        /// </summary>
        private Version GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();

            var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
            if (!string.IsNullOrWhiteSpace(productVersion) && Version.TryParse(productVersion, out var parsedProductVersion))
            {
                return parsedProductVersion;
            }

            var version = assembly.GetName().Version;
            return version ?? new Version(1, 0, 0, 0);
        }

        /// <summary>
        /// Check for updates via the GamePinned API (used by manual "Check Now" button)
        /// </summary>
        public async Task<(bool updateAvailable, string version, string downloadUrl, long fileSize, string releaseNotes, string sha256)> CheckForUpdatesAsync(string? overrideUrl = null)
        {
            try
            {
                var response = await _http.GetStringAsync(overrideUrl ?? UPDATE_API_URL);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                string latestVersionStr = root.GetProperty("version").GetString() ?? "1.0.0";
                string downloadUrl = root.GetProperty("downloadUrl").GetString() ?? "";
                long fileSize = root.TryGetProperty("fileSize", out var sizeProp) ? sizeProp.GetInt64() : 0;
                string releaseNotes = root.TryGetProperty("releaseNotes", out var notesProp) ? notesProp.GetString() ?? "" : "";
                string sha256 = root.TryGetProperty("sha256Hash", out var sha256Prop) ? sha256Prop.GetString() ?? "" : "";

                if (Version.TryParse(latestVersionStr, out var latestVersion))
                {
                    var currentVersion = GetCurrentVersion();
                    if (latestVersion > currentVersion)
                    {
                        return (true, latestVersion.ToString(), downloadUrl, fileSize, releaseNotes, sha256);
                    }
                }

                return (false, GetCurrentVersion().ToString(), "", 0, "", "");
            }
            catch
            {
                return (false, GetCurrentVersion().ToString(), "", 0, "", "");
            }
        }

        /// <summary>
        /// Download and install update with progress reporting (manual install from GUI)
        /// </summary>
        public async Task DownloadAndInstallAsync(string downloadUrl, IProgress<int> progress = null, string expectedSha256 = "")
        {
            try
            {
                var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
                if (string.IsNullOrEmpty(fileName)) fileName = "GamePinned-Setup.exe";

                var tempPath = Path.Combine(Path.GetTempPath(), fileName);

                // Per-chunk stall detection instead of HttpClient.Timeout:
                // HttpClient.Timeout applies to the entire request including body read, so a short
                // total timeout breaks large downloads on slow networks. Infinite timeout hangs
                // forever on dead connections. Correct pattern: cancel if no bytes arrive for 60s
                // (timer resets on every successful chunk).
                const int StallTimeoutSeconds = 60;
                using (var client = new HttpClient())
                {
                    using var headersCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(StallTimeoutSeconds));
                    var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    var canReportProgress = totalBytes > 0 && progress != null;

                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while (true)
                    {
                        using var readCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(StallTimeoutSeconds));
                        try { bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token); }
                        catch (OperationCanceledException) { throw new Exception("Download stalled — no data received for " + StallTimeoutSeconds + " seconds. Check your connection and try again."); }
                        if (bytesRead == 0) break;
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        totalRead += bytesRead;

                        if (canReportProgress)
                        {
                            var percentComplete = (int)((totalRead * 100) / totalBytes);
                            progress.Report(percentComplete);
                        }
                    }

                    await fileStream.FlushAsync();
                }

                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                    throw new Exception("Downloaded file is empty or missing.");

                // Verify SHA256 if the server provided one
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    using var fileForHash = File.OpenRead(tempPath);
                    var actualBytes = await SHA256.HashDataAsync(fileForHash);
                    var actualHex = BitConverter.ToString(actualBytes).Replace("-", "").ToLower();
                    if (!string.Equals(actualHex, expectedSha256.ToLower(), StringComparison.Ordinal))
                    {
                        File.Delete(tempPath);
                        throw new Exception("Update file integrity check failed. Download may be corrupted.");
                    }
                }

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                if (proc == null)
                    throw new Exception("Failed to start the installer process.");

                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // Clean up partial download on failure
                try
                {
                    var cleanupPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(downloadUrl).LocalPath) ?? "GamePinned-Setup.exe");
                    if (File.Exists(cleanupPath)) File.Delete(cleanupPath);
                }
                catch { }
                throw new Exception($"Failed to download/install update: {ex.Message}");
            }
        }
    }
}
