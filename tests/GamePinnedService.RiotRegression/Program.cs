using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GamePinned.Service.Core;
using GamePinned.Service.Platforms;
using GamePinned.Shared.Models;

if (!OperatingSystem.IsWindows())
    return Fail("This regression requires Windows.");

var fixtureRoot = Path.Combine(Path.GetTempPath(), "GamePinned-RiotRegression-" + Guid.NewGuid().ToString("N"));
var metadataRoot = Path.Combine(fixtureRoot, "Metadata");
var backupRoot = Path.Combine(fixtureRoot, "Backups");
var sharedRoot = Path.Combine(fixtureRoot, "Shared Riot Root");
var deepLorPath = Path.Combine(sharedRoot, "LoR", "live", "Game");
var valorantPath = Path.Combine(sharedRoot, "VALORANT");
var misleadingLorPath = Path.Combine(sharedRoot, "LoRBackup");
var specificUnknownPath = Path.Combine(fixtureRoot, "Publisher", "Specific Game");
var aclRiotRoot = Path.Combine(fixtureRoot, "AclProbe");
var aclGamePath = Path.Combine(aclRiotRoot, "Game");
var aclManifestParent = Path.Combine(aclRiotRoot, "Metadata", "probe.live");
var aclManifestPath = Path.Combine(aclManifestParent, "probe.live.product_settings.yaml");
var aclTransitionPayload = Path.Combine(aclGamePath, "Payload");
var aclTransitionProbe = Path.Combine(aclTransitionPayload, "Probe");
var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
var usersIdentity = "*" + usersSid.Value;
var vendorSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
var vendorIdentity = "*" + vendorSid.Value;
var logs = new List<string>();

