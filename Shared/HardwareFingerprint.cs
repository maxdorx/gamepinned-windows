using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GamePinned.Shared
{
    /// <summary>
    /// Generates a unique hardware fingerprint for license binding
    /// Combines: CPU ID + Motherboard Serial + Disk Serial
    /// Uses PowerShell Get-CimInstance (wmic is deprecated on modern Windows)
    /// </summary>
    public static class HardwareFingerprint
    {
        /// <summary>
        /// Generate hardware fingerprint (SHA256 hash of hardware components)
        /// </summary>
        public static string? Generate()
        {
            try
            {
                // Fetch all values in one PowerShell call to save time (spawning processes is slow)
                var combinedCim = GetCombinedCimValues();
                if (string.IsNullOrEmpty(combinedCim)) return null;

                var parts = combinedCim.Split('|');
                if (parts.Length < 3) return null;

                // Hash each component individually for "2-out-of-3" matching
                var cpuHash = ComputeSHA256(parts[0]);
                var mbHash = ComputeSHA256(parts[1]);
                var diskHash = ComputeSHA256(parts[2]);

                return $"{cpuHash}|{mbHash}|{diskHash}";
            }
            catch { return null; }
        }

        /// <summary>
        /// Get multiple CIM values in a single PowerShell execution
        /// </summary>
        static string? GetCombinedCimValues()
        {
            try
            {
                // Combine into one script block that outputs piped strings
                var script = "(Get-CimInstance Win32_Processor).ProcessorId + '|' + (Get-CimInstance Win32_BaseBoard).SerialNumber + '|' + (Get-CimInstance Win32_DiskDrive | Sort-Object DeviceID | Select-Object -First 1).SerialNumber";
                
                var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,  // Must be drained before WaitForExit to prevent deadlock on full output buffers
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.StandardError.ReadToEnd(); // Drain stderr — prevents pipe buffer deadlock if PowerShell emits errors
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(); } catch { }
                    return null;
                }

                return output;
            }
            catch { return null; }
        }

        /// <summary>
        /// Compute SHA256 hash of input string
        /// </summary>
        static string ComputeSHA256(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
    }
}
