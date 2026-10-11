using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GamePinned.Service.Core;
using GamePinned.Service.Platforms;
using GamePinned.Shared.Models;

if (!OperatingSystem.IsWindows())
    return Fail("This regression requires Windows.");

using var identity = WindowsIdentity.GetCurrent();
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    return Fail("Run this regression from an elevated process.");

var fixtureRoot = Path.Combine(Path.GetTempPath(), "GamePinned-SteamRegression-" + Guid.NewGuid().ToString("N"));
var backupPath = Path.Combine(fixtureRoot, "backups", "steam");
var steamApps = Path.Combine(fixtureRoot, "SteamLibrary", "steamapps");
var commonPath = Path.Combine(steamApps, "common");
var gamePath = Path.Combine(steamApps, "common", "Fixture Game");
var payloadPath = Path.Combine(gamePath, "Payload", "game.bin");
var manifestPath = Path.Combine(steamApps, "appmanifest_424242.acf");
var correctDownload = Path.Combine(steamApps, "downloading", "424242");
var wrongDownload = Path.Combine(steamApps, "downloading", "777777");
var unrelatedPath = Path.Combine(steamApps, "common", "Unrelated Game");
var unrelatedPayload = Path.Combine(unrelatedPath, "keep.bin");
var secondSteamApps = Path.Combine(fixtureRoot, "SecondLibrary", "steamapps");
var secondCommonPath = Path.Combine(secondSteamApps, "common");
var secondGamePath = Path.Combine(secondCommonPath, "Second Library Game");
var secondPayloadPath = Path.Combine(secondGamePath, "second.bin");
var secondManifestPath = Path.Combine(secondSteamApps, "appmanifest_888888.acf");
var transitionGamePath = Path.Combine(commonPath, "Policy Transition Game");
var transitionPayloadPath = Path.Combine(transitionGamePath, "Payload");
var transitionProbePath = Path.Combine(transitionPayloadPath, "policy-transition-probe.bin");
var statusProbePath = Path.Combine(steamApps, "common", "Status Probe");
var restoreLibrary = Path.Combine(fixtureRoot, "RestoreLibrary");
var restoreSteamApps = Path.Combine(restoreLibrary, "steamapps");
var restoreGameA = Path.Combine(restoreSteamApps, "common", "Restore A");
var restoreGameB = Path.Combine(restoreSteamApps, "common", "Restore B");
var restoreManifestA = Path.Combine(restoreSteamApps, "appmanifest_1001.acf");
var restoreManifestB = Path.Combine(restoreSteamApps, "appmanifest_1002.acf");
var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
var usersIdentity = "*" + usersSid.Value;
var vendorSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
var vendorIdentity = "*" + vendorSid.Value;
var logs = new List<string>();

