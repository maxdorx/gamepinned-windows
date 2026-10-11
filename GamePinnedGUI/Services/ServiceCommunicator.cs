using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using GamePinned.Shared;
using GamePinned.Shared.Models;

namespace GamePinned.GUI.Services
{
    public class ServiceCommunicator
    {
        private const string SERVICE_NAME = "GamePinned";
        private const string CONFIG_PATH = @"C:\ProgramData\GamePinned\config.json";
        private const string LOG_PATH = @"C:\ProgramData\GamePinned\logs\";
        private static readonly HttpClient _httpClient = new HttpClient();

        public event Action<bool>? OnServiceStatusChanged;
        public event Action<string>? OnRestartLauncherRequested; // Path to launcher

        /// <summary>
        /// Fired when the service streams per-game lock/unlock progress.
        /// Argument: display text, e.g. "Locking: Counter-Strike (1/5)"
        /// </summary>
        public event Action<string>? ProtectionProgressChanged;

        /// <summary>
        /// Fired when the service blocks an uninstall attempt. Arguments:
        /// (platformId, gameName) — e.g. ("steam", "Counter-Strike"). Subscribers should
        /// surface a non-intrusive notification to explain why a launcher just appeared to
        /// crash (Steam) or showed a "file access error" (Epic/Riot).
        /// </summary>
        public event Action<string, string>? UninstallBlocked;

