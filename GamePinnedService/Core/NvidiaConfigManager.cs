using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// Manages NVIDIA settings save/restore for gaming cafe consistency.
    /// Supports multiple named profiles stored under nvidia-config/profiles/.
    /// Backs up: NVIDIA DRS database files (3D settings) + Windows display config (resolution/refresh rate).
    /// Thread-safe: all index read/write operations are serialised by _indexLock.
    /// </summary>
    internal class NvidiaConfigManager
    {
        private static readonly string DrsDatabasePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "NVIDIA Corporation", "Drs");

        private static readonly string[] DrsFiles = { "nvdrsdb0.bin", "nvdrsdb1.bin", "nvdrssel.bin" };

        // Max profiles enforced on save.
        private const int MaxProfiles = 5;

        private readonly string _backupPath;   // nvidia-config/ root
        private readonly Action<string> _logger;
        private readonly Action<string> _debugLogger;

        // Serialises all profiles.json read/write operations.
        private readonly object _indexLock = new();

        // Cached GPU detection results (stable for service lifetime).
        private string? _cachedNvidiaSmiPath;
        private bool _nvidiaSmiPathCached;
        private GpuInfo? _cachedGpuInfo;
        private bool _gpuInfoCached;

        // ─── Paths ─────────────────────────────────────────────────────────────

        private string ProfilesDir => Path.Combine(_backupPath, "profiles");
        private string ProfilesIndexPath => Path.Combine(_backupPath, "profiles.json");
        private string ProfileDir(string id) => Path.Combine(ProfilesDir, id);

        // ─── Constructor ───────────────────────────────────────────────────────

        public NvidiaConfigManager(string backupPath, Action<string> logger, Action<string>? debugLogger = null)
        {
            _backupPath = backupPath;
            _logger = logger;
            _debugLogger = debugLogger ?? (_ => { });
        }

        // ─── Detection ─────────────────────────────────────────────────────────

        public bool IsNvidiaPresent()
        {
            if (Directory.Exists(DrsDatabasePath))
                return true;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm");
                if (key != null) return true;
            }
            catch { }

            return false;
        }

        public GpuInfo? GetGpuInfo()
        {
            if (_gpuInfoCached) return _cachedGpuInfo;

            try
            {
                var nvidiaSmiPath = FindNvidiaSmi();
                if (nvidiaSmiPath == null)
                {
                    _debugLogger("[NVIDIA] GetGpuInfo: nvidia-smi not found in any known location");
                    _gpuInfoCached = true;
                    _cachedGpuInfo = null;
                    return null;
                }

                _debugLogger($"[NVIDIA] GetGpuInfo: using {nvidiaSmiPath}");

                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName = nvidiaSmiPath,
                    Arguments = "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                proc.Start();
                var output = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(); } catch { }
                    _debugLogger("[NVIDIA] GetGpuInfo: nvidia-smi timed out");
                    return null;
                }

                _debugLogger($"[NVIDIA] GetGpuInfo: exit={proc.ExitCode} output=\"{output.Trim()}\" stderr=\"{stderr.Trim()}\"");

                var line = output.Trim();
                if (string.IsNullOrEmpty(line)) return null;

                var parts = line.Split(',');
                if (parts.Length < 3) return null;

                _cachedGpuInfo = new GpuInfo
                {
                    Name = parts[0].Trim(),
                    DriverVersion = parts[1].Trim(),
                    VramMb = parts[2].Trim()
                };
                _gpuInfoCached = true;
                return _cachedGpuInfo;
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] GetGpuInfo: exception — {ex.Message}");
                _gpuInfoCached = true;
                _cachedGpuInfo = null;
                return null;
            }
        }

        private string? FindNvidiaSmi()
        {
            if (_nvidiaSmiPathCached) return _cachedNvidiaSmiPath;

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

            var candidates = new[]
            {
                Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                Path.Combine(sys32, "nvidia-smi.exe"),
                Path.Combine(pf, "NVIDIA Corporation", "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                             "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            };

            foreach (var path in candidates)
            {
                _debugLogger($"[NVIDIA] FindNvidiaSmi: checking {path} — exists={File.Exists(path)}");
                if (File.Exists(path))
                {
                    _cachedNvidiaSmiPath = path;
                    _nvidiaSmiPathCached = true;
                    return path;
                }
            }

            // Fall back to PATH — if not found there either, GetGpuInfo catches the exception
            _debugLogger("[NVIDIA] FindNvidiaSmi: falling back to PATH lookup");
            _cachedNvidiaSmiPath = "nvidia-smi";
            _nvidiaSmiPathCached = true;
            return "nvidia-smi";
        }

        // ─── Profile Index helpers (MUST be called while holding _indexLock) ───

        private NvidiaProfilesIndex ReadIndexInternal()
        {
            if (!File.Exists(ProfilesIndexPath))
                return new NvidiaProfilesIndex();

            try
            {
                var json = File.ReadAllText(ProfilesIndexPath);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.NvidiaProfilesIndex)
                       ?? new NvidiaProfilesIndex();
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] ReadIndexInternal: parse error — {ex.Message}");
                return new NvidiaProfilesIndex();
            }
        }

        /// <summary>
        /// Atomic write: serialise to a .tmp file then rename over the target.
        /// File.Move with overwrite is atomic on NTFS — a torn write can never produce a corrupt index.
        /// </summary>
        private void WriteIndexInternal(NvidiaProfilesIndex index)
        {
            Directory.CreateDirectory(_backupPath);
            var json = JsonSerializer.Serialize(index, AppJsonContext.Default.NvidiaProfilesIndex);
            var tmpPath = ProfilesIndexPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, ProfilesIndexPath, overwrite: true);
        }

        private static string GenerateProfileId(DateTime dt)
        {
            // Include milliseconds so rapid saves within the same second produce different IDs.
            return $"p_{dt:yyyyMMdd_HHmmss_fff}";
        }

        // ─── Status ────────────────────────────────────────────────────────────

        public NvidiaConfigInfo GetStatus()
        {
            bool drsExists = Directory.Exists(DrsDatabasePath);
            var gpu = GetGpuInfo();
            bool present = drsExists || gpu != null;

            var info = new NvidiaConfigInfo { IsNvidiaPresent = present, Gpu = gpu };

            NvidiaProfilesIndex index;
            lock (_indexLock) { index = ReadIndexInternal(); }

            info.Profiles = index.Profiles ?? new List<NvidiaProfile>();
            info.ActiveProfileId = index.ActiveProfileId;
            info.HasSavedConfig = info.Profiles.Count > 0;

            var active = info.Profiles.FirstOrDefault(p => p.Id == index.ActiveProfileId)
                         ?? info.Profiles.OrderByDescending(p => p.SavedAt).FirstOrDefault();
            if (active != null)
            {
                info.SavedAt = active.SavedAt;
                info.HasSavedDisplayConfig = active.MonitorCount > 0;
                info.SavedMonitorCount = active.MonitorCount;
                info.FilesCopied = Enumerable.Range(0, active.FileCount)
                                             .Select(i => DrsFiles.ElementAtOrDefault(i) ?? "")
                                             .Where(f => f.Length > 0)
                                             .ToList();
            }

            _debugLogger($"[NVIDIA] GetStatus: present={present} profiles={info.Profiles.Count} active={info.ActiveProfileId ?? "none"} gpu={gpu?.Name ?? "none"}");
            return info;
        }

        // ─── Save ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Saves current NVIDIA DRS + display settings as a new named profile.
        /// Returns null on success, or a user-facing error message.
        /// Enforces a maximum of MaxProfiles (5) saved profiles.
        /// The entire index read-modify-write is performed under _indexLock to prevent races.
        /// </summary>
        public string? SaveConfig(string? profileName = null)
        {
            if (!IsNvidiaPresent())
                return "NVIDIA GPU not detected on this system.";

            if (!Directory.Exists(DrsDatabasePath))
                return "NVIDIA settings folder not found. Open NVIDIA Control Panel or NVIDIA App once to initialize it, then try again.";

            lock (_indexLock)
            {
                var index = ReadIndexInternal();

                // Enforce profile limit
                if (index.Profiles.Count >= MaxProfiles)
                    return $"Maximum of {MaxProfiles} profiles reached. Delete an existing profile first.";

                // Auto-name: find the lowest unused "Profile N" rather than using Count+1,
                // which produces duplicates after deletions.
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    var existingNames = new HashSet<string>(
                        index.Profiles.Select(p => p.Name),
                        StringComparer.OrdinalIgnoreCase);
                    int n = 1;
                    while (existingNames.Contains($"Profile {n}")) n++;
                    profileName = $"Profile {n}";
                }
                else
                {
                    profileName = profileName.Trim();
                    if (profileName.Length > 64) profileName = profileName[..64];
                }

                var now = DateTime.Now;
                var profileId = GenerateProfileId(now);
                var profileDir = ProfileDir(profileId);
                bool dirCreated = false;

                try
                {
                    Directory.CreateDirectory(profileDir);
                    dirCreated = true;
                    _debugLogger($"[NVIDIA] SaveConfig: profileId='{profileId}' name='{profileName}' dir='{profileDir}'");

                    var copied = new List<string>();
                    foreach (var fileName in DrsFiles)
                    {
                        var src = Path.Combine(DrsDatabasePath, fileName);
                        if (!File.Exists(src))
                        {
                            _debugLogger($"[NVIDIA] Save: {fileName} not found, skipping.");
                            continue;
                        }

                        var dest = Path.Combine(profileDir, fileName);
                        try
                        {
                            File.Copy(src, dest, overwrite: true);
                            copied.Add(fileName);
                            _debugLogger($"[NVIDIA] Saved DRS to profile '{profileId}': {fileName}");
                        }
                        catch (IOException ioEx) when (IsFileLocked(ioEx))
                        {
                            CleanupDir(profileDir);
                            return "Close the NVIDIA App or Control Panel and try again.";
                        }
                    }

                    if (copied.Count == 0)
                    {
                        CleanupDir(profileDir);
                        return "No NVIDIA settings files found to save.";
                    }

                    var displayConfigs = SaveDisplaySettings();
                    _debugLogger($"[NVIDIA] Saved display configs for {displayConfigs.Count} monitor(s) in profile '{profileId}'.");

                    var meta = new NvidiaMeta
                    {
                        SavedAt = now,
                        FilesCopied = copied,
                        DisplayConfigs = displayConfigs
                    };
                    var metaJson = JsonSerializer.Serialize(meta, AppJsonContext.Default.NvidiaMeta);
                    File.WriteAllText(Path.Combine(profileDir, "meta.json"), metaJson);

                    var profile = new NvidiaProfile
                    {
                        Id = profileId,
                        Name = profileName,
                        SavedAt = now,
                        FileCount = copied.Count,
                        MonitorCount = displayConfigs.Count
                    };
                    index.Profiles.Add(profile);

                    // Only auto-promote to active if there was no previous active profile
                    // (first save or all profiles deleted). This prevents silently demoting
                    // a deliberately chosen auto-restore profile when saving a test snapshot.
                    if (string.IsNullOrEmpty(index.ActiveProfileId))
                        index.ActiveProfileId = profileId;

                    WriteIndexInternal(index);

                    _logger($"[NVIDIA] Profile '{profileName}' saved ({displayConfigs.Count} monitor(s)).");
                    return null;
                }
                catch (Exception ex)
                {
                    if (dirCreated) CleanupDir(profileDir);
                    _debugLogger($"[NVIDIA] SaveConfig error: {ex}");
                    return $"Save failed: {ex.Message}";
                }
            }
        }

        // ─── Restore ───────────────────────────────────────────────────────────

        /// <summary>Restores the currently active profile. Returns null on success or an error message.</summary>
        public string? RestoreConfig()
        {
            string? profileId;
            lock (_indexLock)
            {
                var index = ReadIndexInternal();
                if (index.Profiles.Count == 0)
                    return "No saved NVIDIA profiles found.";
                profileId = index.ActiveProfileId
                            ?? index.Profiles.OrderByDescending(p => p.SavedAt).First().Id;
            }

            return RestoreConfigById(profileId);
        }

        /// <summary>Restores a specific profile by ID. Returns null on success or an error message.</summary>
        public string? RestoreConfigById(string profileId)
        {
            _debugLogger($"[NVIDIA] RestoreConfigById: profileId='{profileId}'");
            if (!IsNvidiaPresent())
                return "NVIDIA GPU not detected on this system.";

            if (!Directory.Exists(DrsDatabasePath))
                return "NVIDIA settings folder not found. Open NVIDIA Control Panel or NVIDIA App once to initialize it, then try again.";

            var profileDir = ProfileDir(profileId);
            var metaPath = Path.Combine(profileDir, "meta.json");

            if (!Directory.Exists(profileDir) || !File.Exists(metaPath))
                return $"Profile '{profileId}' not found.";

            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize(json, AppJsonContext.Default.NvidiaMeta);

                if (meta?.FilesCopied == null || meta.FilesCopied.Count == 0)
                    return "Saved profile is empty or corrupted.";

                var restored = 0;
                foreach (var fileName in meta.FilesCopied)
                {
                    var src = Path.Combine(profileDir, fileName);
                    if (!File.Exists(src))
                    {
                        _debugLogger($"[NVIDIA] Restore: backup file missing — {fileName}");
                        continue;
                    }

                    var dest = Path.Combine(DrsDatabasePath, fileName);
                    try
                    {
                        File.Copy(src, dest, overwrite: true);
                    }
                    catch (IOException ioEx) when (IsFileLocked(ioEx))
                    {
                        return "Close the NVIDIA App or Control Panel and try again.";
                    }

                    var srcLen = new FileInfo(src).Length;
                    var destLen = new FileInfo(dest).Length;
                    if (srcLen != destLen)
                    {
                        _debugLogger($"[NVIDIA] Restore: size mismatch on {fileName} (src={srcLen}, dest={destLen})");
                        return $"Restore verification failed for {fileName}.";
                    }

                    _debugLogger($"[NVIDIA] Restored DRS: {fileName}");
                    restored++;
                }

                if (restored == 0)
                    return "No backup files could be restored (files missing from backup).";

                if (meta.DisplayConfigs?.Count > 0)
                {
                    RestoreDisplaySettings(meta.DisplayConfigs);
                    _debugLogger($"[NVIDIA] Restored display settings for {meta.DisplayConfigs.Count} monitor(s).");
                }

                string profileName;
                lock (_indexLock)
                {
                    var index = ReadIndexInternal();
                    profileName = index.Profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? profileId;
                }

                _logger($"[NVIDIA] Profile '{profileName}' restored ({meta.DisplayConfigs?.Count ?? 0} monitor(s)).");
                return null;
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] RestoreConfigById error: {ex}");
                return $"Restore failed: {ex.Message}";
            }
        }

        public bool RestoreConfigOnStartup()
        {
            try
            {
                string? profileId;
                lock (_indexLock)
                {
                    var index = ReadIndexInternal();
                    if (index.Profiles.Count == 0) return false;
                    if (!IsNvidiaPresent()) return false;
                    profileId = index.ActiveProfileId
                                ?? index.Profiles.OrderByDescending(p => p.SavedAt).First().Id;
                }

                var err = RestoreConfigById(profileId);
                if (err != null)
                {
                    _debugLogger($"[NVIDIA] Startup restore skipped: {err}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] RestoreConfigOnStartup error: {ex}");
                return false;
            }
        }

        // ─── Rename ────────────────────────────────────────────────────────────

        /// <summary>Renames a profile. Returns null on success or an error message.</summary>
        public string? RenameProfile(string profileId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "Profile name cannot be empty.";

            newName = newName.Trim();
            if (newName.Length > 64)
                newName = newName[..64];

            lock (_indexLock)
            {
                try
                {
                    var index = ReadIndexInternal();
                    var profile = index.Profiles.FirstOrDefault(p => p.Id == profileId);
                    if (profile == null)
                        return $"Profile '{profileId}' not found.";

                    var oldName = profile.Name;
                    profile.Name = newName;
                    WriteIndexInternal(index);

                    _debugLogger($"[NVIDIA] Renamed profile '{profileId}': '{oldName}' → '{newName}'");
                    return null;
                }
                catch (Exception ex)
                {
                    _debugLogger($"[NVIDIA] RenameProfile error: {ex.Message}");
                    return $"Rename failed: {ex.Message}";
                }
            }
        }

        // ─── Delete ────────────────────────────────────────────────────────────

        /// <summary>Deletes a single profile. If it was active, promotes the next most-recent. Returns null on success.</summary>
        public string? DeleteProfile(string profileId)
        {
            string? dirToDelete = null;
            lock (_indexLock)
            {
                try
                {
                    var index = ReadIndexInternal();
                    var profile = index.Profiles.FirstOrDefault(p => p.Id == profileId);
                    if (profile == null)
                        return $"Profile '{profileId}' not found.";

                    index.Profiles.Remove(profile);

                    if (index.ActiveProfileId == profileId)
                    {
                        index.ActiveProfileId = index.Profiles
                            .OrderByDescending(p => p.SavedAt)
                            .FirstOrDefault()?.Id;
                    }

                    WriteIndexInternal(index);
                    dirToDelete = ProfileDir(profileId);

                    _debugLogger($"[NVIDIA] Deleted profile '{profileId}'. New active: {index.ActiveProfileId ?? "none"}");
                }
                catch (Exception ex)
                {
                    _debugLogger($"[NVIDIA] DeleteProfile error: {ex.Message}");
                    return $"Delete failed: {ex.Message}";
                }
            }

            // Directory deletion is outside the lock — the index no longer references this dir,
            // so no other operation can race on it.
            if (dirToDelete != null && Directory.Exists(dirToDelete))
                CleanupDir(dirToDelete);

            return null;
        }

        // ─── Set Active ────────────────────────────────────────────────────────

        /// <summary>Sets which profile is used for auto-restore on startup. Returns null on success.</summary>
        public string? SetActiveProfile(string profileId)
        {
            lock (_indexLock)
            {
                try
                {
                    var index = ReadIndexInternal();
                    if (!index.Profiles.Any(p => p.Id == profileId))
                        return $"Profile '{profileId}' not found.";

                    index.ActiveProfileId = profileId;
                    WriteIndexInternal(index);

                    _debugLogger($"[NVIDIA] Active profile set to '{profileId}'.");
                    return null;
                }
                catch (Exception ex)
                {
                    _debugLogger($"[NVIDIA] SetActiveProfile error: {ex.Message}");
                    return $"Failed: {ex.Message}";
                }
            }
        }

        // ─── Clear All ─────────────────────────────────────────────────────────

        /// <summary>Deletes ALL saved profiles. Allowed on any tier.</summary>
        public void ClearConfig()
        {
            lock (_indexLock)
            {
                try
                {
                    if (Directory.Exists(ProfilesDir))
                        CleanupDir(ProfilesDir);

                    if (File.Exists(ProfilesIndexPath))
                        try { File.Delete(ProfilesIndexPath); } catch { }

                    // Clean up any legacy flat files that may remain
                    foreach (var fileName in DrsFiles)
                    {
                        var path = Path.Combine(_backupPath, fileName);
                        if (File.Exists(path))
                            try { File.Delete(path); } catch { }
                    }
                    var legacyMeta = Path.Combine(_backupPath, "meta.json");
                    if (File.Exists(legacyMeta))
                        try { File.Delete(legacyMeta); } catch { }

                    _debugLogger("[NVIDIA] All profiles cleared.");
                }
                catch (Exception ex)
                {
                    _debugLogger($"[NVIDIA] ClearConfig error: {ex}");
                }
            }
        }

        // ─── Display Settings (Win32) ──────────────────────────────────────────

        private List<DisplayMonitorConfig> SaveDisplaySettings()
        {
            var monitors = new List<DisplayMonitorConfig>();
            try
            {
                unsafe
                {
                    var device = new DISPLAY_DEVICE();
                    device.cb = (uint)sizeof(DISPLAY_DEVICE);
                    uint devNum = 0;

                    while (NativeMethods.EnumDisplayDevices(null, devNum, ref device, 0))
                    {
                        const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
                        if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                        {
                            string devName = device.GetDeviceName();
                            var devMode = new DEVMODE();
                            devMode.dmSize = (ushort)sizeof(DEVMODE);

                            if (NativeMethods.EnumDisplaySettings(devName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
                            {
                                monitors.Add(new DisplayMonitorConfig
                                {
                                    DeviceName = devName,
                                    Width = (int)devMode.dmPelsWidth,
                                    Height = (int)devMode.dmPelsHeight,
                                    RefreshRate = (int)devMode.dmDisplayFrequency,
                                    BitsPerPel = (int)devMode.dmBitsPerPel,
                                    Orientation = (int)devMode.dmDisplayOrientation,
                                    PositionX = devMode.dmPositionX,
                                    PositionY = devMode.dmPositionY
                                });
                                _debugLogger($"[NVIDIA] Display saved: {devName} {devMode.dmPelsWidth}x{devMode.dmPelsHeight}@{devMode.dmDisplayFrequency}Hz");
                            }
                        }

                        devNum++;
                        device.cb = (uint)sizeof(DISPLAY_DEVICE);
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] SaveDisplaySettings error: {ex.Message}");
            }

            if (monitors.Count == 0)
                _debugLogger("[NVIDIA] SaveDisplaySettings: no monitors captured (session may have no display attached).");

            return monitors;
        }

        private void RestoreDisplaySettings(List<DisplayMonitorConfig> monitors)
        {
            try
            {
                unsafe
                {
                    const uint CDS_UPDATEREGISTRY = 0x00000001;
                    const uint CDS_NORESET = 0x10000000;
                    const uint DM_PELSWIDTH = 0x80000;
                    const uint DM_PELSHEIGHT = 0x100000;
                    const uint DM_DISPLAYFREQUENCY = 0x400000;
                    const uint DM_BITSPERPEL = 0x40000;
                    const uint DM_DISPLAYORIENTATION = 0x80;
                    const uint DM_POSITION = 0x20;

                    foreach (var monitor in monitors)
                    {
                        var devMode = new DEVMODE();
                        devMode.dmSize = (ushort)sizeof(DEVMODE);
                        devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY
                                         | DM_BITSPERPEL | DM_POSITION | DM_DISPLAYORIENTATION;
                        devMode.dmPelsWidth = (uint)monitor.Width;
                        devMode.dmPelsHeight = (uint)monitor.Height;
                        devMode.dmDisplayFrequency = (uint)monitor.RefreshRate;
                        devMode.dmBitsPerPel = (uint)monitor.BitsPerPel;
                        devMode.dmDisplayOrientation = (uint)monitor.Orientation;
                        devMode.dmPositionX = monitor.PositionX;
                        devMode.dmPositionY = monitor.PositionY;

                        int result = NativeMethods.ChangeDisplaySettingsEx(
                            monitor.DeviceName, ref devMode, IntPtr.Zero,
                            CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                        _debugLogger($"[NVIDIA] Display restore: {monitor.DeviceName} {monitor.Width}x{monitor.Height}@{monitor.RefreshRate}Hz → result={result}");
                    }

                    int applyResult = NativeMethods.ChangeDisplaySettingsEx(
                        null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    _debugLogger($"[NVIDIA] Display apply all: result={applyResult}");
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[NVIDIA] RestoreDisplaySettings error: {ex.Message}");
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────

        private static bool IsFileLocked(IOException ex) =>
            ex.HResult == unchecked((int)0x80070020) || ex.HResult == unchecked((int)0x80070021);

        private static void CleanupDir(string path)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }

        // ─── Win32 P/Invoke ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct DEVMODE
        {
            internal fixed char dmDeviceName[32];
            internal ushort dmSpecVersion;
            internal ushort dmDriverVersion;
            internal ushort dmSize;
            internal ushort dmDriverExtra;
            internal uint dmFields;
            internal int dmPositionX;
            internal int dmPositionY;
            internal uint dmDisplayOrientation;
            internal uint dmDisplayFixedOutput;
            internal short dmColor;
            internal short dmDuplex;
            internal short dmYResolution;
            internal short dmTTOption;
            internal short dmCollate;
            internal fixed char dmFormName[32];
            internal ushort dmLogPixels;
            internal uint dmBitsPerPel;
            internal uint dmPelsWidth;
            internal uint dmPelsHeight;
            internal uint dmDisplayFlags;
            internal uint dmDisplayFrequency;
            internal uint dmICMMethod;
            internal uint dmICMIntent;
            internal uint dmMediaType;
            internal uint dmDitherType;
            internal uint dmReserved1;
            internal uint dmReserved2;
            internal uint dmPanningWidth;
            internal uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct DISPLAY_DEVICE
        {
            internal uint cb;
            internal fixed char DeviceName[32];
            internal fixed char DeviceString[128];
            internal uint StateFlags;
            internal fixed char DeviceID[128];
            internal fixed char DeviceKey[128];

            internal unsafe string GetDeviceName()
            {
                fixed (char* p = DeviceName)
                {
                    int len = 0;
                    while (len < 32 && p[len] != '\0') len++;
                    return new string(p, 0, len);
                }
            }
        }

        private static class NativeMethods
        {
            internal const uint ENUM_CURRENT_SETTINGS = unchecked((uint)-1);

            [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool EnumDisplayDevices(
                string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

            [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool EnumDisplaySettings(
                string lpszDeviceName, uint iModeNum, ref DEVMODE lpDevMode);

            [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern int ChangeDisplaySettingsEx(
                string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwFlags, IntPtr lParam);

            [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern int ChangeDisplaySettingsEx(
                string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwFlags, IntPtr lParam);
        }
    }
}