try
{
    Directory.CreateDirectory(backupPath);
    Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
    Directory.CreateDirectory(unrelatedPath);
    Directory.CreateDirectory(secondGamePath);
    Directory.CreateDirectory(statusProbePath);
    File.WriteAllText(payloadPath, "payload");
    File.WriteAllText(unrelatedPayload, "unrelated");
    File.WriteAllText(secondPayloadPath, "second library payload");
    File.WriteAllText(secondManifestPath, "\"AppState\"\n{\n\t\"appid\"\t\t\"888888\"\n\t\"installdir\"\t\t\"Second Library Game\"\n}\n");
    File.WriteAllText(manifestPath, "\"AppState\"\n{\n\t\"appid\"\t\t\"424242\"\n\t\"installdir\"\t\t\"Fixture Game\"\n}\n");
    Directory.CreateDirectory(transitionPayloadPath);
    File.WriteAllText(transitionProbePath, "policy transition probe");
    for (var index = 0; index < 512; index++)
        File.WriteAllText(Path.Combine(transitionPayloadPath, $"load-{index:D4}.bin"), "load");

    Directory.CreateDirectory(restoreGameA);
    Directory.CreateDirectory(restoreGameB);
    File.WriteAllText(restoreManifestA, "\"AppState\"\n{\n\t\"appid\"\t\t\"1001\"\n\t\"installdir\"\t\t\"Restore A\"\n}\n");
    File.WriteAllText(restoreManifestB, "\"AppState\"\n{\n\t\"appid\"\t\t\"1002\"\n\t\"installdir\"\t\t\"Restore B\"\n}\n");

    var platform = new SteamPlatform(backupPath, logs.Add, logs.Add);
    var classifier = new Dictionary<string, bool>();

    var managerConfig = new Config();
    var steamConfig = managerConfig.GetPlatform("steam");
    steamConfig.LibraryPaths.Add(restoreLibrary);
    steamConfig.ProtectedGames.AddRange(new[] { "Restore A", "Restore B" });
    var manager = new PlatformManager(managerConfig, Path.Combine(fixtureRoot, "ManagerBackups"), logs.Add, logs.Add);
    var managedSteam = manager.Platforms.OfType<SteamPlatform>().Single();
    managedSteam.BackupManifests(restoreLibrary, new List<string>());
    File.Delete(restoreManifestA);
    File.Delete(restoreManifestB);
    // A normal protection refresh can run after Steam has removed a live manifest.
    // It must not garbage-collect the last known-good backup before recovery runs.
    managedSteam.BackupManifests(restoreLibrary, new List<string>());
    manager.RestoreGameManifests(new GameInfo
    {
        PlatformId = "steam",
        GameName = "Restore A",
        InstallPath = restoreGameA,
        ManifestPath = restoreManifestA,
        GameId = "1001"
    });
    var triggeringManifestRestored = File.Exists(restoreManifestA);
    var unrelatedManifestStayedAbsent = !File.Exists(restoreManifestB);
    manager.RestoreGameManifests(new GameInfo
    {
        PlatformId = "steam",
        GameName = "Restore B",
        InstallPath = restoreGameB,
        ManifestPath = restoreManifestB,
        GameId = "1002"
    });
    var missingSourceBackupRetained = File.Exists(restoreManifestB);
    File.Delete(restoreManifestB);
    const string reappearedManifest = "live patch replacement";
    File.WriteAllText(restoreManifestA, reappearedManifest);
    manager.RestoreGameManifests(new GameInfo
    {
        PlatformId = "steam",
        GameName = "Restore A",
        InstallPath = restoreGameA,
        ManifestPath = restoreManifestA,
        GameId = "1001"
    });
    var restore = new Dictionary<string, bool>
    {
        ["TriggeringManifestRestored"] = triggeringManifestRestored,
        ["UnrelatedMissingManifestNotRestored"] = unrelatedManifestStayedAbsent,
        ["MissingSourceBackupRetainedAcrossRefresh"] = missingSourceBackupRetained,
        ["ReappearedLiveManifestNotOverwritten"] =
            File.ReadAllText(restoreManifestA) == reappearedManifest
    };

    classifier["NoDownloadFolderIsStable"] = platform.IsGameStable(gamePath);

    Directory.CreateDirectory(correctDownload);
    var chunk = Path.Combine(correctDownload, "chunk.tmp");
    File.WriteAllText(chunk, "active");
    classifier["RecentCorrectAppIdIsUpdating"] = !platform.IsGameStable(gamePath);

    File.SetLastWriteTime(chunk, DateTime.Now.AddMinutes(-5));
    Directory.SetLastWriteTime(correctDownload, DateTime.Now);
    classifier["StaleFileWithFreshFolderMtimeIsStable"] = platform.IsGameStable(gamePath);

    var staleLogCount = logs.Count(line => line.Contains("stale downloading folder", StringComparison.Ordinal));
    _ = platform.IsGameStable(gamePath);
    classifier["StaleDiagnosticIsDeduplicated"] = staleLogCount == 1 &&
        logs.Count(line => line.Contains("stale downloading folder", StringComparison.Ordinal)) == 1;

    // Steam preallocates the complete staged file and can keep its timestamp unchanged
    // for longer than the 90-second freshness window while the download handle remains
    // open. The physical Cards! throttle reproduction exercised this exact shape.
    using (var activePreallocatedHandle = new FileStream(
        chunk, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        classifier["OldTimestampWithOpenDownloadFileIsUpdating"] =
            !platform.IsGameStable(gamePath);
    }

    File.Delete(chunk);
    classifier["EmptyCorrectAppIdFolderIsStable"] = platform.IsGameStable(gamePath);
    Directory.Delete(correctDownload);

    Directory.CreateDirectory(wrongDownload);
    File.WriteAllText(Path.Combine(wrongDownload, "recent.tmp"), "wrong app");
    classifier["RecentWrongAppIdIsIgnored"] = platform.IsGameStable(gamePath);

    File.Delete(payloadPath);
    classifier["EmptyGameWithoutCorrectDownloadFailsClosed"] = !platform.IsGameStable(gamePath);
    classifier["MissingGameDirectoryFailsClosed"] = !platform.IsGameStable(
        Path.Combine(fixtureRoot, "missing", "common", "Missing Game"));
    File.WriteAllText(payloadPath, "payload");

    RunIcacls(fixtureRoot, "/inheritance:r", "/grant:r",
        "*S-1-5-18:(OI)(CI)(F)", "*S-1-5-32-544:(OI)(CI)(F)", "/C", "/Q");

    var fixtureGame = new GameInfo
    {
        PlatformId = "steam",
        GameName = "Fixture Game",
        InstallPath = gamePath,
        ManifestPath = manifestPath,
        GameId = "424242"
    };
    var unrelatedGame = new GameInfo
    {
        PlatformId = "steam",
        GameName = "Unrelated Game",
        InstallPath = unrelatedPath,
        ManifestPath = Path.Combine(steamApps, "appmanifest_777777.acf"),
        GameId = "777777"
    };
    var secondLibraryGame = new GameInfo
    {
        PlatformId = "steam",
        GameName = "Second Library Game",
        InstallPath = secondGamePath,
        ManifestPath = secondManifestPath,
        GameId = "888888"
    };

    // A root-level Delete deny is insufficient on Windows when steamapps/common
    // grants DeleteChild. The physical r13 Cards! limited-user test proved that
    // the complete game root could still be renamed while every descendant was
    // correctly protected. The shared parent rule must be aggregated across
    // protected siblings and must participate in health verification.
    platform.ApplyProtection(fixtureGame, new List<string>(), blockAllNonAdminByDefault: true);
    var rootRuleApplied = HasDeleteDeny(GetExplicitRules(gamePath, usersSid));
    var parentRuleApplied = HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid));
    var completeContractReportedProtected = platform.CheckProtectionActive(gamePath);

    // Re-applying an already exact contract must not tear down and rebuild ACLs.
    // The physical r14 restart timeline observed two root/payload SDDL states while
    // the service re-applied an unchanged protected game, exposing a transient gap.
    logs.Clear();
    platform.ApplyProtection(fixtureGame, new List<string>(), blockAllNonAdminByDefault: true);
    var exactContractSkippedAclRefresh = logs.Any(line =>
        line.Contains("exact protection contract already active", StringComparison.OrdinalIgnoreCase));

    RunIcacls(commonPath, "/remove:d", usersIdentity, "/C", "/Q");
    var missingParentRejected = !platform.CheckProtectionActive(gamePath);

    platform.ApplyProtection(fixtureGame, new List<string>(), blockAllNonAdminByDefault: true);
    platform.ApplyProtection(unrelatedGame, new List<string>(), blockAllNonAdminByDefault: true);
    platform.RemoveProtection(fixtureGame, new List<string>());
    var parentRetainedForProtectedSibling = HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid));
    var unrelatedSiblingStillProtected = HasDeleteDeny(GetExplicitRules(unrelatedPath, usersSid));
    platform.RemoveProtection(unrelatedGame, new List<string>());
    var parentRemovedAfterLastProtectedSibling = !HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid));

    var rootBoundary = new Dictionary<string, bool>
    {
        ["RootDeleteDenyApplied"] = rootRuleApplied,
        ["SharedParentDeleteChildDenyApplied"] = parentRuleApplied,
        ["CompleteContractReportsProtected"] = completeContractReportedProtected,
        ["ExactContractSkipsAclRefresh"] = exactContractSkippedAclRefresh,
        ["MissingSharedParentBoundaryRejected"] = missingParentRejected,
        ["SharedParentBoundaryRetainedForProtectedSibling"] = parentRetainedForProtectedSibling,
        ["ProtectedSiblingRootRemainsProtected"] = unrelatedSiblingStillProtected,
        ["SharedParentBoundaryRemovedAfterLastProtectedSibling"] = parentRemovedAfterLastProtectedSibling
    };

    platform.ApplyProtection(fixtureGame, new List<string>(), blockAllNonAdminByDefault: true);
    platform.ApplyProtection(secondLibraryGame, new List<string>(), blockAllNonAdminByDefault: true);
    var bothLibraryBoundariesApplied =
        HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid)) &&
        HasDeleteChildDeny(GetExplicitRules(secondCommonPath, usersSid));
    platform.RemoveProtection(fixtureGame, new List<string>());
    var firstLibraryBoundaryRemovedIndependently =
        !HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid));
    var secondLibraryBoundaryAndRootRetained =
        HasDeleteChildDeny(GetExplicitRules(secondCommonPath, usersSid)) &&
        HasDeleteDeny(GetExplicitRules(secondGamePath, usersSid));
    platform.RemoveProtection(secondLibraryGame, new List<string>());
    var secondLibraryBoundaryRemovedAfterItsLastGame =
        !HasDeleteChildDeny(GetExplicitRules(secondCommonPath, usersSid));
    var libraryIsolation = new Dictionary<string, bool>
    {
        ["BothLibraryBoundariesApplied"] = bothLibraryBoundariesApplied,
        ["FirstLibraryBoundaryRemovedIndependently"] = firstLibraryBoundaryRemovedIndependently,
        ["SecondLibraryBoundaryAndRootRetained"] = secondLibraryBoundaryAndRootRetained,
        ["SecondLibraryBoundaryRemovedAfterItsLastGame"] = secondLibraryBoundaryRemovedAfterItsLastGame
    };

    // Steam manifests are launcher-owned writable state. Transition cleanup must remove
    // historical manifest denies for both obsolete and newly desired identities without
    // weakening the game tree's make-before-break protection.
    RunIcacls(manifestPath, "/deny", usersIdentity + ":(DE)", "/C", "/Q");
    RunIcacls(manifestPath, "/deny", vendorIdentity + ":(DE)", "/C", "/Q");
    platform.ApplyProtection(fixtureGame, new List<string> { vendorIdentity }, blockAllNonAdminByDefault: true);
    var specificIdentityRootApplied = HasDeleteDeny(GetExplicitRules(gamePath, vendorSid));
    var specificIdentityParentApplied = HasDeleteChildDeny(GetExplicitRules(commonPath, vendorSid));
    var defaultGroupNotRetainedInSpecificMode = !HasDeleteDeny(GetExplicitRules(gamePath, usersSid));
    var steamManifestDeniesRemovedDuringTransition =
        !HasDeleteDeny(GetExplicitRules(manifestPath, usersSid)) &&
        !HasDeleteDeny(GetExplicitRules(manifestPath, vendorSid));
    logs.Clear();
    platform.ApplyProtection(fixtureGame, new List<string> { vendorIdentity }, blockAllNonAdminByDefault: true);
    var specificExactContractSkippedAclRefresh = logs.Any(line =>
        line.Contains("exact protection contract already active", StringComparison.OrdinalIgnoreCase));
    // Authenticated Users is deliberately not a local profile name. Returning to the
    // default group must remove it by inspecting the actual ACL, not C:\Users names.
    platform.ApplyProtection(fixtureGame, new List<string>(), blockAllNonAdminByDefault: true);
    var obsoleteUnenumeratedIdentityRemovedOnReturnToGroup =
        HasDeleteDeny(GetExplicitRules(gamePath, usersSid)) &&
        HasDeleteChildDeny(GetExplicitRules(commonPath, usersSid)) &&
        !HasDeleteDeny(GetExplicitRules(gamePath, vendorSid)) &&
        !HasDeleteChildDeny(GetExplicitRules(commonPath, vendorSid));
    platform.RemoveProtection(fixtureGame, new List<string>());
    var specificIdentityRemoved =
        !HasDeleteDeny(GetExplicitRules(gamePath, vendorSid)) &&
        !HasDeleteChildDeny(GetExplicitRules(commonPath, vendorSid));
    var specificUserMode = new Dictionary<string, bool>
    {
        ["SpecificIdentityRootApplied"] = specificIdentityRootApplied,
        ["SpecificIdentityParentApplied"] = specificIdentityParentApplied,
        ["DefaultGroupNotRetainedInSpecificMode"] = defaultGroupNotRetainedInSpecificMode,
        ["SteamManifestDeniesRemovedDuringTransition"] = steamManifestDeniesRemovedDuringTransition,
        ["SpecificExactContractSkipsAclRefresh"] = specificExactContractSkippedAclRefresh,
        ["ObsoleteUnenumeratedIdentityRemovedOnReturnToGroup"] = obsoleteUnenumeratedIdentityRemovedOnReturnToGroup,
        ["SpecificIdentityRemoved"] = specificIdentityRemoved
    };

    // Protection must remain continuous when the selected identity changes. The physical
    // r15 Cards! test caught the blocked Standard-User deleting a protected probe between
    // removal of the old BUILTIN\Users rules and application of the new per-user rules.
    var transitionGame = new GameInfo
    {
        PlatformId = "steam",
        GameName = "Policy Transition Game",
        InstallPath = transitionGamePath,
        ManifestPath = "",
        GameId = "434343"
    };
    platform.ApplyProtection(transitionGame, new List<string>(), blockAllNonAdminByDefault: true);
    var currentGroups = identity.Groups?.Select(group => group.Value)
        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
    var transitionBaselineExact = platform.CheckProtectionActive(transitionGamePath) &&
        HasDeleteDeny(GetEffectiveRules(transitionProbePath, usersSid));
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
                var groupProtected = HasDeleteDeny(GetEffectiveRules(transitionProbePath, usersSid));
                var specificUserProtected = HasDeleteDeny(GetEffectiveRules(transitionProbePath, vendorSid));
                Interlocked.Increment(ref transitionSamples);
                if (!groupProtected && !specificUserProtected)
                    Interlocked.Exchange(ref transitionGapObserved, 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            Thread.Yield();
        }
    }) { IsBackground = true, Name = "SteamPolicyTransitionDeleteProbe" };
    transitionWorker.Start();
    transitionWorkerReady.Wait();
    Thread.Sleep(100);
    var samplesBeforeTransition = Volatile.Read(ref transitionSamples);
    platform.ApplyProtection(
        transitionGame, new List<string> { vendorIdentity }, blockAllNonAdminByDefault: true);
    Thread.Sleep(100);
    Volatile.Write(ref transitionStop, 1);
    transitionWorker.Join(TimeSpan.FromSeconds(5));
    var transitionSpecificContractExact = platform.CheckProtectionActive(transitionGamePath) &&
        HasDeleteDeny(GetExplicitRules(transitionGamePath, vendorSid)) &&
        HasDeleteChildDeny(GetExplicitRules(commonPath, vendorSid));
    var policyTransitionContinuity = new Dictionary<string, bool>
    {
        ["CurrentIdentityIsBuiltinUsersMember"] = currentGroups.Contains(usersSid.Value),
        ["CurrentIdentityIsAuthenticatedUser"] = currentGroups.Contains(vendorSid.Value),
        ["DefaultGroupBaselineWasExact"] = transitionBaselineExact,
        ["BoundarySamplerObservedProtectedBaseline"] = samplesBeforeTransition > 0,
        ["ProbeDeleteBoundaryContinuousDuringTransition"] = transitionGapObserved == 0,
        ["ProbeRemainedPresent"] = File.Exists(transitionProbePath),
        ["SpecificIdentityContractExactAfterTransition"] = transitionSpecificContractExact
    };
    platform.RemoveProtection(transitionGame, new List<string> { vendorIdentity });

    RunIcacls(statusProbePath, "/deny", usersIdentity + ":(R)", "/C", "/Q");
    classifier["UnrelatedReadDenyDoesNotReportProtected"] = !platform.CheckProtectionActive(statusProbePath);
    RunIcacls(statusProbePath, "/remove:d", usersIdentity, "/C", "/Q");
    RunIcacls(statusProbePath, "/deny", usersIdentity + ":(OI)(CI)(DE,DC)", "/C", "/Q");
    RunIcacls(commonPath, "/deny", usersIdentity + ":(DC)", "/C", "/Q");
    classifier["ExpectedDeleteContractReportsProtected"] = platform.CheckProtectionActive(statusProbePath);
    RunIcacls(commonPath, "/remove:d", usersIdentity, "/C", "/Q");
    RunIcacls(gamePath, "/grant:r", vendorIdentity + ":(OI)(CI)(F)", "/C", "/Q");
    RunIcacls(manifestPath, "/grant:r", vendorIdentity + ":(F)", "/C", "/Q");
    RunIcacls(gamePath, "/deny", usersIdentity + ":(OI)(CI)(DE,DC)", "/T", "/C", "/Q");
    RunIcacls(manifestPath, "/deny", usersIdentity + ":(DE)", "/C", "/Q");
    RunIcacls(unrelatedPath, "/deny", usersIdentity + ":(OI)(CI)(DE,DC)", "/T", "/C", "/Q");

    var before = new
    {
        GameDeny = HasDeleteDeny(GetExplicitRules(gamePath, usersSid)),
        ManifestDeny = HasDeleteDeny(GetExplicitRules(manifestPath, usersSid)),
        UnrelatedDeny = HasDeleteDeny(GetExplicitRules(unrelatedPath, usersSid)),
        GameVendorAllow = HasFullControlAllow(GetExplicitRules(gamePath, vendorSid)),
        ManifestVendorAllow = HasFullControlAllow(GetExplicitRules(manifestPath, vendorSid))
    };

    platform.ResetToDefault(new List<GameInfo>
    {
        new()
        {
            PlatformId = "steam",
            GameName = "Fixture Game",
            InstallPath = gamePath,
            ManifestPath = manifestPath,
            GameId = "424242"
        }
    });

    var lifecycle = new Dictionary<string, bool>
    {
        ["FixtureWasProtected"] = before.GameDeny && before.ManifestDeny && before.UnrelatedDeny,
        ["FixtureVendorAllowsExisted"] = before.GameVendorAllow && before.ManifestVendorAllow,
        ["GameDenyRemoved"] = !HasDeleteDeny(GetExplicitRules(gamePath, usersSid)),
        ["DescendantDenyRemoved"] = !HasDeleteDeny(GetExplicitRules(payloadPath, usersSid)),
        ["HistoricalManifestDenyRemoved"] = !HasDeleteDeny(GetExplicitRules(manifestPath, usersSid)),
        ["GameVendorAllowPreserved"] = HasFullControlAllow(GetExplicitRules(gamePath, vendorSid)),
        ["ManifestVendorAllowPreserved"] = HasFullControlAllow(GetExplicitRules(manifestPath, vendorSid)),
        ["UnrelatedSiblingDenyPreserved"] = HasDeleteDeny(GetExplicitRules(unrelatedPath, usersSid))
    };

    var passed = classifier.Values.All(value => value) &&
                 rootBoundary.Values.All(value => value) &&
                 libraryIsolation.Values.All(value => value) &&
                 specificUserMode.Values.All(value => value) &&
                 policyTransitionContinuity.Values.All(value => value) &&
                 lifecycle.Values.All(value => value) &&
                 restore.Values.All(value => value);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Test = "Steam update classifier and deny-only lifecycle cleanup",
        Passed = passed,
        Classifier = classifier,
        RootBoundary = rootBoundary,
        LibraryIsolation = libraryIsolation,
        SpecificUserMode = specificUserMode,
        PolicyTransitionContinuity = policyTransitionContinuity,
        Lifecycle = lifecycle,
        ExactRestore = restore,
        Logs = logs
    }, new JsonSerializerOptions { WriteIndented = true }));
    return passed ? 0 : 1;
}
finally
{
    if (Directory.Exists(fixtureRoot))
    {
        TryIcacls(commonPath, "/remove:d", usersIdentity, "/C", "/Q");
        TryIcacls(commonPath, "/remove:d", vendorIdentity, "/C", "/Q");
        TryIcacls(secondCommonPath, "/remove:d", usersIdentity, "/C", "/Q");
        TryIcacls(secondCommonPath, "/remove:d", vendorIdentity, "/C", "/Q");
        TryIcacls(fixtureRoot, "/remove:d", usersIdentity, "/T", "/C", "/Q");
        TryIcacls(fixtureRoot, "/remove:d", vendorIdentity, "/T", "/C", "/Q");
        try { Directory.Delete(fixtureRoot, recursive: true); } catch { }
    }
}