try
{
    Directory.CreateDirectory(metadataRoot);
    Directory.CreateDirectory(backupRoot);
    Directory.CreateDirectory(Path.Combine(sharedRoot, "Riot Client"));
    Directory.CreateDirectory(deepLorPath);
    Directory.CreateDirectory(valorantPath);
    Directory.CreateDirectory(misleadingLorPath);
    Directory.CreateDirectory(specificUnknownPath);
    Directory.CreateDirectory(aclGamePath);
    Directory.CreateDirectory(aclManifestParent);
    // Match a real Riot root so parent cleanup stops here instead of walking
    // into the test runner's active profile directory.
    Directory.CreateDirectory(Path.Combine(aclRiotRoot, "Riot Client"));
    Directory.CreateDirectory(aclTransitionProbe);
    // Keep enough child ACL work to make a remove-first transition observable
    // without launching hundreds of concurrent icacls processes on modest test hosts.
    for (var index = 0; index < 8; index++)
        Directory.CreateDirectory(Path.Combine(aclTransitionPayload, $"load-{index:D4}"));
    File.WriteAllText(aclManifestPath, "product_install_full_path: 'C:/fixture'\n");

    WriteManifest("bacon.live", deepLorPath, useRootProperty: false);
    WriteManifest("valorant.live", sharedRoot, useRootProperty: false);
    WriteManifest("teamfighttactics.live", sharedRoot, useRootProperty: true);
    WriteManifest("mystery.live", sharedRoot, useRootProperty: false);
    WriteManifest("bacon", misleadingLorPath, useRootProperty: false);
    WriteManifest("future_title.live", specificUnknownPath, useRootProperty: false);
    WriteManifest("riot_client.live", Path.Combine(sharedRoot, "Riot Client"), useRootProperty: false);
    WriteManifest("danger.live", Path.GetPathRoot(fixtureRoot)!, useRootProperty: false);

    var platform = new RiotPlatform(backupRoot, logs.Add, logs.Add);
    var games = platform.GetInstalledGames(metadataRoot);
    var byName = games.GroupBy(game => game.GameName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    var lorGame = byName["Legends of Runeterra"].Single();
    var futureGame = byName["Future_title.live"].Single();
    platform.BackupManifests(metadataRoot, new List<string>());
    File.Delete(lorGame.ManifestPath);
    File.Delete(futureGame.ManifestPath);
    ((IGamePlatform)platform).RestoreGameManifest(lorGame, metadataRoot, new List<string>());
    var triggeringManifestRestored = File.Exists(lorGame.ManifestPath);
    var unrelatedManifestStayedAbsent = !File.Exists(futureGame.ManifestPath);
    var flattenedManifestNotCreated = !File.Exists(Path.Combine(metadataRoot,
        Path.GetFileName(lorGame.ManifestPath)));
    var unrelatedFlattenedManifestNotCreated = !File.Exists(Path.Combine(metadataRoot,
        Path.GetFileName(futureGame.ManifestPath)));

    const string reappearedManifest = "product_install_full_path: 'live replacement'\n";
    File.WriteAllText(lorGame.ManifestPath, reappearedManifest);
    ((IGamePlatform)platform).RestoreGameManifest(lorGame, metadataRoot, new List<string>());
    var reappearedLiveManifestNotOverwritten =
        File.ReadAllText(lorGame.ManifestPath) == reappearedManifest;

    File.Delete(lorGame.ManifestPath);
    ((IGamePlatform)platform).RestoreGameManifest(new GameInfo
    {
        PlatformId = "riot",
        GameName = "Different Game",
        InstallPath = lorGame.InstallPath,
        ManifestPath = lorGame.ManifestPath,
        GameId = lorGame.GameId
    }, metadataRoot, new List<string>());
    var ownershipMismatchRejected = !File.Exists(lorGame.ManifestPath);

    var outsideManifest = Path.Combine(fixtureRoot, "OutsideMetadata",
        Path.GetFileName(lorGame.ManifestPath));
    ((IGamePlatform)platform).RestoreGameManifest(new GameInfo
    {
        PlatformId = "riot",
        GameName = lorGame.GameName,
        InstallPath = lorGame.InstallPath,
        ManifestPath = outsideManifest,
        GameId = lorGame.GameId
    }, metadataRoot, new List<string>());
    var outsideMetadataRootRejected = !File.Exists(outsideManifest);

    ((IGamePlatform)platform).RestoreGameManifest(futureGame, metadataRoot,
        new List<string> { futureGame.GameName });
    var excludedGameNotRestored = !File.Exists(futureGame.ManifestPath);

    platform.BackupManifests(metadataRoot, new List<string>());
    ((IGamePlatform)platform).RestoreGameManifest(lorGame, metadataRoot, new List<string>());
    var missingSourceBackupRetained = File.Exists(lorGame.ManifestPath);
    var restore = new Dictionary<string, bool>
    {
        ["TriggeringNestedManifestRestored"] = triggeringManifestRestored,
        ["UnrelatedMissingManifestNotRestored"] = unrelatedManifestStayedAbsent,
        ["NoFlattenedRootManifestCreated"] = flattenedManifestNotCreated,
        ["NoUnrelatedFlattenedRootManifestCreated"] = unrelatedFlattenedManifestNotCreated,
        ["ReappearedLiveManifestNotOverwritten"] = reappearedLiveManifestNotOverwritten,
        ["BackupOwnershipMismatchRejected"] = ownershipMismatchRejected,
        ["OutsideMetadataRootRejected"] = outsideMetadataRootRejected,
        ["ExcludedGameNotRestored"] = excludedGameNotRestored,
        ["MissingSourceBackupRetainedAcrossRefresh"] = missingSourceBackupRetained
    };

    var protectionCore = typeof(RiotPlatform).GetMethod("CheckProtectionActiveCore",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Riot protection-contract method not found.");

    var transitionGame = new GameInfo
    {
        PlatformId = "riot",
        GameName = "Policy Transition Game",
        InstallPath = aclGamePath,
        ManifestPath = aclManifestPath,
        GameId = "policy-transition"
    };
    platform.ApplyProtection(transitionGame, new List<string>(), blockAllNonAdminByDefault: true);
    var transitionBaselineExact = (bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? false);
    var transitionStop = 0;
    var transitionGapObserved = 0;
    var transitionSamples = 0;
    using var transitionWorkerReady = new ManualResetEventSlim();
    var transitionWorker = new Thread(() =>
    {
        transitionWorkerReady.Set();
        while (Volatile.Read(ref transitionStop) == 0)
        {
            try
            {
                var directoryGroupProtected = HasDeleteDeny(aclTransitionProbe, usersSid, includeInherited: true);
                var directoryUserProtected = HasDeleteDeny(aclTransitionProbe, vendorSid, includeInherited: true);
                var manifestGroupProtected = HasDeleteDeny(aclManifestPath, usersSid, includeInherited: true);
                var manifestUserProtected = HasDeleteDeny(aclManifestPath, vendorSid, includeInherited: true);
                Interlocked.Increment(ref transitionSamples);
                if ((!directoryGroupProtected && !directoryUserProtected) ||
                    (!manifestGroupProtected && !manifestUserProtected))
                    Interlocked.Exchange(ref transitionGapObserved, 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            Thread.Yield();
        }
    }) { IsBackground = true, Name = "RiotPolicyTransitionAclProbe" };
    transitionWorker.Start();
    transitionWorkerReady.Wait();
    Thread.Sleep(100);
    var samplesBeforeTransition = Volatile.Read(ref transitionSamples);
    platform.ApplyProtection(
        transitionGame, new List<string> { vendorIdentity }, blockAllNonAdminByDefault: true);
    Thread.Sleep(100);
    Volatile.Write(ref transitionStop, 1);
    transitionWorker.Join(TimeSpan.FromSeconds(5));
    var transitionSpecificExact = (bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? false) &&
        HasDeleteDeny(aclGamePath, vendorSid, includeInherited: false) &&
        HasDeleteDeny(aclManifestPath, vendorSid, includeInherited: false);
    var obsoleteGroupRootRemoved = !HasDeleteDeny(aclGamePath, usersSid, includeInherited: false);
    platform.ApplyProtection(transitionGame, new List<string>(), blockAllNonAdminByDefault: true);
    var obsoleteUnenumeratedIdentityRemovedOnReturnToGroup =
        (bool)(protectionCore.Invoke(platform, new object?[] { aclGamePath, aclManifestPath }) ?? false) &&
        HasDeleteDeny(aclGamePath, usersSid, includeInherited: false) &&
        HasDeleteDeny(aclManifestPath, usersSid, includeInherited: false) &&
        !HasDeleteDeny(aclGamePath, vendorSid, includeInherited: false) &&
        !HasDeleteDeny(aclManifestPath, vendorSid, includeInherited: false);
    var policyTransition = new Dictionary<string, bool>
    {
        ["DefaultGroupBaselineWasExact"] = transitionBaselineExact,
        ["BoundarySamplerObservedProtectedBaseline"] = samplesBeforeTransition > 0,
        ["DirectoryAndManifestContinuousDuringTransition"] = transitionGapObserved == 0,
        ["SpecificIdentityContractExactAfterTransition"] = transitionSpecificExact,
        ["ObsoleteGroupRootRemoved"] = obsoleteGroupRootRemoved,
        ["ObsoleteUnenumeratedIdentityRemovedOnReturnToGroup"] = obsoleteUnenumeratedIdentityRemovedOnReturnToGroup
    };
    platform.RemoveProtection(transitionGame, new List<string>());

    AddDeny(aclGamePath, usersSid, FileSystemRights.Read,
        InheritanceFlags.None);
    var unrelatedReadDenyRejected = !(bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? true);
    RemoveExplicitDenies(aclGamePath, usersSid);

    AddDeny(aclGamePath, usersSid, FileSystemRights.Delete,
        InheritanceFlags.ContainerInherit);
    AddDeny(aclManifestPath, usersSid, FileSystemRights.Delete,
        InheritanceFlags.None);
    AddDeny(aclManifestParent, usersSid, FileSystemRights.DeleteSubdirectoriesAndFiles,
        InheritanceFlags.None);
    var exactContractAccepted = (bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? false);
    RemoveExplicitDenies(aclManifestParent, usersSid);
    var missingParentBoundaryRejected = !(bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? true);
    AddDeny(aclManifestParent, usersSid, FileSystemRights.DeleteSubdirectoriesAndFiles,
        InheritanceFlags.None);
    RemoveExplicitDenies(aclManifestPath, usersSid);
    var missingManifestBoundaryRejected = !(bool)(protectionCore.Invoke(platform,
        new object?[] { aclGamePath, aclManifestPath }) ?? true);
    var verifier = new Dictionary<string, bool>
    {
        ["UnrelatedReadDenyRejected"] = unrelatedReadDenyRejected,
        ["ExactThreeBoundaryContractAccepted"] = exactContractAccepted,
        ["MissingManifestParentBoundaryRejected"] = missingParentBoundaryRejected,
        ["MissingManifestBoundaryRejected"] = missingManifestBoundaryRejected
    };

    var checks = new Dictionary<string, bool>
    {
        ["ForwardSlashDeepLoRPathNormalized"] =
            SinglePath("Legends of Runeterra", deepLorPath),
        ["KnownSharedRootRefinedToValorant"] =
            SinglePath("Valorant", valorantPath),
        ["PhantomKnownGameSkipped"] = !byName.ContainsKey("Teamfight Tactics"),
        ["UnknownSharedRootGameSkipped"] = !byName.ContainsKey("Mystery.live"),
        ["CandidateSubstringIsNotPathComponent"] =
            byName.TryGetValue("Legends of Runeterra", out var lorGames) &&
            lorGames.All(game => !PathsEqual(game.InstallPath, misleadingLorPath)),
        ["UnknownSpecificPathAccepted"] = SinglePath("Future_title.live", specificUnknownPath),
        ["RiotClientManifestFiltered"] = !byName.ContainsKey("Riot_client.live"),
        ["DriveRootRejected"] = !byName.ContainsKey("Danger.live"),
        ["NarrowActivityRejectsSiblingPrefix"] =
            CountNarrowActivityEvents(Path.Combine(deepLorPath + "Backup", "chunk.bin")) == 0,
        ["NarrowActivityAcceptsDescendant"] =
            CountNarrowActivityEvents(Path.Combine(deepLorPath, "chunk.bin")) == 1,
        ["ProcessFallbackRejectsSiblingPrefix"] =
            ProbeProcessFallback(deepLorPath + "Backup") == false,
        ["ProcessFallbackAcceptsDescendant"] =
            ProbeProcessFallback(deepLorPath),
        ["KnownProcessNameOutsideInstallRejected"] =
            ProbeProcessFallback(Path.Combine(fixtureRoot, "UnrelatedProcess"), "LoR") == false,
        ["KnownProcessNameInsideInstallAccepted"] =
            ProbeProcessFallback(deepLorPath, "LoR"),
        ["UnlockedGameDeletionDoesNotTriggerFallbackBlock"] =
            CountUninstallFallbackEvents(protectedIntent: false, eventCount: 15) == 0,
        ["ProtectedGameDeletionFeedsFallbackDetector"] =
            CountUninstallFallbackEvents(protectedIntent: true, eventCount: 1) == 1,
        ["ProtectionTransitionResetsFallbackWindow"] =
            CountEventsAcrossProtectionTransition() == 1,
        ["OnlyExpectedGamesReturned"] = games.Count == 3,
        ["SharedRootSkipDiagnosticDeduplicated"] =
            logs.Count(line => line.Contains("unknown game, no subfolder mapping", StringComparison.Ordinal)) == 1,
        ["PhantomSkipDiagnosticPresent"] =
            logs.Any(line => line.Contains("Teamfight Tactics: no install folder found", StringComparison.Ordinal))
    };

    var passed = checks.Values.All(value => value) &&
                 restore.Values.All(value => value) &&
                 policyTransition.Values.All(value => value) &&
                 verifier.Values.All(value => value);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Test = "Riot YAML discovery and path refinement",
        Passed = passed,
        Checks = checks,
        ExactRestore = restore,
        PolicyTransitionContinuity = policyTransition,
        ProtectionVerifier = verifier,
        Games = games.Select(game => new { game.GameName, game.InstallPath, game.ManifestPath }),
        Logs = logs
    }, new JsonSerializerOptions { WriteIndented = true }));
    return passed ? 0 : 1;

    bool SinglePath(string gameName, string expectedPath) =>
        byName.TryGetValue(gameName, out var matches) &&
        matches.Count == 1 && PathsEqual(matches[0].InstallPath, expectedPath);

    int CountNarrowActivityEvents(string eventPath)
    {
        var probe = new RiotPlatform(backupRoot, _ => { }, _ => { });
        var platformType = typeof(RiotPlatform);
        var knownPathsField = platformType.GetField("_knownGameInstallPaths",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot known-path field not found.");
        var eventMethod = platformType.GetMethod("OnNarrowActivityEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot narrow-activity method not found.");
        var eventQueueField = platformType.GetField("_recentFswEventTicks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot activity queue field not found.");

        knownPathsField.SetValue(probe,
            new HashSet<string>(new[] { deepLorPath }, StringComparer.OrdinalIgnoreCase));
        eventMethod.Invoke(probe, new object[] { eventPath, false });
        return ((IEnumerable<long>)eventQueueField.GetValue(probe)!).Count();
    }

    bool ProbeProcessFallback(string executableDirectory, string executableName = "FutureRiotProbe")
    {
        Directory.CreateDirectory(executableDirectory);
        var probeExecutable = Path.Combine(executableDirectory, executableName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), probeExecutable, overwrite: true);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = probeExecutable,
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start Riot process-containment probe.");

        try
        {
            Thread.Sleep(200);
            var probe = new RiotPlatform(backupRoot, _ => { }, _ => { });
            var platformType = typeof(RiotPlatform);
            var knownPathsField = platformType.GetField("_knownGameInstallPaths",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Riot known-path field not found.");
            var runningMethod = platformType.GetMethod("IsAnyRiotGameRunning",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Riot game-running method not found.");

            knownPathsField.SetValue(probe,
                new HashSet<string>(new[] { deepLorPath }, StringComparer.OrdinalIgnoreCase));
            return (bool)(runningMethod.Invoke(probe, null)
                ?? throw new InvalidOperationException("Riot process-containment probe returned null."));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    int CountUninstallFallbackEvents(bool protectedIntent, int eventCount)
    {
        var probe = new RiotPlatform(backupRoot, _ => { }, _ => { });
        var platformType = typeof(RiotPlatform);
        var knownPathsField = platformType.GetField("_knownGameInstallPaths",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot known-path field not found.");
        var intentMethod = platformType.GetMethod("SetGameProtectionIntent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot protection-intent method not found.");
        var eventMethod = platformType.GetMethod("OnNarrowActivityEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot narrow-activity method not found.");
        var deleteQueueField = platformType.GetField("_uninstallDeleteTicks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot uninstall-delete queue field not found.");

        knownPathsField.SetValue(probe,
            new HashSet<string>(new[] { deepLorPath }, StringComparer.OrdinalIgnoreCase));
        intentMethod.Invoke(probe, new object[] { deepLorPath, protectedIntent });
        for (var index = 0; index < eventCount; index++)
        {
            eventMethod.Invoke(probe,
                new object[] { Path.Combine(deepLorPath, $"removed-{index}.bin"), true });
        }

        return ((IEnumerable<long>)deleteQueueField.GetValue(probe)!).Count();
    }

    int CountEventsAcrossProtectionTransition()
    {
        var probe = new RiotPlatform(backupRoot, _ => { }, _ => { });
        var platformType = typeof(RiotPlatform);
        var knownPathsField = platformType.GetField("_knownGameInstallPaths",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot known-path field not found.");
        var intentMethod = platformType.GetMethod("SetGameProtectionIntent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot protection-intent method not found.");
        var eventMethod = platformType.GetMethod("OnNarrowActivityEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot narrow-activity method not found.");
        var deleteQueueField = platformType.GetField("_uninstallDeleteTicks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Riot uninstall-delete queue field not found.");

        knownPathsField.SetValue(probe,
            new HashSet<string>(new[] { deepLorPath }, StringComparer.OrdinalIgnoreCase));
        intentMethod.Invoke(probe, new object[] { deepLorPath, true });
        for (var index = 0; index < 14; index++)
        {
            eventMethod.Invoke(probe,
                new object[] { Path.Combine(deepLorPath, $"before-transition-{index}.bin"), true });
        }
        intentMethod.Invoke(probe, new object[] { deepLorPath, false });
        intentMethod.Invoke(probe, new object[] { deepLorPath, true });
        eventMethod.Invoke(probe,
            new object[] { Path.Combine(deepLorPath, "after-transition.bin"), true });

        return ((IEnumerable<long>)deleteQueueField.GetValue(probe)!).Count();
    }

    void WriteManifest(string productName, string installPath, bool useRootProperty)
    {
        var directory = Path.Combine(metadataRoot, productName);
        Directory.CreateDirectory(directory);
        var property = useRootProperty ? "product_install_root" : "product_install_full_path";
        var yamlPath = Path.Combine(directory, productName + ".product_settings.yaml");
        File.WriteAllText(yamlPath, $"{property}: '{installPath.Replace('\\', '/')}'\n");
    }
}
finally
{
    RemoveExplicitDenies(aclGamePath, usersSid);
    RemoveExplicitDenies(aclManifestPath, usersSid);
    RemoveExplicitDenies(aclManifestParent, usersSid);
    RemoveExplicitDenies(aclGamePath, vendorSid);
    RemoveExplicitDenies(aclManifestPath, vendorSid);
    RemoveExplicitDenies(aclManifestParent, vendorSid);
    if (Directory.Exists(fixtureRoot))
        Directory.Delete(fixtureRoot, recursive: true);
}

static bool PathsEqual(string left, string right) =>
    string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
        StringComparison.OrdinalIgnoreCase);

static void AddDeny(string path, SecurityIdentifier sid, FileSystemRights rights,
    InheritanceFlags inheritanceFlags)
{
    FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
    security.AddAccessRule(new FileSystemAccessRule(sid, rights, inheritanceFlags,
        PropagationFlags.None, AccessControlType.Deny));
    if (security is DirectorySecurity directorySecurity)
        new DirectoryInfo(path).SetAccessControl(directorySecurity);
    else
        new FileInfo(path).SetAccessControl((FileSecurity)security);
}

static bool HasDeleteDeny(string path, SecurityIdentifier sid, bool includeInherited)
{
    FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
    return security.GetAccessRules(true, includeInherited, typeof(SecurityIdentifier))
        .OfType<FileSystemAccessRule>()
        .Any(rule => rule.AccessControlType == AccessControlType.Deny &&
            rule.IdentityReference is SecurityIdentifier ruleSid &&
            ruleSid.Value == sid.Value &&
            (rule.FileSystemRights & FileSystemRights.Delete) != 0);
}

static void RemoveExplicitDenies(string path, SecurityIdentifier sid)
{
    if (!Directory.Exists(path) && !File.Exists(path))
        return;
    FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
    foreach (var rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))
        .OfType<FileSystemAccessRule>()
        .Where(rule => rule.AccessControlType == AccessControlType.Deny &&
            rule.IdentityReference is SecurityIdentifier ruleSid && ruleSid.Value == sid.Value)
        .ToList())
    {
        security.RemoveAccessRuleSpecific(rule);
    }
    if (security is DirectorySecurity directorySecurity)
        new DirectoryInfo(path).SetAccessControl(directorySecurity);
    else
        new FileInfo(path).SetAccessControl((FileSecurity)security);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
