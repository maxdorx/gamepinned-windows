using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Timers;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Timer = System.Timers.Timer;
using GamePinned.Service.Core;
using GamePinned.Shared;
using GamePinned.Shared.Models;

namespace GamePinned.Service
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    class Program
    {
        static void Main(string[] args)
        {
            // Global crash handlers — registered before anything else so we never miss a fatal error
            // Writes directly to the debug log file since the agent/logger may not exist yet
            var crashLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePinned", "logs");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(crashLogDir);
                    var msg = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "unknown";
                    var crashLine1 = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [CRASH] Unhandled exception (fatal={e.IsTerminating}): {msg}";
                    File.AppendAllText(Path.Combine(crashLogDir, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                        PinAgent.EncryptDebugLine(crashLine1, crashLogDir) + "\n");
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(crashLogDir);
                    var crashLine2 = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [CRASH] Unobserved task exception: {e.Exception}";
                    File.AppendAllText(Path.Combine(crashLogDir, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                        PinAgent.EncryptDebugLine(crashLine2, crashLogDir) + "\n");
                    e.SetObserved(); // Prevent process termination
                }
                catch { }
            };

            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n  [ERROR] GamePinned must run as Administrator!");
                Console.ResetColor();
                Thread.Sleep(3000);
                return;
            }

            // Load cache as early as possible
            CacheManager.LoadCache();

            if (args.Length > 0 && args[0] == "--service") { ServiceBase.Run(new GPService()); return; }
            if (args.Length > 0 && args[0] == "--install-service") { SvcHelper.Install(); return; }
            if (args.Length > 0 && args[0] == "--uninstall-service") { SvcHelper.Uninstall(); return; }

            PrintBanner();

            var configPath = GetConfigPath();
            var config = Config.Load(configPath);
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Mode: Console (Debugging)");
            Console.WriteLine($"  Path: {configPath}\n");
            Console.ResetColor();

            // Hardware fingerprint check (anti-copy protection)
            var currentHwId = CacheManager.GetHardwareId();
            if (!string.IsNullOrEmpty(config.HardwareID))
            {
                if (!LicenseManager.IsHardwareMatch(currentHwId, config.HardwareID))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n  [SECURITY] Hardware mismatch detected!");
                    Console.ResetColor();
                    Thread.Sleep(5000);
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(currentHwId))
            {
                config.HardwareID = currentHwId;
                config.Save(configPath);
            }

            var agent = new PinAgent(config, configPath);

            // Register cleanup handlers
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; agent.Stop(); Environment.Exit(0); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => agent.Stop();

            // Start Pipe Server for IPC
            var pipeServer = new PipeServer(configPath, agent.Log, (cmd, p) =>
            {
                switch (cmd)
                {
                    case PipeCommand.ToggleMaintenance:
                        agent.ToggleMaintenance();
                        return Task.FromResult<string?>("TOGGLED");
                    case PipeCommand.RefreshProtection:
                        agent.ApplyProtection();
                        return Task.FromResult<string?>("REFRESHED");
                    case PipeCommand.ClearPlatformSession:
                        {
                            var platformId = p.Length > 2 ? p[2].Trim().ToLowerInvariant() : "";
                            agent.PlatformManager.ClearPlatformSession(platformId);
                            return Task.FromResult<string?>("SESSION_CLEARED");
                        }
                    default: return Task.FromResult<string?>(null);
                }
            }, () => {
                // CRITICAL: Reload config before applying protection
                // This ensures PushConfig changes are applied with updated config
                var newConfig = Config.Load(configPath);
                agent.UpdateConfig(newConfig);
            }, () => agent.GetAllGames(), agent.LogDebug, stopAction: () => { agent.Stop(); Environment.Exit(0); });
            pipeServer.Start();

            // Watch for config changes (Hot Reload) — same as service mode.
            // Auto-restart on Error: FileSystemWatcher silently dies on buffer overflow otherwise.
            // List holds all created watchers (including old ones replaced after restart) so GC
            // doesn't collect live watchers while a handler lambda is still attached to them.
            var configWatchers = new List<FileSystemWatcher>();
            void StartConsoleConfigWatcher(int attempt)
            {
                const int MaxAttempts = 10;
                if (attempt >= MaxAttempts) { agent.LogDebug($"[ConfigWatcher] Giving up after {attempt} attempts"); return; }
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath) ?? "", "config.json");
                    System.Timers.Timer? configDebounce = null;
                    watcher.Changed += (s, e) =>
                    {
                        // Debounce: reset the timer on each change; reload only after 2s of quiet
                        configDebounce?.Stop();
                        configDebounce?.Dispose();
                        var t = new System.Timers.Timer(2000) { AutoReset = false };
                        t.Elapsed += (_, _) =>
                        {
                            try
                            {
                                var newConfig = Config.Load(configPath);
                                agent.UpdateConfig(newConfig);
                                agent.LogDebug("Config reloaded.");
                            }
                            catch (Exception ex) { agent.Log($"Config reload failed: {ex.Message}"); agent.LogDebug($"[Config] Reload exception: {ex}"); }
                        };
                        configDebounce = t;
                        t.Start();
                    };
                    var current = watcher;
                    watcher.Error += (s, e) =>
                    {
                        agent.LogDebug($"[ConfigWatcher] Error: {e.GetException().Message} — scheduling restart");
                        try { current.EnableRaisingEvents = false; current.Dispose(); } catch { }
                        var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
                        System.Threading.Tasks.Task.Delay(backoffMs).ContinueWith(_ => StartConsoleConfigWatcher(attempt + 1));
                    };
                    watcher.EnableRaisingEvents = true;
                    configWatchers.Add(watcher);
                    if (attempt > 0) agent.LogDebug($"[ConfigWatcher] Restarted (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    agent.LogDebug($"[ConfigWatcher] Start failed: {ex.Message}");
                    try { watcher?.Dispose(); } catch { }
                    var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
                    System.Threading.Tasks.Task.Delay(backoffMs).ContinueWith(_ => StartConsoleConfigWatcher(attempt + 1));
                }
            }
            StartConsoleConfigWatcher(0);

            // Hook protection progress — stream per-game progress to GUI loading dialog
            agent.PlatformManager.OnProtectionProgress += (n, total, gameName, isLocking) =>
            {
                var verb = isLocking ? "Locking" : "Unlocking";
                pipeServer.SendNotification(ServiceNotificationType.ProtectionProgress,
                    $"{verb}: {gameName} ({n}/{total})", "");
            };

            // Hook launcher restart events — same as service mode
            agent.PlatformManager.OnLauncherRestartRequested += (platformId, path) =>
            {
                if (platformId == "steam")
                {
                    pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_STEAM", path);
                }
                else if (platformId == "epic")
                {
                    pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_EPIC", path);
                }
                else if (platformId == "riot")
                {
                    pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_RIOT", path);
                }
            };

            // Forward UninstallBlocked events to the GUI as a tray notification.
            // Title carries the platform+game so the GUI can format a friendly message
            // without needing a second IPC roundtrip.
            agent.OnUninstallBlocked += (platformId, gameName) =>
            {
                pipeServer.SendNotification(ServiceNotificationType.UninstallBlocked, platformId, gameName);
            };

            agent.Start();

            Console.WriteLine("\n  [DEBUG] Service is running. Press Ctrl+C to stop.");

            // Keep app alive without an interactive menu
            new ManualResetEvent(false).WaitOne();
        }

        static string GetConfigPath()
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePinned");
            Directory.CreateDirectory(d);
            var path = Path.Combine(d, "config.json");

            // Secure the config file (ACLs) - System & Admin ONLY
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "{}");

                // Create new security settings with strict rules
                var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(admin, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));

                new FileInfo(path).SetAccessControl(security);
            }
            catch { /* Ignore if fails (e.g. not admin) */ }

            return path;
        }

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(@"
   ____                      ____  _                      _
  / ___| __ _ _ __ ___   ___|  _ \(_)_ __  _ __   ___  __| |
 | |  _ / _` | '_ ` _ \ / _ \ |_) | | '_ \| '_ \ / _ \/ _` |
 | |_| | (_| | | | | | |  __/  __/| | | | | | | |  __/ (_| |
  \____|\__,_|_| |_| |_|\___|_|   |_|_| |_|_| |_|\___|\__,_|

  GamePinned - jail for games
");
            Console.ResetColor();
        }
    }

    // ====== WINDOWS SERVICE ======
    public class GPService : ServiceBase
    {
        PinAgent? _agent;
        PipeServer? _pipeServer;
        FileSystemWatcher? _configWatcher;
        readonly List<FileSystemWatcher> _configWatcherHistory = new();
        volatile bool _stoppingConfigWatcher = false;

        public GPService() { ServiceName = "GamePinned"; }
        protected override void OnStart(string[] args)
        {
            // Load cache on service start
            CacheManager.LoadCache();

            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePinned", "config.json");
            var config = Config.Load(configPath);

            // Hardware fingerprint check (anti-copy protection)
            var currentHwId = CacheManager.GetHardwareId();
            if (!string.IsNullOrEmpty(config.HardwareID) && !LicenseManager.IsHardwareMatch(currentHwId, config.HardwareID))
            {
                // Hardware mismatch - log but DON'T crash service (this causes Error 1064)
            }
            else if (string.IsNullOrEmpty(config.HardwareID) && !string.IsNullOrEmpty(currentHwId))
            {
                // First run - save hardware ID
                config.HardwareID = currentHwId;
                config.Save(configPath);
            }

            // Init Logger (Console + File)
            Action<string> logger = msg => 
            {
                try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(configPath) ?? "", "logs", $"gamepinned_{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
            };

            // Start Pipe Server for IPC
            _pipeServer = new PipeServer(configPath, logger, (cmd, p) =>
            {
                if (_agent == null) return Task.FromResult<string?>("AGENT_NOT_READY");
                switch (cmd)
                {
                    case PipeCommand.ToggleMaintenance:
                        _agent.ToggleMaintenance();
                        return Task.FromResult<string?>("TOGGLED");
                    case PipeCommand.RefreshProtection:
                        _agent.ApplyProtection();
                        return Task.FromResult<string?>("REFRESHED");
                    case PipeCommand.ClearPlatformSession:
                        {
                            var platformId = p.Length > 2 ? p[2].Trim().ToLowerInvariant() : "";
                            _agent.PlatformManager.ClearPlatformSession(platformId);
                            return Task.FromResult<string?>("SESSION_CLEARED");
                        }
                    default: return Task.FromResult<string?>(null);
                }
            }, () => {
                // CRITICAL: Reload config before applying protection
                // This ensures PushConfig changes are applied with updated config
                var newConfig = Config.Load(configPath);
                _agent?.UpdateConfig(newConfig);
            }, () => _agent?.GetAllGames() ?? new List<GameInfo>(), msg => _agent?.LogDebug(msg), stopAction: () => this.Stop());
            _pipeServer.Start();

            _agent = new PinAgent(config, configPath);

            // Watch for config changes (Hot Reload) with auto-restart on Error.
            // Without auto-restart, a buffer overflow on busy systems silently stops detection
            // and the GUI's config pushes get ignored (the pipe path still works via PushConfig,
            // but external edits would be invisible).
            _stoppingConfigWatcher = false;
            StartServiceConfigWatcher(configPath, logger, 0);

            // Hook protection progress — stream per-game progress to GUI loading dialog
            _agent.PlatformManager.OnProtectionProgress += (n, total, gameName, isLocking) =>
            {
                var verb = isLocking ? "Locking" : "Unlocking";
                _pipeServer.SendNotification(ServiceNotificationType.ProtectionProgress,
                    $"{verb}: {gameName} ({n}/{total})", "");
            };

            // Hook Restart Request
            _agent.PlatformManager.OnLauncherRestartRequested += (platformId, path) =>
            {
                if (platformId == "steam")
                {
                    _pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_STEAM", path);
                }
                else if (platformId == "epic")
                {
                    _pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_EPIC", path);
                }
                else if (platformId == "riot")
                {
                    _pipeServer.SendNotification(ServiceNotificationType.Info, "RESTART_RIOT", path);
                }
            };

            // Forward UninstallBlocked events to the GUI (Critical #14: console mode must
            // match service mode). Title carries platformId, message carries game name.
            _agent.OnUninstallBlocked += (platformId, gameName) =>
            {
                _pipeServer.SendNotification(ServiceNotificationType.UninstallBlocked, platformId, gameName);
            };

            // CRITICAL: Run Start() on a background thread so OnStart() returns immediately.
            // OnStart() has ~30s to return to SCM. ApplyProtection() + CloudSync() can take
            // several minutes on customer PCs (many games, HDD, many user profiles, slow network).
            // If OnStart() blocks > 30s, SCM marks service as stuck at "Starting" while timers
            // (health check, icacls) are already running — causing the 300 icacls accumulation bug.
            var agentDebugLogPath = Path.Combine(Path.GetDirectoryName(configPath) ?? "", "logs");
            new Thread(() =>
            {
                try { _agent.Start(); }
                catch (Exception ex)
                {
                    try
                    {
                        Directory.CreateDirectory(agentDebugLogPath);
                        var crashLine3 = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [CRASH] Agent.Start() threw: {ex}";
                        File.AppendAllText(Path.Combine(agentDebugLogPath, $"debug_{DateTime.Now:yyyyMMdd}.log"),
                            PinAgent.EncryptDebugLine(crashLine3, agentDebugLogPath) + "\n");
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Start the config file watcher with auto-restart on Error.
        /// Without auto-restart, FileSystemWatcher buffer overflow silently stops hot-reload.
        /// Exponential backoff (2s, 4s, 8s, ..., 60s cap), max 10 attempts before giving up.
        /// </summary>
        void StartServiceConfigWatcher(string configPath, Action<string> logger, int attempt)
        {
            if (_stoppingConfigWatcher) return;
            const int MaxAttempts = 10;
            if (attempt >= MaxAttempts)
            {
                _agent?.LogDebug($"[ConfigWatcher] Giving up after {attempt} attempts");
                return;
            }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath) ?? "", "config.json");
                System.Timers.Timer? configDebounce = null;
                watcher.Changed += (s, e) =>
                {
                    configDebounce?.Stop();
                    configDebounce?.Dispose();
                    var t = new System.Timers.Timer(2000) { AutoReset = false };
                    t.Elapsed += (_, _) =>
                    {
                        try
                        {
                            var newConfig = Config.Load(configPath);
                            _agent?.UpdateConfig(newConfig);
                            _agent?.LogDebug("Config reloaded.");
                        }
                        catch (Exception ex) { logger($"Config reload failed: {ex.Message}"); _agent?.LogDebug($"[Config] Reload exception: {ex}"); }
                    };
                    configDebounce = t;
                    t.Start();
                };
                var current = watcher;
                watcher.Error += (s, e) =>
                {
                    _agent?.LogDebug($"[ConfigWatcher] Error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingConfigWatcher) return;
                    try { current.EnableRaisingEvents = false; current.Dispose(); } catch { }
                    var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
                    System.Threading.Tasks.Task.Delay(backoffMs).ContinueWith(_ => StartServiceConfigWatcher(configPath, logger, attempt + 1));
                };
                watcher.EnableRaisingEvents = true;
                _configWatcher = watcher;
                _configWatcherHistory.Add(watcher);
                if (attempt > 0) _agent?.LogDebug($"[ConfigWatcher] Restarted (attempt {attempt})");
            }
            catch (Exception ex)
            {
                _agent?.LogDebug($"[ConfigWatcher] Start failed: {ex.Message}");
                try { watcher?.Dispose(); } catch { }
                var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
                System.Threading.Tasks.Task.Delay(backoffMs).ContinueWith(_ => StartServiceConfigWatcher(configPath, logger, attempt + 1));
            }
        }

        protected override void OnStop()
        {
            _stoppingConfigWatcher = true;
            foreach (var w in _configWatcherHistory)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _configWatcherHistory.Clear();
            _agent?.Stop();
            _pipeServer?.Dispose();
        }
    }

    public static class SvcHelper
    {
        public static void Install()
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Could not get process filename");
            RunCmd("sc", $"create GamePinned binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"GamePinned Service\"");
            RunCmd("sc", $"config GamePinned binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"GamePinned Service\"");
            RunCmd("sc", "description GamePinned \"Prevents unauthorized game uninstallation on shared computers\"");
            RunCmd("sc", "failure GamePinned reset= 60 actions= restart/5000/restart/10000/restart/30000");

            // Set service security descriptor - Only SYSTEM and Administrators can completely control the service
            // Authenticated Users (AU) are granted read, query status, and START (RP) rights, but NOT STOP (WP).
            RunCmd("sc", "sdset GamePinned D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;AU)");

            RunCmd("sc", "start GamePinned");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n  [OK] GamePinned service installed and started.");
            Console.WriteLine("  The service will auto-restart on failure.");
            Console.WriteLine("  Standard users can query status and start the service, but cannot stop it.");
            Console.ResetColor();
        }

        public static void Uninstall()
        {
            // Plain-text uninstall trace written to the user's temp folder, NOT to
            // C:\ProgramData\GamePinned (the uninstaller deletes that folder right after this
            // runs). This is the only diagnostic that exists for the uninstall path: the console
            // is invisible (installer runs --uninstall-service with runhidden) and the encrypted
            // debug log directory does not survive uninstall. Contains only operational steps,
            // game names/paths, and icacls exit codes; never license or hardware data.
            var uninstallLogPath = Path.Combine(Path.GetTempPath(), "gamepinned_uninstall.log");
            Action<string> ulog = msg =>
            {
                try { File.AppendAllText(uninstallLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
            };
            var uninstVersion = typeof(PinAgent).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
            ulog($"=== Uninstall started (v{uninstVersion}, pid {Environment.ProcessId}) ===");

            RunCmd("sc", "stop GamePinned");
            // Wait up to 10s for graceful stop. Old 1.0.x OnStop can take minutes on large game
            // libraries; the self-excluding kill below is the safety net for those.
            Thread.Sleep(10000);
            ulog("sc stop issued, 10s grace elapsed");

            // Kill any OTHER GamePinnedService.exe instance still running (an old service
            // process that ignored sc stop), to release the exe file lock before Inno copies
            // or deletes files.
            //
            // CRITICAL: this method runs INSIDE GamePinnedService.exe (--uninstall-service).
            // The previous implementation, RunCmd("taskkill", "/F /IM GamePinnedService.exe"),
            // matched this very process by image name and terminated it at this exact line, so
            // the permission reset and `sc delete` below NEVER executed. Every uninstall up to
            // and including 2.0.4 silently left deny rules on game folders because of this
            // (Critical #16, #59). Kill by PID and exclude ourselves; never kill by image name
            // from inside this process.
            var myPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("GamePinnedService"))
            {
                try
                {
                    if (p.Id != myPid)
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                        ulog($"Killed leftover service process pid {p.Id}");
                    }
                }
                catch { /* exited between snapshot and Kill */ }
                finally { try { p.Dispose(); } catch { } }
            }

            // Remove GamePinned-style delete-deny residue directly.
            // Cannot rely on OnStop, the service may be killed before this finishes.
            // Uses ResetAllToDefault rather than routine RemoveProtection because it discovers
            // stale delete-deny identities recursively. It must preserve launcher/vendor allow
            // ACEs, inheritance, and ownership; never replace this with icacls /reset. See
            // Critical #59.
            try
            {
                Console.WriteLine("\n  Removing GamePinned protection permissions...");
                var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GamePinned", "config.json");
                // Run the reset even without a config: DetectPlatforms() re-populates library
                // paths from scratch, so installed games are still found and cleaned.
                var config = File.Exists(configPath) ? Config.Load(configPath) : new Config();
                ulog(File.Exists(configPath) ? "config.json loaded" : "config.json not found, using defaults (reset still runs)");
                var backupPath = Path.Combine(Path.GetDirectoryName(configPath) ?? "", "backups");
                var platformManager = new PlatformManager(config, backupPath,
                    msg => { Console.WriteLine($"  {msg}"); ulog(msg); }, ulog);
                platformManager.DetectPlatforms();
                platformManager.ResetAllToDefault("uninstall");
                Console.WriteLine("  GamePinned protection permissions removed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Could not reset permissions: {ex.Message}");
                ulog($"RESET FAILED: {ex}");
            }

            RunCmd("sc", "delete GamePinned");
            ulog("sc delete issued");
            ulog("=== Uninstall finished ===");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n  [OK] GamePinned uninstalled. Game protection permissions removed.");
            Console.ResetColor();
        }

        static void RunCmd(string f, string a)
        {
            using var proc = Process.Start(new ProcessStartInfo(f, a) { UseShellExecute = false, CreateNoWindow = true });
            if (proc != null && !proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
            }
        }
    }


    // ====== CORE AGENT ======
    public class PinAgent
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private const int SyncIntervalMs      = 24 * 60 * 60 * 1000; // 24 hours
        private const int AutoUpdateIntervalMs = 24 * 60 * 60 * 1000; // 24 hours
        private const int LogRetentionDays    = 7;

        // Debug log encryption — RSA-2048 hybrid (AES session key per run, RSA-encrypted header).
        // Configure your own public key here. Keep the matching private key off client machines.
        // Even extracting this public key from the binary cannot decrypt any logs.
        private static readonly string _debugLogPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
REPLACE_WITH_YOUR_PUBLIC_KEY
-----END PUBLIC KEY-----";
        private static readonly Dictionary<string, byte[]> _debugSessionKeys = new();
        private static readonly object _debugSessionKeyLock = new object();

        /// <summary>
        /// Encrypts a debug log line. Each service run writes a SESSION_KEY header (RSA-encrypted AES key)
        /// then encrypts all lines with AES-256-CBC. Multiple headers per file are fine — decryptor reads
        /// them sequentially and switches keys on each one.
        /// </summary>
        internal static string EncryptDebugLine(string line, string logDir)
        {
            try
            {
                var date = DateTime.Now.ToString("yyyyMMdd");
                byte[] sessionKey;

                lock (_debugSessionKeyLock)
                {
                    if (!_debugSessionKeys.TryGetValue(date, out sessionKey!))
                    {
                        var newKey = RandomNumberGenerator.GetBytes(32);
                        try
                        {
                            using var rsa = RSA.Create();
                            rsa.ImportFromPem(_debugLogPublicKeyPem);
                            var encryptedKey = rsa.Encrypt(newKey, RSAEncryptionPadding.OaepSHA256);
                            Directory.CreateDirectory(logDir);
                            File.AppendAllText(
                                Path.Combine(logDir, $"debug_{date}.log"),
                                "SESSION_KEY:" + Convert.ToBase64String(encryptedKey) + "\n");
                            // Only cache the key after the header is successfully written.
                            // If the write fails, the next call retries — no orphaned encrypted lines.
                            _debugSessionKeys[date] = newKey;
                            sessionKey = newKey;
                        }
                        catch { return "[LOG_INIT_FAILED]"; }
                    }
                }

                using var aes = Aes.Create();
                aes.Key = sessionKey;
                aes.GenerateIV();
                using var encryptor = aes.CreateEncryptor();
                var plain = Encoding.UTF8.GetBytes(line);
                var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                var combined = new byte[16 + cipher.Length];
                aes.IV.CopyTo(combined, 0);
                cipher.CopyTo(combined, 16);
                return Convert.ToBase64String(combined);
            }
            catch
            {
                return "[ENCRYPTION_FAILED]";
            }
        }
        Config _config;
        string _configPath, _backupPath, _logPath;
        PlatformManager _platformManager;
        Timer? _backupTimer, _healthTimer, _syncTimer, _updateTimer;
        bool _maint = false;
        NvidiaConfigManager _nvidiaManager = null!;
        private int _healthCheckRunning = 0;   // Reentrancy guard — prevents overlapping health checks on slow PCs
        private int _platformDiscoveryTickCount = 0;
        private int _applyProtectionRunning = 0; // Set while ApplyProtection() is executing; HealthCheck skips if set to avoid double icacls
        // Serializes concurrent ApplyProtection() calls (ToggleMaintenance + UpdateConfig config-watcher both
        // fire when maintenance mode is toggled — without this they spawn 40+ parallel icacls on the same HDD)
        private readonly SemaphoreSlim _applyProtectionLock = new SemaphoreSlim(1, 1);

        private bool? _lastHealthCheckLicenseValid; // Tracks last value so log only fires on change
        private readonly HashSet<string> _gamesBeingInstalled = new(); // Tracks games mid-install to suppress false BLOCKED + duplicate new-game events
        private readonly object _installLock = new object();
        // Tracks games explicitly removed from ProtectedGames by the admin (via UpdateConfig/PushConfig).
        // OnNewGameDetected skips auto-add for these games for 120s to prevent the
        // unlock → Riot-manifest-event → OnNewGameDetected → re-lock cycle (Critical #30 area).
        private readonly Dictionary<string, DateTime> _recentlyUnlockedGames = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _recentlyUnlockedLock = new object();

        public PlatformManager PlatformManager => _platformManager; // Expose for event hooks

        /// <summary>
        /// Fired when an uninstall attempt is blocked by GamePinned. Wired to a GUI
        /// notification so the user understands why Steam appeared to "crash" (we
        /// killed it intentionally) or why an Epic/Riot uninstall failed with a
        /// generic "file access error". Argument: "platformId|gameName" — e.g.
        /// "steam|Counter-Strike". Subscribers must dispatch UI work back to the
        /// GUI thread.
        /// </summary>
        public event Action<string, string>? OnUninstallBlocked;

        /// <summary>
        /// Get all detected games across all platforms (for GUI display)
        /// </summary>
        public List<GameInfo> GetAllGames()
        {
            return _platformManager.GetAllGames();
        }

        public PinAgent(Config config, string configPath)
        {
            _config = config;
            _maint = config.MaintenanceMode; // Sync internal flag with config
            _configPath = configPath;
            var pathDir = Path.GetDirectoryName(configPath);
            _backupPath = Path.Combine(pathDir ?? "", "backups");
            _logPath = Path.Combine(pathDir ?? "", "logs");
            Directory.CreateDirectory(_backupPath);
            Directory.CreateDirectory(_logPath);

            // Restrict logs directory to Admins + SYSTEM only — standard users cannot read debug logs
            try
            {
                var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var dirSecurity = new DirectorySecurity();
                dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                dirSecurity.AddAccessRule(new FileSystemAccessRule(admin, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                dirSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(_logPath).SetAccessControl(dirSecurity);
            }
            catch { }

            // Initialize platform manager
            _platformManager = new PlatformManager(config, _backupPath, Log, LogDebug);

            // Initialize NVIDIA config manager
            var nvidiaBackupPath = Path.Combine(pathDir ?? "", "nvidia-config");
            _nvidiaManager = new NvidiaConfigManager(nvidiaBackupPath, Log, LogDebug);

        }

        public void UpdateConfig(Config newConfig)
        {
            // Detect what actually changed to avoid expensive operations
            bool maintChanged = _maint != newConfig.MaintenanceMode;
            bool usersChanged = !_config.BlockedUsers.SequenceEqual(newConfig.BlockedUsers);
            bool blockDefaultChanged = _config.BlockAllNonAdminByDefault != newConfig.BlockAllNonAdminByDefault;
            bool gamesChanged = AnyGameProtectionChanged(_config, newConfig);

            bool protectionChanged = usersChanged || blockDefaultChanged || gamesChanged || maintChanged;

            // Track games explicitly removed from ProtectedGames so OnNewGameDetected won't
            // auto-add them back when Riot Client activity fires a manifest-created event
            // shortly after the deny rules are removed (the unlock → re-lock cycle).
            if (gamesChanged)
            {
                var now = DateTime.Now;
                lock (_recentlyUnlockedLock)
                {
                    foreach (var oldPlatform in _config.Platforms)
                    {
                        var newPlatform = newConfig.Platforms.FirstOrDefault(p => p.PlatformId == oldPlatform.PlatformId);
                        if (newPlatform == null) continue;
                        foreach (var gameName in oldPlatform.ProtectedGames)
                        {
                            if (!newPlatform.ProtectedGames.Contains(gameName, StringComparer.OrdinalIgnoreCase))
                            {
                                _recentlyUnlockedGames[$"{oldPlatform.PlatformId}|{gameName}"] = now;
                                LogDebug($"[UpdateConfig] Marked {gameName} ({oldPlatform.PlatformId}) as recently unlocked");
                            }
                        }
                    }
                }
            }

            _config = newConfig;
            _maint = newConfig.MaintenanceMode;
            _platformManager.UpdateConfig(newConfig);

            // Handle maintenance mode state changes
            if (_maint)
            {
                if (maintChanged) Log("MAINTENANCE MODE ENABLED - Removing locks...");
                // Wait for any in-progress ApplyProtection to finish before removing rules.
                // Without this, ApplyProtection (parallel icacls /T) can still be running on
                // thread A while RemoveProtection runs on thread B. ApplyProtection finishes
                // AFTER RemoveProtection and leaves some games locked despite maintenance mode.
                _applyProtectionLock.Wait();
                try
                {
                    _platformManager.RemoveProtection("maintenance-on");
                }
                finally
                {
                    _applyProtectionLock.Release();
                }
                return; // Don't apply protection or restart watchers in maintenance mode
            }
            else
            {
                if (maintChanged) Log("MAINTENANCE MODE DISABLED - Re-applying locks...");
            }

            // Only run expensive operations if protection settings changed
            // Safe changes: AdminPasswordHash, LicenseKey, HardwareID, AutoUpdateEnabled
            if (!protectionChanged)
            {
                LogDebug("Config updated (non-lock settings).");
                return; // Skip expensive operations for password/license changes
            }

            LogDebug("Applying new configuration...");
            ApplyProtection();

            // CRITICAL: Restart watchers when config changes (blocked users, protected/excluded games)
            _platformManager.StopWatchers();
            _platformManager.StartWatchers(OnUninstallDetected, OnNewGameDetected);

            Safe(() => _platformManager.BackupManifests());
        }

        /// <summary>
        /// Check if ExcludedGames or ProtectedGames changed on any platform
        /// </summary>
        private bool AnyGameProtectionChanged(Config oldCfg, Config newCfg)
        {
            foreach (var oldPlatform in oldCfg.Platforms)
            {
                var newPlatform = newCfg.Platforms.FirstOrDefault(p => p.PlatformId == oldPlatform.PlatformId);
                if (newPlatform == null) continue;

                if (!oldPlatform.ExcludedGames.SequenceEqual(newPlatform.ExcludedGames)) return true;
                if (!oldPlatform.ProtectedGames.SequenceEqual(newPlatform.ProtectedGames)) return true;
            }
            return false;
        }

        public void Start()
        {
            Log("Agent starting...");

            // Cleanup old log files (older than 7 days)
            CleanupOldLogs();

            // Detect and configure installed platforms (Steam, Epic, Riot)
            _platformManager.DetectPlatforms();
            _config.Save(_configPath);

            // Version-change lifecycle cleanup: if the running binary's version differs from the
            // last version that completed Start() on this machine, remove stale GamePinned-style
            // delete-deny residue BEFORE re-applying protection. This is a PERMANENT policy (fires
            // on every future update/reinstall, not a one-time migration) and must preserve
            // launcher/vendor allow ACEs, inheritance, and ownership.
            // Cheap on routine restarts/reboots: the version string only changes when a new
            // binary is actually installed, not on every service start. See Critical #59.
            var asmForVersionCheck = typeof(PinAgent).Assembly;
            var currentVersion = asmForVersionCheck.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                     ?.InformationalVersion?.Split('+')[0]
                                  ?? asmForVersionCheck.GetName().Version?.ToString()
                                  ?? "1.0.0";
            if (!string.Equals(_config.LastKnownVersion, currentVersion, StringComparison.Ordinal))
            {
                var previousVersion = string.IsNullOrEmpty(_config.LastKnownVersion) ? "(none)" : _config.LastKnownVersion;
                Log($"Version change detected: {previousVersion} -> {currentVersion}. Cleaning stale GamePinned protection permissions before re-applying protection...");
                Safe(() => _platformManager.ResetAllToDefault($"version change {previousVersion} -> {currentVersion}"));
                _config.LastKnownVersion = currentVersion;
                _config.Save(_configPath);
            }

            // Restore NVIDIA settings before user logs in (DRS files not yet locked by NVIDIA user-space processes)
            // Paid-tier feature: only runs for valid license + auto-restore enabled
            var nvidiaLicenseValid = LicenseManagerInternal.ValidateLicense(_config.LicenseKey, CacheManager.GetHardwareId() ?? "");
            if (nvidiaLicenseValid && _config.NvidiaAutoRestore)
            {
                bool nvidiaRestored = false;
                Safe(() => { nvidiaRestored = _nvidiaManager.RestoreConfigOnStartup(); });
                if (nvidiaRestored)
                    LogDebug("[NVIDIA] Restored saved configuration on startup.");
            }

            // Apply protection FIRST (this sets up icacls deny rules)
            // NOTE: SetDenyRule() now automatically removes duplicates before adding rules
            ApplyProtection();

            // CRITICAL: Backup manifests BEFORE starting watchers
            LogDebug("Creating initial manifest backups...");
            _platformManager.BackupManifests();
            Thread.Sleep(1000);  // Ensure backup completes - DO NOT REMOVE
            LogDebug("Initial backups complete.");

            // Now start watchers (safe because backups exist)
            _platformManager.StartWatchers(OnUninstallDetected, OnNewGameDetected);

            // Start timers
            _backupTimer = new Timer(10000);
            _backupTimer.Elapsed += (s, e) => Safe(() => _platformManager.BackupManifests());
            _backupTimer.Start();

            _healthTimer = new Timer(10000);
            _healthTimer.Elapsed += (s, e) => Safe(HealthCheck);
            _healthTimer.Start();

            // Sync with cloud on interval
            _syncTimer = new Timer(SyncIntervalMs);
            _syncTimer.Elapsed += (s, e) => Safe(CloudSync);
            _syncTimer.Start();

            // Initial sync on start
            Safe(CloudSync);

            // Auto-update: initial check after 30 minutes, then on interval
            var initialUpdateTimer = new Timer(1800000);
            initialUpdateTimer.Elapsed += (s, e) => { initialUpdateTimer.Stop(); initialUpdateTimer.Dispose(); if (_config.AutoUpdateEnabled) Safe(TrySilentUpdate); };
            initialUpdateTimer.Start();
            _updateTimer = new Timer(AutoUpdateIntervalMs);
            _updateTimer.Elapsed += (s, e) => { if (_config.AutoUpdateEnabled) Safe(TrySilentUpdate); };
            _updateTimer.Start();

            Log($"Running. {_config.Platforms.Count} platform(s) monitored.");
        }

        public void Stop()
        {
            // INTENTIONAL: deny rules are NOT removed on stop.
            // Rules are only removed in two cases:
            //   1. Admin explicitly unlocks a game from the GUI (PushConfig removes game from
            //      ProtectedGames → ApplyProtection → Pass 2 unlocks it)
            //   2. GamePinned is uninstalled (SvcHelper.Uninstall invokes lifecycle cleanup,
            //      which removes GamePinned-style delete-deny residue while preserving vendor ACLs)
            //
            // Every other stop path — GUI stop button, services.msc, task manager, sc stop,
            // crash, Windows shutdown, service restart — rules stay. The new service instance
            // re-applies the same rules on startup anyway, so there is no protection gap.
            //
            // This also means OnStop() completes in milliseconds (no icacls) regardless of
            // how many games are protected, which fixes the SCM timeout issue on large libraries.
            _backupTimer?.Stop();
            _healthTimer?.Stop();
            _syncTimer?.Stop();
            _updateTimer?.Stop();
            // releaseHandles: true — this is the ONLY full-shutdown stop path. Riot releases its
            // no-delete-share handles here. Other StopWatchers callers (UpdateConfig,
            // ToggleMaintenance) intentionally do NOT release handles (Critical #53 fix).
            _platformManager.StopWatchers(releaseHandles: true);

            Log("Agent stopped.");
        }

        void Safe(Action a) { try { a(); } catch (Exception ex) { Log($"ERR: {ex.Message}"); LogDebug($"[CRASH] Safe() caught: {ex}"); } }

        // ═══════════ PROTECTION ═══════════
        public void ApplyProtection(bool silent = false)
        {
            if (_maint) return;

            // Serialize concurrent callers — ToggleMaintenance and the config file watcher both
            // fire ApplyProtection when maintenance mode is toggled. On a machine with 20+ games
            // the concurrent Parallel.ForEach spawns 40+ icacls /T processes competing for the
            // same HDD, causing thrashing. The second caller waits and then runs with the latest
            // config state rather than both racing and leaving games in a half-locked state.
            _applyProtectionLock.Wait();
            try
            {
                // Re-check maintenance flag after acquiring lock — the first caller may have
                // been ToggleMaintenance enabling maintenance; by the time we get here it's on
                if (_maint) return;

                if (_config.BlockedUsers.Count == 0)
                {
                    if (!silent) Log("  No users listed — blocking all members of the Windows Users group by default.");
                }
                else
                {
                    if (!silent) Log($"  Blocking {_config.BlockedUsers.Count} user(s): {string.Join(", ", _config.BlockedUsers)}");
                }

                Interlocked.Exchange(ref _applyProtectionRunning, 1);
                try
                {
                    var licenseValid = LicenseManagerInternal.ValidateLicense(_config.LicenseKey, CacheManager.GetHardwareId());
                    LogDebug($"[License] ApplyProtection: licenseValid={licenseValid} silent={silent}");
                    _platformManager.ApplyProtection(licenseValid, silent);
                }
                finally
                {
                    Interlocked.Exchange(ref _applyProtectionRunning, 0);
                }
            }
            finally
            {
                _applyProtectionLock.Release();
            }
        }

        // ═══════════ EVENT HANDLERS ═══════════
        void OnUninstallDetected(GameInfo game)
        {
            try
            {
                if (_maint)
                {
                    Log($"[MAINT] Allowed uninstall: {game.GameName} ({game.PlatformId})");
                    return;
                }

                // Skip if this game is currently being installed (Riot/Epic file churn during install triggers watcher)
                var installKey = $"{game.PlatformId}|{game.GameName}";
                lock (_installLock)
                {
                    if (_gamesBeingInstalled.Contains(installKey))
                    {
                        LogDebug($"[Install] Ignoring watcher event for {game.GameName} — game is being installed");
                        return;
                    }
                }

                // Don't log here — the BLOCKED/ALLOWED line below is enough

                // CRITICAL: Check if this game is ACTUALLY PROTECTED
                // Don't block uninstall of games that aren't protected (exempt or free tier limited)
                // NOTE: Do NOT use GetAllGames() here — for Riot/Epic the manifest is already deleted
                // by the time this fires, so the game won't appear in GetAllGames(). Use config directly.
                var licenseValid = LicenseManagerInternal.ValidateLicense(_config.LicenseKey, CacheManager.GetHardwareId());
                LogDebug($"[License] OnUninstallDetected: licenseValid={licenseValid} game={game.GameName} platform={game.PlatformId}");

                // Build protected list directly from config (not from filesystem scan)
                var gamesToProtect = _config.Platforms
                    .SelectMany(p => p.ProtectedGames.Select(name => new { PlatformId = p.PlatformId, GameName = name }))
                    .Where(g => {
                        var pc = _config.GetPlatform(g.PlatformId);
                        return !pc.ExcludedGames.Contains(g.GameName, StringComparer.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (!licenseValid && gamesToProtect.Count > 1)
                    gamesToProtect = gamesToProtect.Take(1).ToList();

                // Check if THIS game is in the protected list
                bool isProtected = gamesToProtect.Any(g =>
                    g.GameName.Equals(game.GameName, StringComparison.OrdinalIgnoreCase) &&
                    g.PlatformId == game.PlatformId);

                if (!isProtected)
                {
                    Log($"[ALLOWED] {game.GameName} - not in lock list (unlocked or free tier limited)");
                    Safe(() => _platformManager.BackupManifests());
                    return;
                }

                // Steam-specific: distinguish update-manifest-rewrite from real uninstall.
                // Steam deletes and rewrites the .acf when applying a patch (no deny rule on .acf —
                // see Critical #3). Poll every 200ms up to 3s: if .acf comes back it was a patch
                // (suppress, exit within ~200ms of reappearance). If still gone after 3s it is a
                // real uninstall — proceed to kill. Game files are protected by directory deny rules
                // throughout, so no game files can be deleted during the wait.
                if (game.PlatformId == "steam" && !string.IsNullOrEmpty(game.ManifestPath))
                {
                    bool manifestReappeared = false;
                    for (int i = 0; i < 15; i++) // 15 × 200ms = 3s max
                    {
                        try { Thread.Sleep(200); } catch { }
                        if (File.Exists(game.ManifestPath))
                        {
                            manifestReappeared = true;
                            LogDebug($"[Steam] Suppressing block for {game.GameName} — manifest rewritten within {(i + 1) * 200}ms (patch applying, not uninstall)");
                            break;
                        }
                    }
                    if (manifestReappeared) return;
                }

                // Riot-specific: distinguish patch-YAML-rewrite from real uninstall attempt.
                // Riot's patcher deletes the manifest YAML then rewrites it as part of normal
                // updates. A real uninstall attempt leaves the YAML deleted. Wait briefly,
                // then check if the YAML reappeared — if yes, it was a patch, suppress entirely.
                // Why YAML reappearance (not exe-existence): when our deny rules successfully
                // block an uninstall, the exe is still present by design — exe-presence can't
                // distinguish "patch" from "blocked uninstall".
                if (game.PlatformId == "riot")
                {
                    try { Thread.Sleep(3000); } catch { }
                    if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                    {
                        LogDebug($"[Riot] Suppressing BLOCKED log for {game.GameName} — YAML rewritten within 3s (patch, not uninstall)");
                        return;
                    }
                }

                var platformDisplay = char.ToUpper(game.PlatformId[0]) + game.PlatformId.Substring(1);

                if (game.PlatformId == "steam")
                {
                    Log($"BLOCKED: {game.GameName} (Steam).");

                    // 1. Kill launcher immediately (stops further file deletion)
                    _platformManager.KillPlatformLauncher(game.PlatformId);
                    Thread.Sleep(2000); // Ensure process fully terminated — DO NOT REMOVE

                    // 2. Restore manifest from backup
                    _platformManager.RestoreGameManifests(game);

                    // 3. Re-apply protection
                    ApplyProtection(silent: true);

                    Thread.Sleep(1000); // Ensure file ops complete — DO NOT REMOVE

                    // Steam stays closed. The previous design auto-relaunched Steam via the
                    // GUI pipe (Session 0 isolation workaround) so the customer saw the game
                    // "magically" back, but it required the GUI to be running and added a
                    // class of silent failures (path resolution, GUI not running, etc.). We
                    // now let the customer relaunch Steam themselves; the tray notification
                    // explains what happened.
                }
                else
                {
                    // Epic/Riot: NTFS deny on manifest prevents uninstall without kill/restore.
                    Log($"BLOCKED: {game.GameName} ({platformDisplay}).");
                }

                // Notify the GUI for a friendly tray balloon tip — same notification on every
                // platform. Fire-and-forget; subscribers marshal to UI thread themselves.
                try { OnUninstallBlocked?.Invoke(game.PlatformId, game.GameName); }
                catch (Exception ex) { LogDebug($"[OnUninstallBlocked] subscriber threw: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] OnUninstallDetected failed: {ex.Message}");
                LogDebug($"[OnUninstallDetected] Exception: {ex}");
            }
        }

        void OnNewGameDetected(GameInfo game)
        {
            if (_maint) return;

            var existingPlatformConfig = _config.GetPlatform(game.PlatformId);
            var alreadyInProtectedGames = existingPlatformConfig.ProtectedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase);

            // If already in ProtectedGames, this event is either:
            //   (a) Post-restore false-positive — manifest recreated after Steam/Riot kill/restore
            //       cycle. The game folder is fully populated, no install activity. Protection was
            //       already re-applied by ApplyProtection(silent: true) inside OnUninstallDetected.
            //   (b) Reinstall — user is reinstalling a previously-protected game (Bug E2 path,
            //       Critical #E2). The install is in progress and our protection MUST NOT be
            //       re-applied yet, or the install will fail mid-stream (procmon-verified).
            //
            // Discriminator: check if the platform reports the game as "stable" right now. A
            // fully populated post-restore folder is stable. A fresh reinstall pulling files
            // into .egstore\bps\c\backingstore (Epic) or steamapps/downloading (Steam) is not.
            if (alreadyInProtectedGames)
            {
                var platform = _platformManager.Platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                bool isStable = true;
                try { if (platform != null) isStable = platform.IsGameStable(game.InstallPath); } catch { }

                if (isStable)
                {
                    // Case (a). Skip silently — protection already in place via OnUninstallDetected.
                    LogDebug($"[NewGame] Skipping {game.GameName} — already in ProtectedGames + stable (post-restore false-positive)");
                    return;
                }

                // Case (b). Fall through into the work item so we wait for stability before
                // applying protection. Note we do NOT return here.
                LogDebug($"[NewGame] {game.GameName} already in ProtectedGames but install in progress — waiting for stability before re-applying protection (E2)");
            }

            var installKey = $"{game.PlatformId}|{game.GameName}";

            // Skip auto-add if admin just explicitly removed this game from ProtectedGames.
            // When Riot protection is removed, Riot Client detects the change and rewrites its
            // manifest YAML, firing OnManifestCreated → OnNewGameDetected. Without this check,
            // the game gets auto-added back to ProtectedGames and re-locked — undoing the admin's
            // explicit unlock action. Suppress for 120 seconds after an explicit unlock.
            lock (_recentlyUnlockedLock)
            {
                if (_recentlyUnlockedGames.TryGetValue(installKey, out var unlockedAt))
                {
                    if ((DateTime.Now - unlockedAt).TotalSeconds < 120)
                    {
                        LogDebug($"[NewGame] Skipping {game.GameName} — recently unlocked by admin ({(int)(DateTime.Now - unlockedAt).TotalSeconds}s ago), suppressing auto-lock");
                        return;
                    }
                    // Window expired — clean up and allow detection to proceed normally
                    _recentlyUnlockedGames.Remove(installKey);
                }
            }

            // Dedup: if we're already processing this game (watcher fires multiple events per install), skip
            lock (_installLock)
            {
                if (_gamesBeingInstalled.Contains(installKey))
                {
                    LogDebug($"[NewGame] Skipping duplicate event for {game.GameName} — already processing");
                    return;
                }
                _gamesBeingInstalled.Add(installKey);
            }

            // Capture the config reference BEFORE entering the work item.
            // UpdateConfig() can replace _config on another thread at any time.
            // Using a single snapshot ensures that GetPlatform() and Save() both
            // operate on the same object — preventing a scenario where we add the
            // game to one config version but save a different, newer one.
            var configSnapshot = _config;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var platform = _platformManager.Platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                    if (platform == null) return;

                    // Check license first — free tier does nothing (game already appears in GUI via GetAllGames)
                    var licenseValid = LicenseManagerInternal.ValidateLicense(configSnapshot.LicenseKey, CacheManager.GetHardwareId());
                    LogDebug($"[License] OnNewGameDetected: licenseValid={licenseValid} game={game.GameName}");

                    if (!licenseValid) return;

                    // Paid tier: wait for game to be stable before locking
                    LogDebug($"[NEW GAME] {game.GameName} ({game.PlatformId}) - waiting to stabilize...");

                    bool stable = false;
                    for (int i = 0; i < 30; i++) // Max 5 minutes (30 * 10s)
                    {
                        if (platform.IsGameStable(game.InstallPath))
                        {
                            stable = true;
                            break;
                        }
                        LogDebug($"  -> Still waiting for {game.GameName} to stabilize... ({i + 1}/30)");
                        Thread.Sleep(10000);
                    }

                    if (!stable)
                        LogDebug($"  -> {game.GameName} did not stabilize in time. Locking anyway.");

                    // Auto-add to ProtectedGames so it persists across restarts.
                    // Use configSnapshot (captured before this thread started) so that
                    // GetPlatform() and Save() operate on the same config object.
                    var platformConfig = configSnapshot.GetPlatform(game.PlatformId);
                    if (!platformConfig.ProtectedGames.Contains(game.GameName))
                    {
                        platformConfig.ProtectedGames.Add(game.GameName);
                        configSnapshot.Save(_configPath);
                        LogDebug($"[NewGame] Auto-added {game.GameName} to ProtectedGames (paid tier)");
                    }

                    Safe(() => _platformManager.BackupManifests());
                    _platformManager.ApplySingleGameProtection(game, licenseValid);

                    Log($"[NEW GAME] {game.GameName} - locked.");
                }
                finally
                {
                    lock (_installLock) { _gamesBeingInstalled.Remove(installKey); }
                }
            });
        }

        void HealthCheck()
        {
            if (_maint) return;

            // Reentrancy guard — if previous health check is still running (slow PC/HDD), skip this tick
            if (Interlocked.CompareExchange(ref _healthCheckRunning, 1, 0) != 0)
            {
                LogDebug("[HealthCheck] Skipped — previous check still running");
                return;
            }

            // Skip if ApplyProtection() is currently running (e.g. triggered by UpdateConfig from a PushConfig pipe call).
            // Without this, HealthCheck and ApplyProtection run in parallel and both call SetDenyRule on the same
            // game, wasting two icacls processes. ApplyProtection already covers everything HealthCheck would do.
            if (Volatile.Read(ref _applyProtectionRunning) == 1)
            {
                LogDebug("[HealthCheck] Skipped — ApplyProtection running");
                Interlocked.Exchange(ref _healthCheckRunning, 0);
                return;
            }

            try
            {
                // Launchers can be installed after GamePinned starts. Refresh the installed-platform
                // inventory once per minute so the GUI and watchers discover them without requiring
                // a service restart. DetectPlatforms is change-aware, so config writes and watcher
                // restarts happen only when a platform or library path actually changed.
                _platformDiscoveryTickCount++;
                if (_platformDiscoveryTickCount >= 6)
                {
                    _platformDiscoveryTickCount = 0;
                    if (_platformManager.DetectPlatforms())
                    {
                        _config.Save(_configPath);
                        _platformManager.StopWatchers();
                        _platformManager.StartWatchers(OnUninstallDetected, OnNewGameDetected);
                        LogDebug("[PlatformDiscovery] Installed platform inventory changed; config saved and watchers refreshed");
                    }
                }

                var licenseValid = LicenseManagerInternal.ValidateLicense(_config.LicenseKey, CacheManager.GetHardwareId());
                if (licenseValid != _lastHealthCheckLicenseValid)
                {
                    LogDebug($"[HealthCheck] licenseValid changed → {licenseValid}");
                    _lastHealthCheckLicenseValid = licenseValid;
                }
                _platformManager.HealthCheck(licenseValid);
            }
            finally
            {
                Interlocked.Exchange(ref _healthCheckRunning, 0);
            }
        }

        void CloudSync()
        {
            if (string.IsNullOrEmpty(_config.LicenseKey)) return;
            if (string.IsNullOrEmpty(_config.RawLicenseKey)) { LogDebug("[License] CloudSync: skipped — RawLicenseKey not set (pre-fix install, re-activate to enable)."); return; }
            LogDebug("[License] CloudSync: starting...");

            // Capture config reference before entering the async task.
            // UpdateConfig() can replace _config on another thread; using a snapshot
            // ensures RawLicenseKey, LicenseKey reads and the final Save() all see
            // the same object — no stale config written over a newer UpdateConfig save.
            var syncConfig = _config;

            Task.Run(async () =>
            {
                try
                {
                    var requestJson = $"{{\"key\":\"{syncConfig.RawLicenseKey}\",\"hwid\":\"{CacheManager.GetHardwareId()}\"}}";
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    // Use the production backend URL
                    var response = await _httpClient.PostAsync("https://api.example.com/sync", content);

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        LogDebug("[License] CloudSync: 403 Forbidden — license revoked by admin. Clearing local license.");
                        Log("License revoked by admin. Downgrading to free tier.");
                        syncConfig.LicenseKey = "";
                        syncConfig.RawLicenseKey = "";
                        syncConfig.Save(_configPath);
                        // Also update the live _config reference so subsequent ValidateLicense calls see empty key
                        _config.LicenseKey = "";
                        _config.RawLicenseKey = "";
                        ApplyProtection();
                        return;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // 404 = key not found in backend (wrong format, corrupted RawLicenseKey, or
                        // pre-activation state). This is NOT a revocation — do NOT clear the license.
                        // Local HMAC validation still works. Customer should re-activate to fix sync.
                        LogDebug("[License] CloudSync: 404 Not Found — key not recognized by backend. RawLicenseKey may be wrong format. Local license preserved; re-activate to fix.");
                        return;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("license_blob", out var blobProp))
                        {
                            var newBlob = blobProp.GetString();
                            if (!string.IsNullOrEmpty(newBlob) && newBlob != syncConfig.LicenseKey)
                            {
                                LogDebug("[License] CloudSync: new blob received from server, updating local key.");
                                Log("License updated from server.");
                                syncConfig.LicenseKey = newBlob;
                                syncConfig.Save(_configPath);
                                ApplyProtection(silent: true);
                            }
                            else
                            {
                                LogDebug($"[License] CloudSync: OK, blob unchanged. HTTP {(int)response.StatusCode}");
                            }
                        }
                        else
                        {
                            LogDebug($"[License] CloudSync: OK, no license_blob in response. HTTP {(int)response.StatusCode}");
                        }
                    }
                    else
                    {
                        LogDebug($"[License] CloudSync: non-success HTTP {(int)response.StatusCode}");
                    }
                }
                catch (Exception ex) { LogDebug($"[License] CloudSync: failed — {ex.Message}"); }
            });
        }

        // ═══════════ MAINTENANCE ═══════════
        public void ToggleMaintenance()
        {
            // Sync with config (GUI already toggled config.MaintenanceMode before sending this command)
            _maint = _config.MaintenanceMode;

            if (_maint)
            {
                Log("MAINTENANCE ON - Locks disabled");
                // Wait for any in-progress ApplyProtection before removing rules (same race as UpdateConfig).
                _applyProtectionLock.Wait();
                try
                {
                    _platformManager.RemoveProtection("maintenance-toggled-on");
                }
                finally
                {
                    _applyProtectionLock.Release();
                }
            }
            else
            {
                Log("MAINTENANCE OFF - Re-pinning...");
                ApplyProtection();

                // Restart watchers — config may have changed while maintenance was on
                // (e.g. user added/removed a protected game or blocked user while in maintenance)
                _platformManager.StopWatchers();
                _platformManager.StartWatchers(OnUninstallDetected, OnNewGameDetected);

                _platformManager.BackupManifests();
            }
        }


        // ═══════════ AUTO-UPDATE ═══════════
        void TrySilentUpdate()
        {
            // Auto-update is a paid-tier feature only
            var licenseValid = LicenseManagerInternal.ValidateLicense(_config.LicenseKey, CacheManager.GetHardwareId());
            if (!licenseValid) return;

            Task.Run(async () =>
            {
                var updatePath = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "update.exe");
                try
                {
                    var url = GetEffectiveUpdateUrl();
                    using var checkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var response = await checkClient.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(response);
                    var root = doc.RootElement;

                    var latestStr = root.GetProperty("version").GetString() ?? "1.0.0";
                    var downloadUrl = root.GetProperty("downloadUrl").GetString() ?? "";
                    var expectedSha256 = root.TryGetProperty("sha256Hash", out var sha256Prop) ? sha256Prop.GetString() ?? "" : "";

                    if (!Version.TryParse(latestStr, out var latest)) return;
                    var current = typeof(PinAgent).Assembly.GetName().Version ?? new Version(1, 0, 0);
                    LogDebug($"[Update] TrySilentUpdate: latest={latestStr} current={current}");
                    if (latest <= current || string.IsNullOrEmpty(downloadUrl)) return;

                    Log($"Update available: v{current} → v{latest}. Downloading...");

                    // Per-chunk stall detection instead of HttpClient.Timeout:
                    // HttpClient.Timeout applies to the ENTIRE request (including body read), so a
                    // short total timeout breaks large downloads on slow cafe networks. Infinite timeout
                    // hangs forever on dead connections. Correct pattern: no total timeout, but cancel
                    // if no bytes arrive for 60s (timer resets on every successful chunk).
                    const int StallTimeoutSeconds = 60;
                    using var dlClient = new HttpClient();
                    using var headersCts = new CancellationTokenSource(TimeSpan.FromSeconds(StallTimeoutSeconds));
                    using var dlResponse = await dlClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                    dlResponse.EnsureSuccessStatusCode();
                    using (var contentStream = await dlResponse.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(updatePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        while (true)
                        {
                            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(StallTimeoutSeconds));
                            int bytesRead;
                            try { bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token); }
                            catch (OperationCanceledException) { Log("Update download stalled — aborting."); LogDebug("[Update] Download stalled, no bytes in " + StallTimeoutSeconds + "s"); try { File.Delete(updatePath); } catch { } return; }
                            if (bytesRead == 0) break;
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        }
                    }

                    if (!File.Exists(updatePath) || new FileInfo(updatePath).Length == 0)
                    {
                        Log("Update download failed: empty file.");
                        return;
                    }

                    // Verify SHA256 if the server provided one
                    if (!string.IsNullOrEmpty(expectedSha256))
                    {
                        using var hashStream = File.OpenRead(updatePath);
                        var actualBytes = await System.Security.Cryptography.SHA256.HashDataAsync(hashStream);
                        var actualHex = BitConverter.ToString(actualBytes).Replace("-", "").ToLower();
                        if (!string.Equals(actualHex, expectedSha256.ToLower(), StringComparison.Ordinal))
                        {
                            File.Delete(updatePath);
                            Log("Update aborted: SHA256 mismatch. File may be corrupted.");
                            return;
                        }
                    }

                    // Flag file so the new GUI shows a notification after restart
                    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "autoupdated.flag"), latestStr);

                    Log($"Update downloaded. Installing v{latest} silently...");

                    // Service runs as SYSTEM — no UAC prompt needed
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = updatePath,
                        Arguments = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    LogDebug($"Installer process started. PID={proc?.Id.ToString() ?? "null"}.");

                    // Clean up the downloaded installer after it starts (it copies itself during install)
                    if (proc != null)
                    {
                        try
                        {
                            proc.WaitForExit(120000); // Wait up to 2 minutes for installer to finish
                            if (File.Exists(updatePath)) File.Delete(updatePath);
                        }
                        catch (Exception exCleanup) { LogDebug($"[Update] Installer cleanup error: {exCleanup.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    // Clean up failed update file
                    try { if (File.Exists(updatePath)) File.Delete(updatePath); } catch { }
                    Log($"Auto-update check failed: {ex.Message}");
                    LogDebug($"[Update] TrySilentUpdate exception: {ex}");
                }
            });
        }

        string GetEffectiveUpdateUrl()
        {
            const string prodUrl = "https://api.example.com/version";
            return string.IsNullOrEmpty(_config.DevUpdateUrl) ? prodUrl : _config.DevUpdateUrl;
        }

        // ═══════════ LOGGING ═══════════
        public void Log(string msg)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine($"  [{ts}] {msg}");
            try { File.AppendAllText(Path.Combine(_logPath, $"gamepinned_{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
        }

        /// <summary>
        /// Debug log — writes to debug_*.log only, never shown in GUI
        /// </summary>
        public void LogDebug(string msg)
        {
            try
            {
                var date = DateTime.Now.ToString("yyyyMMdd");
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
                File.AppendAllText(
                    Path.Combine(_logPath, $"debug_{date}.log"),
                    EncryptDebugLine(line, _logPath) + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Cleanup old log files (older than 7 days)
        /// </summary>
        private void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logPath))
                    return;

                var cutoffDate = DateTime.Now.AddDays(-LogRetentionDays);
                int deletedCount = 0;

                var cutoffStr = cutoffDate.ToString("yyyyMMdd");
                foreach (var pattern in new[] { "gamepinned_*.log", "debug_*.log" })
                {
                    foreach (var logFile in Directory.GetFiles(_logPath, pattern))
                    {
                        try
                        {
                            var name = Path.GetFileNameWithoutExtension(logFile);
                            var datePart = name.Length >= 8 ? name[^8..] : "";
                            if (string.Compare(datePart, cutoffStr, StringComparison.Ordinal) <= 0)
                            {
                                File.Delete(logFile);
                                deletedCount++;
                            }
                        }
                        catch { }
                    }
                }

                if (deletedCount > 0)
                    LogDebug($"[Cleanup] Deleted {deletedCount} log file(s) older than {LogRetentionDays} days");
            }
            catch (Exception ex)
            {
                Log($"Log cleanup error: {ex.Message}");
            }
        }
    }
}
