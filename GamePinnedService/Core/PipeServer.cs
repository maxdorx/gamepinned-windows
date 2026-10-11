using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using GamePinned.Shared;
using GamePinned.Shared.Models;
using System.Reflection;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// Named Pipe server for sending notifications to the GUI
    /// Runs in the service process and accepts connections from GamePinnedGUI
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class PipeServer : IDisposable
    {
        private const int LogRetentionDays = 7;

        private CancellationTokenSource? _cts;
        private readonly string _configPath;
        private readonly Action<string> _logger;
        private readonly Action<string> _debugLogger;
        private readonly Func<PipeCommand, string[], Task<string?>>? _commandHandler;
        private readonly Action? _onConfigChanged;
        private readonly Func<List<GameInfo>>? _getAllGames;
        private readonly NvidiaConfigManager _nvidiaManager;
        private readonly Action? _stopAction;

        public PipeServer(string configPath, Action<string> logger, Func<PipeCommand, string[], Task<string?>>? commandHandler = null, Action? onConfigChanged = null, Func<List<GameInfo>>? getAllGames = null, Action<string>? debugLogger = null, Action? stopAction = null)
        {
            _configPath = configPath;
            _logger = logger;
            _debugLogger = debugLogger ?? (_ => { });
            _commandHandler = commandHandler;
            _onConfigChanged = onConfigChanged;
            _getAllGames = getAllGames;
            _stopAction = stopAction;
            _nvidiaManager = new NvidiaConfigManager(
                Path.Combine(Path.GetDirectoryName(configPath) ?? "", "nvidia-config"),
                logger, _debugLogger);
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunServer(_cts.Token));
            // Silent — internal IPC
        }

        public void Stop()
        {
            _cts?.Cancel();
            // Silent — internal IPC
        }

        private async Task RunServer(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var pipeSecurity = new PipeSecurity();
                    
                    // Allow Administrators and SYSTEM Full Control (required for creation and management)
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                        
                    // Allow any standard user to interact with the service pipe
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));

#pragma warning disable CA1416 // Validate platform compatibility
                    var pipeServer = NamedPipeServerStreamAcl.Create(
                        PipeConstants.PIPE_NAME,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, // Support multiple concurrent connections properly
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        65536, // inBufferSize: 64KB for receiving commands
                        65536, // outBufferSize: 64KB for sending large GetAllData responses
                        pipeSecurity);