static List<FileSystemAccessRule> GetExplicitRules(string path, SecurityIdentifier sid)
{
    FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
    return security.GetAccessRules(true, false, typeof(SecurityIdentifier))
        .OfType<FileSystemAccessRule>()
        .Where(rule => rule.IdentityReference is SecurityIdentifier ruleSid && ruleSid.Value == sid.Value)
        .ToList();
}

static List<FileSystemAccessRule> GetEffectiveRules(string path, SecurityIdentifier sid)
{
    FileSystemSecurity security = Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
    return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .OfType<FileSystemAccessRule>()
        .Where(rule => rule.IdentityReference is SecurityIdentifier ruleSid && ruleSid.Value == sid.Value)
        .ToList();
}

static bool HasDeleteDeny(IEnumerable<FileSystemAccessRule> rules) => rules.Any(rule =>
    rule.AccessControlType == AccessControlType.Deny &&
    (rule.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0);

static bool HasDeleteChildDeny(IEnumerable<FileSystemAccessRule> rules) => rules.Any(rule =>
    rule.AccessControlType == AccessControlType.Deny &&
    (rule.FileSystemRights & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0);

static bool HasFullControlAllow(IEnumerable<FileSystemAccessRule> rules) => rules.Any(rule =>
    rule.AccessControlType == AccessControlType.Allow &&
    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

static void RunIcacls(params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "icacls.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start icacls.exe.");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"icacls failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
}

static void TryIcacls(params string[] arguments)
{
    try { RunIcacls(arguments); } catch { }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
