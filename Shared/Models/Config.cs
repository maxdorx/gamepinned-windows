using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// Main configuration for GamePinned
    /// Supports multiple platforms (Steam, Epic, Riot, etc.)
    /// </summary>
    public class Config
    {
        // Serializes all Config.Save() and Config.Load() file operations.
        // Multiple service threads (OnNewGameDetected, CloudSync, PushConfig handler,
        // DetectPlatforms) all write config.json concurrently. Without this lock a
        // torn write produces invalid JSON — Load() returns new Config() (empty), which
        // replaces _config in-memory and breaks all future protection decisions until restart.
        private static readonly object _fileLock = new object();

        /// <summary>
        /// Config schema version. Reserved for future migration logic.
        /// v3 = current multi-platform format (Platforms array). v2 and earlier are no longer supported (Critical #11, #17).
        /// </summary>
        public int SchemaVersion { get; set; } = 4;

        /// <summary>
        /// Hardware fingerprint ID for license binding
        /// </summary>
        public string HardwareID { get; set; } = "";

        /// <summary>
        /// License key (Base64 encoded signed blob — for local HMAC validation)
        /// </summary>
        public string LicenseKey { get; set; } = "";

        /// <summary>
        /// Original raw license key (GP-XXXXXXXX) — used for server-side CloudSync lookups.
        /// Populated on activation. Empty on pre-existing installs (CloudSync skips gracefully).
        /// </summary>
        public string RawLicenseKey { get; set; } = "";

        /// <summary>
        /// Windows usernames to block from uninstalling games
        /// </summary>
        public List<string> BlockedUsers { get; set; } = new();

        /// <summary>
        /// Platform-specific configurations
        /// </summary>
        public List<PlatformConfig> Platforms { get; set; } = new();

        /// <summary>
        /// Maintenance mode - when true, protection is disabled
        /// </summary>
        public bool MaintenanceMode { get; set; } = false;

        /// <summary>
        /// Auto-update enabled
        /// </summary>
        public bool AutoUpdateEnabled { get; set; } = true;

        /// <summary>
        /// SHA256 hash of the admin password for GUI exit protection
        /// </summary>
        public string AdminPasswordHash { get; set; } = "";

        /// <summary>
        /// If true (default), blocks all members of the Windows Users group (S-1-5-32-545) when BlockedUsers list is empty.
        /// This includes any admin accounts that are also members of that group.
        /// If false, unblocks everyone when list is empty.
        /// Note: the field name says "NonAdmin" for historical reasons — the actual target is group membership, not admin status.
        /// </summary>
        public bool BlockAllNonAdminByDefault { get; set; } = true;

        /// <summary>
        /// Override update API URL for dev/testing. Empty = use production URL.
        /// Set to "https://api.example.com/version/dev" to test auto-update without affecting live clients.
        /// </summary>
        public string DevUpdateUrl { get; set; } = "";

        /// <summary>
        /// When true (default), service restores saved NVIDIA DRS profile on every startup.
        /// When false, the saved profile is kept but restore must be triggered manually from the GUI.
        /// </summary>
        public bool NvidiaAutoRestore { get; set; } = true;

        /// <summary>
        /// The AssemblyInformationalVersion of the last GamePinned build that completed
        /// PinAgent.Start() on this machine. Compared against the currently running version
        /// on every startup (see Critical #59); a mismatch means a reinstall or update just
        /// happened, and triggers PlatformManager.ResetAllToDefault before ApplyProtection
        /// re-applies rules. That lifecycle cleanup removes stale GamePinned-style delete-deny
        /// residue while preserving launcher/vendor allow ACEs. Empty on pre-existing installs,
        /// treated as "different" so the cleanup fires once on the first startup after this
        /// field was introduced.
        /// </summary>
        public string LastKnownVersion { get; set; } = "";

        /// <summary>
        /// Save configuration to JSON file
        /// </summary>
        public void Save(string path)
        {
            lock (_fileLock)
            {
            SaveInternal(path);
            }
        }

        private void SaveInternal(string path)
        {
            var lines = new List<string>
            {
                "{",
                $"  \"SchemaVersion\": {SchemaVersion},",
                $"  \"HardwareID\": \"{EscapeJson(HardwareID)}\",",
                $"  \"LicenseKey\": \"{EscapeJson(LicenseKey)}\",",
                $"  \"RawLicenseKey\": \"{EscapeJson(RawLicenseKey)}\",",
                $"  \"MaintenanceMode\": {(MaintenanceMode ? "true" : "false")},",
                $"  \"AutoUpdateEnabled\": {(AutoUpdateEnabled ? "true" : "false")},",
                $"  \"AdminPasswordHash\": \"{EscapeJson(AdminPasswordHash)}\",",
                $"  \"BlockAllNonAdminByDefault\": {(BlockAllNonAdminByDefault ? "true" : "false")},",
                $"  \"DevUpdateUrl\": \"{EscapeJson(DevUpdateUrl)}\",",
                $"  \"NvidiaAutoRestore\": {(NvidiaAutoRestore ? "true" : "false")},",
                $"  \"LastKnownVersion\": \"{EscapeJson(LastKnownVersion)}\","
            };

            // BlockedUsers array
            lines.Add("  \"BlockedUsers\": [");
            for (int i = 0; i < BlockedUsers.Count; i++)
                lines.Add($"    \"{EscapeJson(BlockedUsers[i])}\"{(i < BlockedUsers.Count - 1 ? "," : "")}");
            lines.Add("  ],");

            // Platforms array
            lines.Add("  \"Platforms\": [");
            for (int p = 0; p < Platforms.Count; p++)
            {
                var platform = Platforms[p];
                lines.Add("    {");
                lines.Add($"      \"PlatformId\": \"{EscapeJson(platform.PlatformId)}\",");
                lines.Add($"      \"Enabled\": {(platform.Enabled ? "true" : "false")},");

                // LibraryPaths
                lines.Add("      \"LibraryPaths\": [");
                for (int i = 0; i < platform.LibraryPaths.Count; i++)
                {
                    lines.Add($"        \"{EscapeJson(platform.LibraryPaths[i])}\"{(i < platform.LibraryPaths.Count - 1 ? "," : "")}");
                }
                lines.Add("      ],");

                // ExcludedGames
                lines.Add("      \"ExcludedGames\": [");
                for (int i = 0; i < platform.ExcludedGames.Count; i++)
                    lines.Add($"        \"{EscapeJson(platform.ExcludedGames[i])}\"{(i < platform.ExcludedGames.Count - 1 ? "," : "")}");
                lines.Add("      ],");

                // ProtectedGames (no trailing comma on last property)
                lines.Add("      \"ProtectedGames\": [");
                for (int i = 0; i < platform.ProtectedGames.Count; i++)
                    lines.Add($"        \"{EscapeJson(platform.ProtectedGames[i])}\"{(i < platform.ProtectedGames.Count - 1 ? "," : "")}");
                lines.Add("      ]");

                lines.Add($"    }}{(p < Platforms.Count - 1 ? "," : "")}");
            }
            lines.Add("  ]");
            lines.Add("}");

            // Atomic write: write to .tmp first, then rename.
            // File.WriteAllText truncates to 0 bytes before writing — if the GUI reads config
            // during that window it gets empty JSON, AdminPasswordHash returns "" and triggers
            // the "set new password" setup dialog. NTFS rename is atomic: readers always see
            // either the old complete file or the new one, never 0 bytes.
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, string.Join(Environment.NewLine, lines));
            File.Move(tempPath, path, overwrite: true);
        }

        /// <summary>
        /// Load configuration from JSON file (v3 format only — see Critical #11, #17).
        /// </summary>
        public static Config Load(string path)
        {
            if (!File.Exists(path)) return new Config();

            lock (_fileLock)
            {
            return LoadInternal(path);
            }
        }

        private static Config LoadInternal(string path)
        {
            if (!File.Exists(path)) return new Config();

            try
            {
                var json = File.ReadAllText(path);
                var config = new Config
                {
                    SchemaVersion = ParseInt(json, "SchemaVersion", defaultValue: 4),
                    HardwareID = ParseString(json, "HardwareID"),
                    LicenseKey = ParseString(json, "LicenseKey"),
                    RawLicenseKey = ParseString(json, "RawLicenseKey"),
                    BlockedUsers = ParseArray(json, "BlockedUsers"),
                    MaintenanceMode = ParseBool(json, "MaintenanceMode"),
                    AutoUpdateEnabled = ParseBool(json, "AutoUpdateEnabled", defaultValue: true),
                    AdminPasswordHash = ParseString(json, "AdminPasswordHash"),
                    BlockAllNonAdminByDefault = ParseBool(json, "BlockAllNonAdminByDefault", defaultValue: true),
                    DevUpdateUrl = ParseString(json, "DevUpdateUrl"),
                    NvidiaAutoRestore = ParseBool(json, "NvidiaAutoRestore", defaultValue: true),
                    LastKnownVersion = ParseString(json, "LastKnownVersion")
                };

                config.Platforms = ParsePlatforms(json);
                return config;
            }
            catch
            {
                return new Config();
            }
        }

        /// <summary>
        /// Get platform config by ID, or create if doesn't exist
        /// </summary>
        public PlatformConfig GetPlatform(string platformId)
        {
            var platform = Platforms.FirstOrDefault(p => p.PlatformId == platformId);
            if (platform == null)
            {
                platform = new PlatformConfig { PlatformId = platformId };
                Platforms.Add(platform);
            }
            return platform;
        }

        #region JSON Helpers (Hand-rolled, no dependencies)

        /// <summary>
        /// Escapes a string for safe embedding in a JSON double-quoted value.
        /// Handles backslashes and double quotes — the two characters that break the hand-rolled parser.
        /// </summary>
        static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string ParseString(string json, string key)
        {
            var idx = json.IndexOf($"\"{key}\"");
            if (idx < 0) return "";
            var start = json.IndexOf(':', idx) + 1;
            var valueStart = json.IndexOf('"', start) + 1;
            if (valueStart <= 0) return "";
            // Walk forward to find the closing quote, skipping escaped quotes
            var pos = valueStart;
            while (pos < json.Length)
            {
                if (json[pos] == '\\') { pos += 2; continue; }
                if (json[pos] == '"') break;
                pos++;
            }
            if (pos >= json.Length) return "";
            var value = json.Substring(valueStart, pos - valueStart);
            // Unescape \" sequences
            value = value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            // Strip stray control characters (\r, \n, etc.) that can sneak in via
            // concurrent writes or file encoding issues — these break base64 decoding
            return value.Trim('\r', '\n', '\t', ' ');
        }

        static int ParseInt(string json, string key, int defaultValue = 0)
        {
            var idx = json.IndexOf($"\"{key}\"");
            if (idx < 0) return defaultValue;
            var start = json.IndexOf(':', idx) + 1;
            var end = json.IndexOfAny(new[] { ',', '}', '\n' }, start);
            if (end < 0) return defaultValue;
            var raw = json.Substring(start, end - start).Trim();
            return int.TryParse(raw, out var val) ? val : defaultValue;
        }

        static bool ParseBool(string json, string key, bool defaultValue = false)
        {
            var idx = json.IndexOf($"\"{key}\"");
            if (idx < 0) return defaultValue;
            var start = json.IndexOf(':', idx) + 1;
            var valueStart = json.IndexOfAny(new[] { 't', 'f' }, start);
            if (valueStart < 0 || valueStart + 4 > json.Length) return defaultValue;
            return json.Substring(valueStart, 4) == "true";
        }

        static List<string> ParseArray(string json, string key)
        {
            var result = new List<string>();
            var idx = json.IndexOf($"\"{key}\"");
            if (idx < 0) return result;
            var start = json.IndexOf('[', idx);
            var end = json.IndexOf(']', start);
            if (start < 0 || end < 0) return result;
            var parts = json.Substring(start + 1, end - start - 1).Split('"');
            for (int i = 1; i < parts.Length; i += 2)
            {
                var val = parts[i].Replace("\\\\", "\\").Trim();
                if (val.Length > 0) result.Add(val);
            }
            return result;
        }

        static List<PlatformConfig> ParsePlatforms(string json)
        {
            var result = new List<PlatformConfig>();
            var idx = json.IndexOf("\"Platforms\"");
            if (idx < 0) return result;

            var arrayStart = json.IndexOf('[', idx);
            var arrayEnd = FindMatchingBracket(json, arrayStart, '[', ']');
            if (arrayStart < 0 || arrayEnd < 0) return result;

            // Find all platform objects in array
            var pos = arrayStart + 1;
            while (pos < arrayEnd)
            {
                var objStart = json.IndexOf('{', pos);
                if (objStart < 0 || objStart > arrayEnd) break;

                var objEnd = FindMatchingBracket(json, objStart, '{', '}');
                if (objEnd < 0 || objEnd > arrayEnd) break;

                var platformJson = json.Substring(objStart, objEnd - objStart + 1);
                var platform = new PlatformConfig
                {
                    PlatformId = ParseString(platformJson, "PlatformId"),
                    Enabled = ParseBool(platformJson, "Enabled", true),
                    LibraryPaths = ParseArray(platformJson, "LibraryPaths"),
                    ExcludedGames = ParseArray(platformJson, "ExcludedGames"),
                    ProtectedGames = ParseArray(platformJson, "ProtectedGames")
                };
                result.Add(platform);

                pos = objEnd + 1;
            }

            return result;
        }

        static int FindMatchingBracket(string json, int start, char open, char close)
        {
            int depth = 1;
            for (int i = start + 1; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                if (json[i] == close) depth--;
                if (depth == 0) return i;
            }
            return -1;
        }

        #endregion
    }
}
