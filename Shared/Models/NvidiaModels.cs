using System;
using System.Collections.Generic;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// A single saved NVIDIA profile entry in the profiles index.
    /// </summary>
    public class NvidiaProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public int FileCount { get; set; }
        public int MonitorCount { get; set; }
    }

    /// <summary>
    /// Serialized to nvidia-config/profiles.json — index of all saved profiles.
    /// </summary>
    public class NvidiaProfilesIndex
    {
        public List<NvidiaProfile> Profiles { get; set; } = new();
        /// <summary>Profile ID used by auto-restore on startup. Defaults to the most recently saved profile.</summary>
        public string? ActiveProfileId { get; set; }
    }

    /// <summary>
    /// Status of NVIDIA profile save/restore feature — returned by GetNvidiaStatus pipe command.
    /// </summary>
    public class NvidiaConfigInfo
    {
        public bool IsNvidiaPresent { get; set; }
        /// <summary>True when at least one profile exists (kept for backward compat).</summary>
        public bool HasSavedConfig { get; set; }
        public bool HasSavedDisplayConfig { get; set; }
        public int SavedMonitorCount { get; set; }
        public DateTime? SavedAt { get; set; }
        public List<string> FilesCopied { get; set; } = new();
        public GpuInfo? Gpu { get; set; }
        public List<NvidiaProfile> Profiles { get; set; } = new();
        public string? ActiveProfileId { get; set; }
    }

    /// <summary>
    /// GPU hardware info from nvidia-smi
    /// </summary>
    public class GpuInfo
    {
        public string Name { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public string VramMb { get; set; } = "";
    }

    /// <summary>
    /// Per-monitor display configuration snapshot (resolution, refresh rate, etc.)
    /// </summary>
    public class DisplayMonitorConfig
    {
        public string DeviceName { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshRate { get; set; }
        public int BitsPerPel { get; set; }
        public int Orientation { get; set; }
        public int PositionX { get; set; }
        public int PositionY { get; set; }
    }

    /// <summary>
    /// Internal metadata written to nvidia-config/meta.json
    /// </summary>
    public class NvidiaMeta
    {
        public DateTime SavedAt { get; set; }
        public List<string> FilesCopied { get; set; } = new();
        public List<DisplayMonitorConfig> DisplayConfigs { get; set; } = new();
    }
}