#pragma warning restore CA1416

                    await pipeServer.WaitForConnectionAsync(ct);

                    // Fire and forget handling of this specific connection
                    _ = HandleClientAsync(pipeServer, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger($"Pipe server error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken ct)
        {
            using (pipeServer)
            {
                try
                {
                    // Read command from GUI
                    var buffer = new byte[65536];
                    var bytesRead = await pipeServer.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) return;

                    var command = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    _debugLogger($"[Pipe] Received: {command.Substring(0, Math.Min(command.Length, 120))}");

                    // Process command and send response
                    var response = await ProcessCommandAsync(command);
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await pipeServer.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
                    await pipeServer.FlushAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger($"Error handling client: {ex.Message}");
                    _debugLogger($"[Pipe] HandleClientAsync exception: {ex}");
                }
            }
        }

        private async Task<string> ProcessCommandAsync(string raw)
        {
            var (type, parts) = PipeMessage.Parse(raw);

            if (type != "CMD" || parts.Length < 2)
            {
                _debugLogger($"[Pipe] Invalid command format: {raw.Substring(0, Math.Min(raw.Length, 80))}");
                return PipeMessage.CreateResponse("ERROR", "Invalid command");
            }

            if (!Enum.TryParse<PipeCommand>(parts[1], out var cmd))
            {
                _debugLogger($"[Pipe] Unknown command: {parts[1]}");
                return PipeMessage.CreateResponse("ERROR", "Unknown command");
            }

            switch (cmd)
            {
                case PipeCommand.GetStatus:
                    {
                        var config = Config.Load(_configPath);
                        var hwId = CacheManager.GetHardwareId() ?? "ERROR";
                        var licenseStatus = LicenseManagerInternal.ValidateLicenseDetailed(config.LicenseKey, hwId);
                        _debugLogger($"[License] GetStatus: status={licenseStatus} hwId={hwId[..Math.Min(8, hwId.Length)]}...");

                        string status = licenseStatus switch
                        {
                            LicenseStatus.Active => "Active",
                            LicenseStatus.HardwareMismatch => "Hardware Mismatch",
                            LicenseStatus.Expired => "Expired",
                            LicenseStatus.Invalid => "Invalid",
                            LicenseStatus.Free => "Free",
                            _ => "Unknown"
                        };

                        return PipeMessage.CreateResponse("OK", status);
                    }

                case PipeCommand.ToggleMaintenance:
                    if (_commandHandler != null)
                    {
                        var result = await _commandHandler(cmd, parts);
                        return PipeMessage.CreateResponse("OK", result ?? "TOGGLED");
                    }
                    return PipeMessage.CreateResponse("OK", "TOGGLED");

                case PipeCommand.RefreshProtection:
                    if (_commandHandler != null)
                    {
                        var result = await _commandHandler(cmd, parts);
                        return PipeMessage.CreateResponse("OK", result ?? "REFRESHED");
                    }
                    return PipeMessage.CreateResponse("OK", "REFRESHED");

                case PipeCommand.PushConfig:
                    if (parts.Length < 3) return PipeMessage.CreateResponse("ERROR", "Missing payload");
                    try
                    {
                        var dataParts = parts[2].Split('|');
                        if (dataParts.Length < 2) return PipeMessage.CreateResponse("ERROR", "Malformed payload");
                        
                        var providedHash = dataParts[0];
                        var jsonBase64 = dataParts[1];
                        
                        // Basic auth check
                        var currentConfig = Config.Load(_configPath);
                        if (!string.IsNullOrEmpty(currentConfig.AdminPasswordHash) &&
                            currentConfig.AdminPasswordHash != providedHash)
                        {
                            _debugLogger("[License] PushConfig: authentication failed (wrong password hash).");
                            return PipeMessage.CreateResponse("ERROR", "Authentication failed");
                        }

                        var jsonBytes = Convert.FromBase64String(jsonBase64);
                        var newJson = Encoding.UTF8.GetString(jsonBytes);

                        // CRITICAL: Deserialize to Config and re-save using Config.Save()
                        // (canonical hand-rolled format). Writing the raw System.Text.Json output
                        // directly causes + and / in base64 fields to be stored as \u002B/\u002F.
                        // Our hand-rolled ParseString does NOT unescape those sequences, so every
                        // subsequent Config.Load()→Config.Save() cycle (e.g. DetectPlatforms)
                        // doubles the backslashes via EscapeJson — corrupting hash fields.
                        var parsedConfig = System.Text.Json.JsonSerializer.Deserialize(newJson, GamePinned.Shared.GamePinnedJsonContext.Default.Config);
                        if (parsedConfig != null)
                        {
                            parsedConfig.Save(_configPath);
                        }
                        else
                        {
                            // Fallback: write raw JSON if deserialization fails (shouldn't happen)
                            File.WriteAllText(_configPath, newJson);
                        }

                        // Trigger protection refresh to apply changes immediately
                        _onConfigChanged?.Invoke();

                        var savedConfig = Config.Load(_configPath);
                        var usersDesc = savedConfig.BlockedUsers.Count == 0 ? "all (default)" : string.Join(", ", savedConfig.BlockedUsers);
                        _debugLogger($"[Pipe] PushConfig: saved — maint={savedConfig.MaintenanceMode} blockedUsers={savedConfig.BlockedUsers.Count} [{usersDesc}] platforms={savedConfig.Platforms.Count}");

                        return PipeMessage.CreateResponse("OK", "CONFIG_SAVED");
                    }
                    catch (Exception ex)
                    {
                        _debugLogger($"[Pipe] PushConfig exception: {ex}");
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.ActivateLicense:
                    if (parts.Length < 3) return PipeMessage.CreateResponse("ERROR", "Missing license key");
                    try
                    {
                        var licenseKey = parts[2];
                        var hwId = CacheManager.GetHardwareId() ?? "ERROR";
                        var activateStatus = LicenseManagerInternal.ValidateLicenseDetailed(licenseKey, hwId);
                        _debugLogger($"[License] ActivateLicense: status={activateStatus} hwId={hwId[..Math.Min(8, hwId.Length)]}...");

                        if (activateStatus == LicenseStatus.Active)
                        {
                            return PipeMessage.CreateResponse("OK", "LICENSE_VALID");
                        }
                        else
                        {
                            return PipeMessage.CreateResponse("ERROR", "INVALID_LICENSE");
                        }
                    }
                    catch (Exception ex)
                    {
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.ToggleStartup:
                    if (parts.Length < 3) return PipeMessage.CreateResponse("ERROR", "Missing parameters");
                    try
                    {
                        var dataParts = parts[2].Split('|');
                        bool enable = bool.Parse(dataParts[0]);

                        // Launch with Windows is a paid-tier feature
                        if (enable)
                        {
                            var startupCfg = Config.Load(_configPath);
                            var startupHwId = CacheManager.GetHardwareId();
                            var startupLicenseValid = LicenseManagerInternal.ValidateLicense(startupCfg.LicenseKey, startupHwId);
                            if (!startupLicenseValid)
                                return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        }

                        using (Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                        {
                            if (key == null) return PipeMessage.CreateResponse("ERROR", "Registry key not found");

                            if (enable)
                            {
                                if (dataParts.Length < 2) return PipeMessage.CreateResponse("ERROR", "Missing exe path");
                                string exePath = dataParts[1];
                                key.SetValue("GamePinned", $"\"{exePath}\" --minimized");
                            }
                            else
                            {
                                key.DeleteValue("GamePinned", false);
                            }
                        }

                        // Also update StartupApproved — Windows Settings writes here to enable/disable startup
                        // apps independently of the Run key. Without this, toggling via Windows Settings
                        // overrides our Run key and the two toggles fight each other.
                        const string approvedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                        using (var approvedKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(approvedPath, true))
                        {
                            if (enable)
                                approvedKey?.SetValue("GamePinned", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
                            else
                                approvedKey?.DeleteValue("GamePinned", false);
                        }

                        _debugLogger($"[Pipe] ToggleStartup: enable={enable} — registry written");
                        return PipeMessage.CreateResponse("OK", "STARTUP_TOGGLED");
                    }
                    catch (Exception ex)
                    {
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.ClearLogs:
                    try
                    {
                        var logPath = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "logs");
                        int clearedCount = 0;
                        if (Directory.Exists(logPath))
                        {
                            var cutoff = DateTime.Now.AddDays(-LogRetentionDays).ToString("yyyyMMdd");
                            foreach (var file in Directory.GetFiles(logPath, "gamepinned_*.log"))
                            {
                                try
                                {
                                    var name = Path.GetFileNameWithoutExtension(file);
                                    // Extract date suffix (last 8 chars: yyyyMMdd)
                                    var datePart = name.Length >= 8 ? name[^8..] : "";
                                    if (string.Compare(datePart, cutoff, StringComparison.Ordinal) <= 0)
                                        File.Delete(file); // older than retention window → delete
                                    else
                                        File.WriteAllText(file, string.Empty); // within retention window → truncate
                                    clearedCount++;
                                }
                                catch { }
                            }
                        }
                        _debugLogger($"[Pipe] ClearLogs: {clearedCount} file(s) cleared");
                        return PipeMessage.CreateResponse("OK", "LOGS_CLEARED");
                    }
                    catch (Exception ex)
                    {
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.CheckForUpdate:
                    try
                    {
                        var updateInfo = await CheckForUpdatesAsync();
                        if (updateInfo == null)
                        {
                            return PipeMessage.CreateResponse("OK", "NO_UPDATE");
                        }
                        
                        var json = JsonSerializer.Serialize(updateInfo, GamePinnedJsonContext.Default.UpdateInfo);
                        return PipeMessage.CreateResponse("OK", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
                    }
                    catch (Exception ex)
                    {
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.GetAllData:
                    try
                    {
                        // Debug only — don't log routine GUI requests
                        var config = Config.Load(_configPath);
                        var hwId = CacheManager.GetHardwareId();

                        // Always use fresh license info (not cached) to avoid stale Free tier display
                        var licenseType = LicenseManager.GetLicenseInfo(config.LicenseKey);
                        var licenseExpiry = LicenseManager.GetExpiryDate(config.LicenseKey);
                        var getAllDataStatus = LicenseManagerInternal.ValidateLicenseDetailed(config.LicenseKey, hwId);
                        _debugLogger($"[License] GetAllData: type={licenseType} status={getAllDataStatus} expiry={licenseExpiry?.ToString("yyyy-MM-dd") ?? "none"} hwId={hwId[..Math.Min(8, hwId.Length)]}...");

                        // Get all games from platform manager (via callback)
                        var games = new List<GameInfo>();
                        try
                        {
                            if (_getAllGames != null)
                            {
                                // Use proper platform manager to get games (Epic, Riot, Steam)
                                games = _getAllGames();
                                // Silent — internal IPC
                            }
                        }
                        catch { /* Ignore game detection errors */ }

                        // Get recent logs
                        var recentLogs = new List<string>();
                        try
                        {
                            var logPath = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "logs");
                            if (Directory.Exists(logPath))
                            {
                                var logFiles = Directory.GetFiles(logPath, "gamepinned_*.log")
                                    .OrderByDescending(f => f)
                                    .Take(LogRetentionDays); // Match retention window
                                
                                foreach (var logFile in logFiles)
                                {
                                    // FileShare.ReadWrite is required because the logger holds
                                    // the file open with a shared write lock. File.ReadAllLines
                                    // uses FileShare.Read by default and throws IOException
                                    // ("file is being used by another process") on the active log.
                                    string[] fileLines;
                                    using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    using (var sr = new StreamReader(fs))
                                        fileLines = sr.ReadToEnd().Split('\n');

                                    var lines = fileLines
                                        .Select(l => l.TrimEnd('\r'))
                                        .Where(l => l.Length > 0)
                                        .Reverse()
                                        .Take(50); // Last 50 lines per file

                                    recentLogs.AddRange(lines);
                                }
                                
                                recentLogs = recentLogs
                                    .Take(100) // Total 100 most recent
                                    .Reverse()
                                    .ToList();
                            }
                        }
                        catch { /* Ignore log reading errors */ }

                        // Check startup registry — entry must exist in Run AND not be disabled in StartupApproved
                        bool startWithWindows = false;
                        try
                        {
                            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                            {
                                if (key != null && key.GetValue("GamePinned") != null)
                                {
                                    // Check StartupApproved — first byte 0x03 means disabled by Windows Settings
                                    bool approvedDisabled = false;
                                    using (var approvedKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false))
                                    {
                                        var approvedVal = approvedKey?.GetValue("GamePinned") as byte[];
                                        if (approvedVal != null && approvedVal.Length >= 1 && approvedVal[0] == 0x03)
                                            approvedDisabled = true;
                                    }
                                    startWithWindows = !approvedDisabled;
                                }
                            }
                        }
                        catch { /* Ignore registry errors */ }

                        var refreshData = new RefreshData
                        {
                            Games = games,
                            LicenseInfo = new LicenseInfo
                            {
                                HardwareId = hwId,
                                IsValid = !string.IsNullOrEmpty(config.LicenseKey) &&
                                         LicenseManagerInternal.ValidateLicense(config.LicenseKey, hwId),
                                LicenseType = licenseType,
                                ExpiryDate = licenseExpiry
                            },
                            ServiceRunning = true, // Service is running since we're processing this
                            BlockedUsers = config.BlockedUsers,
                            MaintenanceMode = config.MaintenanceMode,
                            AutoUpdateEnabled = config.AutoUpdateEnabled,
                            StartWithWindows = startWithWindows,
                            NvidiaPresent = _nvidiaManager.IsNvidiaPresent(),
                            NvidiaAutoRestore = config.NvidiaAutoRestore,
                            RecentLogs = recentLogs,
                            BlockAllNonAdminByDefault = config.BlockAllNonAdminByDefault,
                            ProtocolVersion = PipeConstants.PROTOCOL_VERSION,
                            Stats = new DashboardStats
                            {
                                ProtectedGamesCount = games.Count(g => g.IsProtected),  // Fixed: Count actually protected games, not non-excluded games
                                TotalGamesCount = games.Count,
                                BlockedUsersCount = config.BlockedUsers.Count,
                                ActivePlatformsCount = config.Platforms.Count(p => p.Enabled)
                            }
                        };

                        // Use source-generated JSON context for Native AOT compatibility
                        var json = System.Text.Json.JsonSerializer.Serialize(refreshData, GamePinned.Shared.Models.AppJsonContext.Default.RefreshData);
                        return PipeMessage.CreateResponse("OK", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
                    }
                    catch (Exception ex)
                    {
                        _logger($"Error loading data: {ex.Message}");
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.GetNvidiaStatus:
                    {
                        // Allowed on all tiers — read-only
                        var nvidiaStatus = _nvidiaManager.GetStatus();
                        var nvidiaJson = System.Text.Json.JsonSerializer.Serialize(nvidiaStatus, AppJsonContext.Default.NvidiaConfigInfo);
                        return PipeMessage.CreateResponse("OK", nvidiaJson);
                    }

                case PipeCommand.SaveNvidiaConfig:
                    {
                        // Paid-tier feature — parts[2] optionally contains the profile name
                        var nvCfg = Config.Load(_configPath);
                        var nvHwId = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfg.LicenseKey, nvHwId))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var profileName = parts.Length > 2 ? parts[2].Trim() : null;
                        var saveErr = _nvidiaManager.SaveConfig(string.IsNullOrEmpty(profileName) ? null : profileName);
                        _debugLogger($"[NVIDIA] SaveConfig: name='{profileName ?? "auto"}' result={(saveErr ?? "OK")}");
                        return PipeMessage.CreateResponse(saveErr == null ? "OK" : "ERROR", saveErr ?? "");
                    }

                case PipeCommand.RestoreNvidiaConfig:
                    {
                        // Restore the currently active profile — paid-tier feature
                        var nvCfg2 = Config.Load(_configPath);
                        var nvHwId2 = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfg2.LicenseKey, nvHwId2))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var restoreErr = _nvidiaManager.RestoreConfig();
                        _debugLogger($"[NVIDIA] RestoreConfig (active): result={(restoreErr ?? "OK")}");
                        return PipeMessage.CreateResponse(restoreErr == null ? "OK" : "ERROR", restoreErr ?? "");
                    }

                case PipeCommand.RestoreNvidiaProfile:
                    {
                        // Restore a specific profile by ID — paid-tier feature; parts[2] = profileId
                        var nvCfg3 = Config.Load(_configPath);
                        var nvHwId3 = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfg3.LicenseKey, nvHwId3))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var profileIdToRestore = parts.Length > 2 ? parts[2].Trim() : "";
                        if (string.IsNullOrEmpty(profileIdToRestore))
                            return PipeMessage.CreateResponse("ERROR", "Profile ID required.");
                        var restoreByIdErr = _nvidiaManager.RestoreConfigById(profileIdToRestore);
                        _debugLogger($"[NVIDIA] RestoreProfile '{profileIdToRestore}': result={(restoreByIdErr ?? "OK")}");
                        return PipeMessage.CreateResponse(restoreByIdErr == null ? "OK" : "ERROR", restoreByIdErr ?? "");
                    }

                case PipeCommand.RenameNvidiaProfile:
                    {
                        // Paid-tier feature — parts[2] = "profileId|newName"
                        // NOTE: PipeMessage.Parse splits on '|' with limit 3, so parts[2] preserves the
                        // second '|' separator between profileId and newName intact.
                        var nvCfgR = Config.Load(_configPath);
                        var nvHwIdR = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfgR.LicenseKey, nvHwIdR))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var renameData = parts.Length > 2 ? parts[2] : "";
                        var pipeIdx = renameData.IndexOf('|');
                        if (pipeIdx < 0)
                            return PipeMessage.CreateResponse("ERROR", "Expected format: profileId|newName");
                        var renameId = renameData[..pipeIdx].Trim();
                        var newName = renameData[(pipeIdx + 1)..].Trim();
                        var renameErr = _nvidiaManager.RenameProfile(renameId, newName);
                        _debugLogger($"[NVIDIA] RenameProfile '{renameId}' → '{newName}': result={(renameErr ?? "OK")}");
                        return PipeMessage.CreateResponse(renameErr == null ? "OK" : "ERROR", renameErr ?? "");
                    }

                case PipeCommand.DeleteNvidiaProfile:
                    {
                        // Paid-tier feature — parts[2] = profileId
                        var nvCfgD = Config.Load(_configPath);
                        var nvHwIdD = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfgD.LicenseKey, nvHwIdD))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var profileIdToDelete = parts.Length > 2 ? parts[2].Trim() : "";
                        if (string.IsNullOrEmpty(profileIdToDelete))
                            return PipeMessage.CreateResponse("ERROR", "Profile ID required.");
                        var deleteErr = _nvidiaManager.DeleteProfile(profileIdToDelete);
                        _debugLogger($"[NVIDIA] DeleteProfile '{profileIdToDelete}': result={(deleteErr ?? "OK")}");
                        return PipeMessage.CreateResponse(deleteErr == null ? "OK" : "ERROR", deleteErr ?? "");
                    }

                case PipeCommand.SetActiveNvidiaProfile:
                    {
                        // Paid-tier feature — parts[2] = profileId
                        var nvCfgA = Config.Load(_configPath);
                        var nvHwIdA = CacheManager.GetHardwareId() ?? "";
                        if (!LicenseManagerInternal.ValidateLicense(nvCfgA.LicenseKey, nvHwIdA))
                            return PipeMessage.CreateResponse("ERROR", "PAID_TIER_REQUIRED");
                        var profileIdToActivate = parts.Length > 2 ? parts[2].Trim() : "";
                        if (string.IsNullOrEmpty(profileIdToActivate))
                            return PipeMessage.CreateResponse("ERROR", "Profile ID required.");
                        var setActiveErr = _nvidiaManager.SetActiveProfile(profileIdToActivate);
                        _debugLogger($"[NVIDIA] SetActiveProfile '{profileIdToActivate}': result={(setActiveErr ?? "OK")}");
                        return PipeMessage.CreateResponse(setActiveErr == null ? "OK" : "ERROR", setActiveErr ?? "");
                    }

                case PipeCommand.ClearNvidiaConfig:
                    {
                        // Allowed on all tiers — removes a paid-tier artifact
                        _nvidiaManager.ClearConfig();
                        _debugLogger("[NVIDIA] ClearConfig: all profiles deleted");
                        return PipeMessage.CreateResponse("OK", "");
                    }

                case PipeCommand.SetNvidiaAutoRestore:
                    {
                        // Allowed on all tiers — writes NvidiaAutoRestore to config.json via
                        // the service (avoids the TOCTOU race of a direct GUI file write).
                        var rawVal = parts.Length > 2 ? parts[2].Trim() : "";
                        var enabled = rawVal.Equals("true", StringComparison.OrdinalIgnoreCase);
                        try
                        {
                            var cfg = Config.Load(_configPath);
                            cfg.NvidiaAutoRestore = enabled;
                            cfg.Save(_configPath);
                            _debugLogger($"[NVIDIA] SetNvidiaAutoRestore: {enabled}");
                            return PipeMessage.CreateResponse("OK", "");
                        }
                        catch (Exception ex)
                        {
                            _debugLogger($"[NVIDIA] SetNvidiaAutoRestore error: {ex.Message}");
                            return PipeMessage.CreateResponse("ERROR", ex.Message);
                        }
                    }

                case PipeCommand.ClearPlatformSession:
                    {
                        // parts[2] = platformId ("steam", "epic", "riot")
                        var platformId = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : "";
                        if (string.IsNullOrEmpty(platformId))
                            return PipeMessage.CreateResponse("ERROR", "Platform ID required");
                        if (_commandHandler != null)
                        {
                            var result = await _commandHandler(cmd, parts);
                            return PipeMessage.CreateResponse("OK", result ?? "SESSION_CLEARED");
                        }
                        return PipeMessage.CreateResponse("OK", "SESSION_CLEARED");
                    }

                case PipeCommand.GetDiagnosticLogs:
                    try
                    {
                        var logDir = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "logs");
                        var sb = new System.Text.StringBuilder();
                        if (Directory.Exists(logDir))
                        {
                            var debugFiles = Directory.GetFiles(logDir, "debug_*.log")
                                .OrderByDescending(f => f)
                                .Take(2);
                            foreach (var file in debugFiles)
                            {
                                try
                                {
                                    sb.AppendLine("=== " + Path.GetFileName(file) + " ===");
                                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    using var sr = new StreamReader(fs);
                                    sb.Append(sr.ReadToEnd());
                                }
                                catch (Exception ex) { _debugLogger($"[Pipe] GetDiagnosticLogs: {Path.GetFileName(file)} read error: {ex.Message}"); }
                            }
                        }
                        _debugLogger($"[Pipe] GetDiagnosticLogs: returned {sb.Length} chars");
                        return PipeMessage.CreateResponse("OK", sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        _debugLogger($"[Pipe] GetDiagnosticLogs exception: {ex}");
                        return PipeMessage.CreateResponse("ERROR", ex.Message);
                    }

                case PipeCommand.RestartService:
                    {
                        // GUI enforces auth via RequireAuth() before this is ever called.
                        // No service-side credential check needed — knowing the GUI password
                        // already lets you unlock games directly, so service stop adds nothing.
                        if (_stopAction == null)
                            return PipeMessage.CreateResponse("ERROR", "Stop not available");
                        _debugLogger("[Pipe] RestartService: stopping service.");
                        _ = Task.Run(async () => { await Task.Delay(500); _stopAction(); });
                        return PipeMessage.CreateResponse("OK", "STOPPING");
                    }

                default:
                    return PipeMessage.CreateResponse("OK", "RECEIVED");
            }
        }

        /// <summary>
        /// Send a notification to the GUI (fire and forget)
        /// </summary>
        public void SendNotification(ServiceNotificationType type, string title, string message)
        {
            Task.Run(async () =>
            {
                try
                {
                    using var pipeClient = new NamedPipeClientStream(".", "GamePinnedGUINotify", PipeDirection.Out);
                    await pipeClient.ConnectAsync(2000);

                    var notification = PipeMessage.CreateNotification(type, title, message);
                    var bytes = Encoding.UTF8.GetBytes(notification);
                    await pipeClient.WriteAsync(bytes, 0, bytes.Length);
                }
                catch
                {
                    // GUI not running, ignore
                }
            });
        }

        /// <summary>
        /// Check for updates from the Cloudflare Worker update server
        /// </summary>
        private async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                
                // Get current version from assembly attributes (AOT/Single-file safe)
                var asm = Assembly.GetExecutingAssembly();
                var currentVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion 
                                     ?? asm.GetName().Version?.ToString() 
                                     ?? "1.0.0";
                
                // Call your existing Cloudflare Worker update endpoint (same domain as licensing)
                var response = await httpClient.GetAsync("https://api.example.com/version");
                if (!response.IsSuccessStatusCode)
                {
                    _debugLogger($"[Update] CheckForUpdates: HTTP {(int)response.StatusCode} — non-success");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, GamePinnedJsonContext.Default.UpdateInfo);

                if (updateInfo == null)
                {
                    _debugLogger("[Update] CheckForUpdates: failed to parse version response");
                    return null;
                }

                // Compare versions
                if (IsNewerVersion(updateInfo.Version, currentVersion))
                {
                    _debugLogger($"[Update] CheckForUpdates: update available ({currentVersion} → {updateInfo.Version})");
                    return updateInfo;
                }
                else
                {
                    _debugLogger($"[Update] CheckForUpdates: up to date (current={currentVersion} latest={updateInfo.Version})");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Update] CheckForUpdates: failed — {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Compare version strings to determine if update is newer
        /// </summary>
        private bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                var latest = Version.Parse(latestVersion);
                var current = Version.Parse(currentVersion);
                return latest > current;
            }
            catch
            {
                // If parsing fails, assume update is available
                return true;
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