        public void StartNotificationListener()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var pipeServer = new NamedPipeServerStream("GamePinnedGUINotify", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipeServer.WaitForConnectionAsync();

                        using var reader = new StreamReader(pipeServer);
                        var msg = await reader.ReadToEndAsync();

                        if (!string.IsNullOrEmpty(msg))
                        {
                            var parts = msg.Split('|');
                            if (parts.Length >= 3 && parts[0] == "NTF")
                            {
                                if (parts[1] == "Info" && parts.Length >= 4)
                                {
                                    if (parts[2] == "RESTART_STEAM" || parts[2] == "RESTART_EPIC" || parts[2] == "RESTART_RIOT")
                                        OnRestartLauncherRequested?.Invoke(parts[3]);
                                }
                                else if (parts[1] == "ProtectionProgress" && parts.Length >= 3)
                                {
                                    // parts[2] = "Locking: Counter-Strike (1/5)"
                                    ProtectionProgressChanged?.Invoke(parts[2]);
                                }
                                else if (parts[1] == "UninstallBlocked" && parts.Length >= 4)
                                {
                                    // parts[2] = platformId (e.g. "steam"), parts[3] = gameName
                                    UninstallBlocked?.Invoke(parts[2], parts[3]);
                                }
                            }
                        }
                    }
                    catch
                    {
                        await Task.Delay(1000); // Retry on error
                    }
                }
            });
        }

        #region Service Control

        public async Task<bool> GetServiceStatusAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(SERVICE_NAME);
                    return sc.Status == ServiceControllerStatus.Running;
                }
                catch { return false; }
            });
        }

        /// <summary>
        /// Whether the "GamePinned" Windows Service is registered at all, regardless of whether
        /// it's currently running. Distinct from GetServiceStatusAsync, which collapses "not
        /// installed" and "installed but stopped" into the same false. Used to give the user a
        /// specific reason (not installed vs. just not running) instead of a generic failure.
        /// ServiceController.GetServices() never throws for a name that simply isn't present, it
        /// just won't be in the list, so this never hits the same Win32Exception that querying
        /// .Status on a non-existent service does.
        /// </summary>
        public bool IsServiceInstalled()
        {
            try
            {
                return ServiceController.GetServices()
                    .Any(s => s.ServiceName.Equals(SERVICE_NAME, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public async Task<bool> StartServiceAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(SERVICE_NAME);
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                        OnServiceStatusChanged?.Invoke(true);
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        public async Task<bool> StopServiceAsync()
        {
            // Try pipe-based stop first — works without elevation.
            // The service validates the admin hash before stopping, so this is as secure
            // as any other admin action in the GUI.
            bool pipeSentOk = false;
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.RestartService));
                pipeSentOk = response != null && response.Contains("|OK|");
            }
            catch { /* pipe unavailable — fall through to ServiceController */ }

            if (pipeSentOk)
            {
                // Poll ServiceController.Status — AU users can query status (SERVICE_QUERY_STATUS)
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    try
                    {
                        using var sc = new ServiceController(SERVICE_NAME);
                        sc.Refresh();
                        if (sc.Status == ServiceControllerStatus.Stopped)
                        {
                            OnServiceStatusChanged?.Invoke(false);
                            return true;
                        }
                    }
                    catch { }
                }
                throw new Exception("Service did not stop within 20 seconds.");
            }

            // Fallback: direct ServiceController (requires elevation — only works when GUI runs as admin)
            return await Task.Run(() =>
            {
                using var sc = new ServiceController(SERVICE_NAME);
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    OnServiceStatusChanged?.Invoke(false);
                    return true;
                }
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        OnServiceStatusChanged?.Invoke(false);
                        return true;
                    }
                    throw new Exception("Service did not stop within 15 seconds.");
                }
                throw new Exception("Service is busy. Try again in a moment.");
            });
        }

        #endregion

        #region Configuration & Data Access

        public async Task<List<GameInfo>> GetProtectedGamesAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    var games = new List<GameInfo>();

                    foreach (var platform in config.Platforms)
                    {
                        if (!platform.Enabled) continue;

                        // STEAM: Directory scanning works (games are folders in steamapps/common)
                        if (platform.PlatformId == "steam")
                        {
                            foreach (var libraryPath in platform.LibraryPaths)
                            {
                                var commonPath = System.IO.Path.Combine(libraryPath, "common");
                                if (!System.IO.Directory.Exists(commonPath)) continue;

                                try
                                {
                                    foreach (var gameFolder in System.IO.Directory.GetDirectories(commonPath))
                                    {
                                        var gameName = System.IO.Path.GetFileName(gameFolder);
                                        if (string.IsNullOrEmpty(gameName)) continue;

                                        // Skip excluded games
                                        if (platform.ExcludedGames.Contains(gameName)) continue;

                                        games.Add(new GameInfo
                                        {
                                            PlatformId = "steam",
                                            GameName = gameName,
                                            InstallPath = gameFolder,
                                            ManifestPath = "" // Not needed for display
                                        });
                                    }
                                }
                                catch { }
                            }
                        }
                        // EPIC/RIOT: Use service's platform detection via Named Pipe
                        // Fallback mode (GetProtectedGamesAsync) won't show Epic/Riot games
                        // This is intentional - proper detection requires manifest parsing in service
                    }

                    return games;
                }
                catch { return new List<GameInfo>(); }
            });
        }

        public async Task<List<string>> GetBlockedUsersAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    return config.BlockedUsers.ToList();
                }
                catch { return new List<string>(); }
            });
        }

        public async Task<bool> AddBlockedUserAsync(string username)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    if (config.BlockedUsers.Contains(username)) return false;

                    config.BlockedUsers.Add(username);
                    return SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                }
                catch { return false; }
            });
        }

        public async Task<bool> RemoveBlockedUserAsync(string username)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    if (!config.BlockedUsers.Contains(username)) return false;

                    config.BlockedUsers.Remove(username);
                    return SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                }
                catch { return false; }
            });
        }

        #endregion

        #region Game Management

        /// <summary>
        /// Bulk protect all games on a platform (single config save)
        /// </summary>
        public async Task<int> ProtectAllGamesAsync(string platformId, List<string> gameNames)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    var platform = config.Platforms.FirstOrDefault(p => p.PlatformId == platformId);
                    if (platform == null) return 0;

                    int addedCount = 0;
                    bool anyExcludedRemoved = false;
                    foreach (var gameName in gameNames)
                    {
                        // Add to protected list if not already there
                        if (!platform.ProtectedGames.Contains(gameName))
                        {
                            platform.ProtectedGames.Add(gameName);
                            addedCount++;
                        }

                        // Remove from excluded list if present
                        if (platform.ExcludedGames.Contains(gameName))
                        {
                            platform.ExcludedGames.Remove(gameName);
                            anyExcludedRemoved = true;
                        }
                    }

                    // Always push config — even if already in ProtectedGames, the push triggers
                    // ApplyProtection() in the service which re-applies any missing NTFS rules
                    // (e.g. after health-check temporarily removed them during an update).
                    if (addedCount == 0 && !anyExcludedRemoved)
                    {
                        bool success2 = SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                        // Return 1 so caller shows "Done" instead of "No Changes" — NTFS rules were re-applied
                        // Return -1 so caller can distinguish "service unavailable" from "no changes"
                        return success2 ? 1 : -1;
                    }

                    // Save config ONCE (not per-game)
                    bool success = SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                    // Return -1 so caller can distinguish "service unavailable" from "no changes"
                    return success ? addedCount : -1;
                }
                catch { return -1; }
            });
        }

        /// <summary>
        /// Bulk exempt all games on a platform (single config save)
        /// </summary>
        public async Task<int> ExemptAllGamesAsync(string platformId, List<string> gameNames)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    var platform = config.Platforms.FirstOrDefault(p => p.PlatformId == platformId);
                    if (platform == null) return 0;

                    int addedCount = 0;
                    foreach (var gameName in gameNames)
                    {
                        // Add to excluded list if not already there
                        if (!platform.ExcludedGames.Contains(gameName))
                        {
                            platform.ExcludedGames.Add(gameName);
                            addedCount++;
                        }

                        // Remove from protected list if present
                        if (platform.ProtectedGames.Contains(gameName))
                            platform.ProtectedGames.Remove(gameName);
                    }

                    // Save config ONCE (not per-game)
                    bool success = SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                    // Return -1 so caller can distinguish "service unavailable" from "no changes"
                    return success ? addedCount : -1;
                }
                catch { return -1; }
            });
        }

        #endregion

        #region Admin Password

        public async Task<string> GetAdminPasswordHashAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    return config.AdminPasswordHash;
                }
                catch { return ""; }
            });
        }

        public async Task<bool> SetAdminPasswordAsync(string passwordHash)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    config.AdminPasswordHash = passwordHash;
                    return SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                }
                catch { return false; }
            });
        }

        #endregion

        #region License Management

        public async Task<(string hardwareId, string status, string type, DateTime? expiry)> GetLicenseInfoAsync()
        {
            return await Task.Run(async () =>
            {
                try
                {
                    var hwId = CacheManager.GetHardwareId() ?? "ERROR";
                    var config = Config.Load(CONFIG_PATH);

                    if (string.IsNullOrWhiteSpace(config.LicenseKey))
                        return (hwId, "Free", "1 game limit", (DateTime?)null);

                    var licenseInfo = LicenseManager.GetLicenseInfo(config.LicenseKey);
                    var expiry = LicenseManager.GetExpiryDate(config.LicenseKey);
                    
                    // Ask the service for the REAL status (Active/Expired/Free)
                    var statusResponse = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.GetStatus));
                    var statusParsed = PipeMessage.ParseResponse(statusResponse ?? "");
                    var status = statusParsed.status == "OK" ? statusParsed.data : "Unknown";

                    return (hwId, status, licenseInfo, expiry);
                }
                catch
                {
                    return (CacheManager.GetHardwareId() ?? "ERROR", "Error", "Unknown", (DateTime?)null);
                }
            });
        }

        public async Task<bool> ActivateLicenseAsync(string licenseKey, string? rawKey = null)
        {
            if (string.IsNullOrWhiteSpace(licenseKey)) return false;

            try
            {
                // Delegate validation to the SECURE Native AOT Service
                var command = PipeMessage.CreateCommand(PipeCommand.ActivateLicense, licenseKey);
                var responseRaw = await SendCommandAsync(command);
                var response = PipeMessage.ParseResponse(responseRaw ?? "");

                if (response.status == "OK" && response.data == "LICENSE_VALID")
                {
                    var config = Config.Load(CONFIG_PATH);
                    config.LicenseKey = licenseKey;
                    config.HardwareID = CacheManager.GetHardwareId() ?? "";
                    // Set RawLicenseKey here so only ONE SaveConfigViaIpcAsync is needed —
                    // avoids a second PushConfig round-trip from ActivateOnlineAsync
                    if (!string.IsNullOrWhiteSpace(rawKey))
                        config.RawLicenseKey = rawKey.Trim();

                    return await SaveConfigViaIpcAsync(config);
                }
                return false;
            }
            catch { return false; }
        }

        #endregion

        #region Auto Update

        public async Task<bool> SetAutoUpdateEnabledAsync(bool enabled)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    config.AutoUpdateEnabled = enabled;
                    return SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                }
                catch { return false; }
            });
        }

        #endregion

        #region Maintenance Mode

        public async Task<bool> ToggleMaintenanceModeAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = Config.Load(CONFIG_PATH);
                    config.MaintenanceMode = !config.MaintenanceMode;
                    return SaveConfigViaIpcAsync(config).GetAwaiter().GetResult();
                }
                catch { return false; }
            });
        }

        #endregion

        #region Logs

        public async Task<List<string>> GetLogsAsync(int lastN = 100)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(LOG_PATH))
                        return new List<string>();

                    var logFiles = Directory.GetFiles(LOG_PATH, "gamepinned_*.log")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Take(3)
                        .ToList();

                    var allLines = new List<string>();
                    foreach (var logFile in logFiles)
                    {
                        try 
                        { 
                            using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var sr = new StreamReader(fs))
                            {
                                while (!sr.EndOfStream)
                                {
                                    var line = sr.ReadLine();
                                    if (line != null) // Removed aggressive filtering to show all logs
                                        allLines.Add(line);
                                }
                            }
                        }
                        catch { }
                    }

                    return allLines.TakeLast(lastN).ToList();
                }
                catch { return new List<string> { "Error reading logs." }; }
            });
        }

        public async Task<bool> ClearLogsAsync()
        {
            var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.ClearLogs));
            return response != null && response.StartsWith("RSP|OK");
        }

        /// <summary>
        /// Clear login/session data for the given platform so next launch shows a fresh login screen.
        /// </summary>
        /// <param name="platformId">"steam", "epic", or "riot"</param>
        public async Task<bool> ClearPlatformSessionAsync(string platformId)
        {
            var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.ClearPlatformSession, platformId));
            return response != null && response.StartsWith("RSP|OK");
        }

        #endregion

        #region Named Pipe Communication

        private async Task<bool> SaveConfigViaIpcAsync(Config config)
        {
            try
            {
                // To authenticate the update to the Service, read the original Hash from disk
                // If this is the FIRST setup (no hash yet), we use SETUP_OVERRIDE
                var authHash = "SETUP_OVERRIDE";
                try
                {
                    var currentDiskConfig = Config.Load(CONFIG_PATH);
                    if (!string.IsNullOrEmpty(currentDiskConfig.AdminPasswordHash))
                        authHash = currentDiskConfig.AdminPasswordHash;
                }
                catch { }

                // CRITICAL: Must use UnsafeRelaxedJsonEscaping so that base64 characters
                // like + and / are NOT escaped to \u002B / \u002F.
                // The default JavaScriptEncoder.Default escapes them for HTML safety, but our
                // hand-rolled ParseString does NOT unescape \uXXXX sequences — so each
                // Config.Load() → Config.Save() cycle (e.g. DetectPlatforms on service restart)
                // would read \u002B literally, then EscapeJson doubles the backslash, causing
                // exponential growth and ultimately corrupting the AdminPasswordHash field.
                var json = System.Text.Json.JsonSerializer.Serialize(config,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                var jsonBase64 = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
                
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.PushConfig, $"{authHash}|{jsonBase64}"));
                if (response != null && response.StartsWith("RSP|OK"))
                {
                    // PushConfig already triggers protection via _onConfigChanged callback
                    // No need to call RefreshProtection - that would apply protection twice
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        public async Task<bool> ToggleStartupAsync(bool enable, string exePath)
        {
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.ToggleStartup, $"{enable}|{exePath}"));
                return response != null && response.StartsWith("RSP|OK");
            }
            catch { return false; }
        }

        public async Task<string?> SendCommandAsync(string command)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", PipeConstants.PIPE_NAME, PipeDirection.InOut);
                await pipeClient.ConnectAsync(5000);

                var commandBytes = Encoding.UTF8.GetBytes(command);
                await pipeClient.WriteAsync(commandBytes, 0, commandBytes.Length);

                // Read in a loop until the server closes the connection (returns 0 bytes).
                // A single ReadAsync is NOT safe — named pipes can deliver data in multiple
                // chunks, and large GetAllData responses (200+ games) exceed 64KB when
                // base64-encoded, causing silent truncation with a fixed-size buffer.
                using var ms = new System.IO.MemoryStream();
                var buffer = new byte[65536];
                int bytesRead;
                while ((bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, bytesRead);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                // Never show this raw exception to the user, even in Debug builds: this fires
                // on every failed pipe call (service not installed, not running, busy, or a
                // transient hiccup), and popping a dialog for each one is both alarming and, for
                // background/periodic calls, spammy. Callers already treat a null return as "the
                // service wasn't reachable" and handle it their own way (see IsServiceInstalled
                // and the persistent ServiceStatusText indicator for the ambient, always-visible
                // signal). Debug.WriteLine only, visible in a debugger's Output window, never on
                // screen.
                System.Diagnostics.Debug.WriteLine($"[ServiceCommunicator] SendCommandAsync failed: {ex.Message}");
                return null;
            }
        }

        #endregion
        public async Task<(bool success, string message)> SendDiagnosticReportAsync()
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // Service reads debug_*.log as SYSTEM (logs directory is Admins+SYSTEM only)
                    var pipeResponse = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.GetDiagnosticLogs));
                    var (diagStatus, content) = PipeMessage.ParseResponse(pipeResponse ?? "");

                    if (diagStatus != "OK" || string.IsNullOrWhiteSpace(content))
                        return (false, "No logs found. The service will generate logs once it starts.");

                    // Cap at 500KB (send the most recent portion)
                    const int maxBytes = 512 * 1024;
                    if (Encoding.UTF8.GetByteCount(content) > maxBytes)
                        content = "[...truncated, showing tail...]\n" + content.Substring(Math.Max(0, content.Length - maxBytes / 2));

                    // Get hardware ID and raw license key
                    var hwId = CacheManager.GetHardwareId() ?? "";
                    var rawLicenseKey = "";
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var infoVerAttr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
                    var appVersion = infoVerAttr?.InformationalVersion?.Split('+')[0]
                                     ?? asm.GetName().Version?.ToString()
                                     ?? "unknown";
                    try
                    {
                        var cfg = Config.Load(CONFIG_PATH);
                        rawLicenseKey = cfg.RawLicenseKey ?? "";
                    }
                    catch { }

                    var payload = new
                    {
                        hardware_id = hwId,
                        raw_license_key = rawLicenseKey,
                        log_content = content,
                        app_version = appVersion
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync("https://api.example.com/api/diagnostic", httpContent);
                    if (response.IsSuccessStatusCode)
                        return (true, "Report sent successfully. Our team will review it shortly.");

                    var body = await response.Content.ReadAsStringAsync();
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                        return (false, msg ?? "Server returned an error. Please try again later.");
                    }
                    catch
                    {
                        return (false, "Server returned an error. Please try again later.");
                    }
                }
                catch (Exception ex)
                {
                    return (false, "Connection error: " + ex.Message);
                }
            });
        }

        public async Task<(bool success, string message)> ActivateOnlineAsync(string licenseKey)
        {
            try
            {
                var hwId = CacheManager.GetHardwareId();
                if (string.IsNullOrEmpty(hwId)) return (false, "Could not generate Hardware ID");

                var payload = new { key = licenseKey, hwid = hwId };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://api.example.com/activate", content);
                var responseJson = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode)
                {
                    var blob = root.GetProperty("license_blob").GetString() ?? "";

                    // Pass the raw key so ActivateLicenseAsync can set both LicenseKey and
                    // RawLicenseKey in one SaveConfigViaIpcAsync call instead of two.
                    var ipcSuccess = await ActivateLicenseAsync(blob, licenseKey);

                    return (ipcSuccess, ipcSuccess ? "Activated successfully!" : "Service failed to store license.");
                }
                else
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "Activation failed";
                    return (false, msg ?? "Unknown error");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Connection error: {ex.Message}");
            }
        }

        #region NVIDIA Profile

        public async Task<NvidiaConfigInfo?> GetNvidiaStatusAsync()
        {
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.GetNvidiaStatus));
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                if (status != "OK" || string.IsNullOrEmpty(data)) return null;
                return JsonSerializer.Deserialize<NvidiaConfigInfo>(data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }

        /// <summary>Save current NVIDIA settings as a new profile. profileName is optional (auto-named if empty).</summary>
        public async Task<(bool success, string message)> SaveNvidiaConfigAsync(string? profileName = null)
        {
            try
            {
                var cmd = PipeMessage.CreateCommand(PipeCommand.SaveNvidiaConfig, profileName ?? "");
                var response = await SendCommandAsync(cmd);
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                return (status == "OK", string.IsNullOrEmpty(data) ? "Unknown error." : data);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Restore a specific profile by ID.</summary>
        public async Task<(bool success, string message)> RestoreNvidiaProfileAsync(string profileId)
        {
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.RestoreNvidiaProfile, profileId));
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                return (status == "OK", string.IsNullOrEmpty(data) ? "Unknown error." : data);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Rename a profile.</summary>
        public async Task<(bool success, string message)> RenameNvidiaProfileAsync(string profileId, string newName)
        {
            try
            {
                var response = await SendCommandAsync(
                    PipeMessage.CreateCommand(PipeCommand.RenameNvidiaProfile, $"{profileId}|{newName}"));
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                return (status == "OK", string.IsNullOrEmpty(data) ? "Unknown error." : data);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Delete a single profile by ID.</summary>
        public async Task<(bool success, string message)> DeleteNvidiaProfileAsync(string profileId)
        {
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.DeleteNvidiaProfile, profileId));
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                return (status == "OK", string.IsNullOrEmpty(data) ? "Unknown error." : data);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Set which profile is used for auto-restore on startup.</summary>
        public async Task<(bool success, string message)> SetActiveNvidiaProfileAsync(string profileId)
        {
            try
            {
                var response = await SendCommandAsync(PipeMessage.CreateCommand(PipeCommand.SetActiveNvidiaProfile, profileId));
                var (status, data) = PipeMessage.ParseResponse(response ?? "");
                return (status == "OK", string.IsNullOrEmpty(data) ? "Unknown error." : data);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Enable or disable auto-restore on startup. Routed through the service to avoid a direct config.json write race.</summary>
        public async Task<bool> SetNvidiaAutoRestoreAsync(bool enabled)
        {
            try
            {
                var response = await SendCommandAsync(
                    PipeMessage.CreateCommand(PipeCommand.SetNvidiaAutoRestore, enabled ? "true" : "false"));
                return response != null && response.StartsWith("RSP|OK");
            }
            catch { return false; }
        }

        #endregion
    }
}
