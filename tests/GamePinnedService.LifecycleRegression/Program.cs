using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GamePinned.Service.Platforms;
using GamePinned.Shared.Models;

if (!OperatingSystem.IsWindows())
    return Fail("This regression requires Windows.");

using var identity = WindowsIdentity.GetCurrent();
var principal = new WindowsPrincipal(identity);
if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
    return Fail("Run this regression from an elevated process.");

var fixtureRoot = Path.Combine(Path.GetTempPath(), "GamePinned-EpicOrphan-" + Guid.NewGuid().ToString("N"));
var backupPath = Path.Combine(fixtureRoot, "backups", "epic");
var epicRoot = Path.Combine(fixtureRoot, "Epic Games");
var orphanPath = Path.Combine(epicRoot, "OrphanGame");
var unrelatedPath = Path.Combine(epicRoot, "UnrelatedSibling");
var orphanPayload = Path.Combine(orphanPath, "Payload", "game.bin");
var unrelatedPayload = Path.Combine(unrelatedPath, "keep.bin");
var transitionPath = Path.Combine(epicRoot, "PolicyTransitionGame");
var transitionPayloadPath = Path.Combine(transitionPath, "Payload");
var transitionProbePath = Path.Combine(transitionPayloadPath, "policy-transition-probe.bin");
var transitionManifestParent = Path.Combine(fixtureRoot, "Manifests");
var transitionManifestPath = Path.Combine(transitionManifestParent, "policy-transition.item");
var classifierGame = Path.Combine(fixtureRoot, "ClassifierGame");
var freshClassifierGame = Path.Combine(fixtureRoot, "FreshClassifierGame");
var backingStorePath = Path.Combine(classifierGame, ".egstore", "bps", "c", "backingstore");
var resumeDataPath = Path.Combine(classifierGame, ".egstore", "bps", "m", "$resumeData");
var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
var usersIdentity = "*" + usersSid.Value;
var vendorAllowSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
var vendorAllowIdentity = "*" + vendorAllowSid.Value;

try
{
    Directory.CreateDirectory(backupPath);
    Directory.CreateDirectory(Path.GetDirectoryName(orphanPayload)!);
    Directory.CreateDirectory(unrelatedPath);
    Directory.CreateDirectory(Path.GetDirectoryName(backingStorePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(resumeDataPath)!);
    Directory.CreateDirectory(transitionPayloadPath);
    Directory.CreateDirectory(transitionManifestParent);
    File.WriteAllText(orphanPayload, "orphan payload");
    File.WriteAllText(unrelatedPayload, "unrelated payload");
    File.WriteAllText(transitionProbePath, "policy transition probe");
    File.WriteAllText(transitionManifestPath, "policy transition manifest");
    for (var index = 0; index < 512; index++)
        File.WriteAllText(Path.Combine(transitionPayloadPath, $"load-{index:D4}.bin"), "load");

    var logs = new List<string>();
    var platform = new EpicPlatform(backupPath, logs.Add, logs.Add);
    var classifier = new Dictionary<string, bool>();
    Directory.SetCreationTime(classifierGame, DateTime.Now.AddMinutes(-10));
    classifier["OldInstallWithoutBackingStoreIsStable"] = platform.IsGameStable(classifierGame);

    File.WriteAllText(resumeDataPath, "uninstall state");
    classifier["RecentResumeDataOnlyIsStable"] = platform.IsGameStable(classifierGame);
    File.WriteAllText(Path.Combine(classifierGame, ".egstore", ".lock"), "uninstall lock");
    classifier["UnrelatedRecentEgstoreFilesAreStable"] = platform.IsGameStable(classifierGame);

    File.WriteAllText(backingStorePath, "active update");
    classifier["RecentBackingStoreFileIsUpdating"] = !platform.IsGameStable(classifierGame);
    File.SetCreationTime(backingStorePath, DateTime.Now.AddMinutes(-2));
    File.SetLastWriteTime(backingStorePath, DateTime.Now.AddMinutes(-2));
    classifier["StaleBackingStoreFileIsStable"] = platform.IsGameStable(classifierGame);

    File.Delete(backingStorePath);
    Directory.CreateDirectory(backingStorePath);
    var stagedChunk = Path.Combine(backingStorePath, "chunk.tmp");
    File.WriteAllText(stagedChunk, "active update");
    classifier["RecentBackingStoreDirectoryChildIsUpdating"] = !platform.IsGameStable(classifierGame);
    File.SetCreationTime(stagedChunk, DateTime.Now.AddMinutes(-2));
    File.SetLastWriteTime(stagedChunk, DateTime.Now.AddMinutes(-2));
    classifier["StaleBackingStoreDirectoryIsStable"] = platform.IsGameStable(classifierGame);

    Directory.CreateDirectory(freshClassifierGame);
    classifier["FreshInstallIsUpdating"] = !platform.IsGameStable(freshClassifierGame);
    classifier["MissingInstallFailsClosedAsStable"] = platform.IsGameStable(
        Path.Combine(fixtureRoot, "MissingClassifierGame"));

    RunIcacls(fixtureRoot, "/inheritance:r", "/grant:r",
        "*S-1-5-18:(OI)(CI)(F)", "*S-1-5-32-544:(OI)(CI)(F)", "/C", "/Q");

    var transitionGame = new GameInfo
    {
        PlatformId = "epic",
        GameName = "Policy Transition Game",
        InstallPath = transitionPath,
        ManifestPath = transitionManifestPath,
        GameId = "policy-transition"
    };
    platform.ApplyProtection(transitionGame, new List<string>(), blockAllNonAdminByDefault: true);
    var transitionBaselineExact =
        HasDeleteDeny(GetExplicitRules(transitionPath, usersSid)) &&
        HasDeleteDeny(GetEffectiveRules(transitionProbePath, usersSid)) &&
        HasDeleteDeny(GetExplicitRules(transitionManifestPath, usersSid));
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
                var payloadGroupProtected = HasDeleteDeny(GetEffectiveRules(transitionProbePath, usersSid));
                var payloadUserProtected = HasDeleteDeny(GetEffectiveRules(transitionProbePath, vendorAllowSid));
                var manifestGroupProtected = HasDeleteDeny(GetEffectiveRules(transitionManifestPath, usersSid));
                var manifestUserProtected = HasDeleteDeny(GetEffectiveRules(transitionManifestPath, vendorAllowSid));
                Interlocked.Increment(ref transitionSamples);
                if ((!payloadGroupProtected && !payloadUserProtected) ||
                    (!manifestGroupProtected && !manifestUserProtected))
                    Interlocked.Exchange(ref transitionGapObserved, 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            Thread.Yield();
        }
    }) { IsBackground = true, Name = "EpicPolicyTransitionAclProbe" };
    transitionWorker.Start();
    transitionWorkerReady.Wait();
    Thread.Sleep(100);
    var samplesBeforeTransition = Volatile.Read(ref transitionSamples);
    platform.ApplyProtection(
        transitionGame, new List<string> { vendorAllowIdentity }, blockAllNonAdminByDefault: true);
    Thread.Sleep(100);
    Volatile.Write(ref transitionStop, 1);
    transitionWorker.Join(TimeSpan.FromSeconds(5));
    var specificRootContractApplied = HasDeleteDeny(GetExplicitRules(transitionPath, vendorAllowSid));
    var specificPayloadContractApplied = HasDeleteDeny(GetEffectiveRules(transitionProbePath, vendorAllowSid));
    var specificManifestContractApplied = HasDeleteDeny(GetExplicitRules(transitionManifestPath, vendorAllowSid));
    var obsoleteGroupRootRemoved = !HasDeleteDeny(GetExplicitRules(transitionPath, usersSid));
    platform.ApplyProtection(transitionGame, new List<string>(), blockAllNonAdminByDefault: true);
    var obsoleteUnenumeratedIdentityRemovedOnReturnToGroup =
        HasDeleteDeny(GetExplicitRules(transitionPath, usersSid)) &&
        HasDeleteDeny(GetEffectiveRules(transitionProbePath, usersSid)) &&
        HasDeleteDeny(GetExplicitRules(transitionManifestPath, usersSid)) &&
        !HasDeleteDeny(GetExplicitRules(transitionPath, vendorAllowSid)) &&
        !HasDeleteDeny(GetEffectiveRules(transitionProbePath, vendorAllowSid)) &&
        !HasDeleteDeny(GetExplicitRules(transitionManifestPath, vendorAllowSid));
    var policyTransition = new Dictionary<string, bool>
    {
        ["DefaultGroupBaselineWasExact"] = transitionBaselineExact,
        ["BoundarySamplerObservedProtectedBaseline"] = samplesBeforeTransition > 0,
        ["PayloadAndManifestContinuousDuringTransition"] = transitionGapObserved == 0,
        ["SpecificRootContractApplied"] = specificRootContractApplied,
        ["SpecificPayloadContractApplied"] = specificPayloadContractApplied,
        ["SpecificManifestContractApplied"] = specificManifestContractApplied,
        ["ObsoleteGroupRootRemoved"] = obsoleteGroupRootRemoved,
        ["ObsoleteUnenumeratedIdentityRemovedOnReturnToGroup"] = obsoleteUnenumeratedIdentityRemovedOnReturnToGroup
    };
    platform.RemoveProtection(transitionGame, new List<string>());

    RunIcacls(orphanPath, "/grant:r", vendorAllowIdentity + ":(OI)(CI)(F)", "/C", "/Q");
    RunIcacls(orphanPath, "/deny", usersIdentity + ":(OI)(CI)(DE,DC)", "/T", "/C", "/Q");
    RunIcacls(unrelatedPath, "/deny", usersIdentity + ":(OI)(CI)(DE,DC)", "/T", "/C", "/Q");

    var backupManifest = Path.Combine(backupPath, "orphan.item");
    File.WriteAllText(backupManifest, JsonSerializer.Serialize(new
    {
        DisplayName = "Orphan Game",
        AppName = "orphan",
        InstallLocation = orphanPath,
        LaunchExecutable = "Payload\\game.exe"
    }));

    var beforeOrphan = GetExplicitRules(orphanPath, usersSid);
    var beforeVendorAllow = GetExplicitRules(orphanPath, vendorAllowSid);
    var beforeUnrelated = GetExplicitRules(unrelatedPath, usersSid);
    if (!HasDeleteDeny(beforeOrphan) || !HasDeleteDeny(beforeUnrelated) || !HasFullControlAllow(beforeVendorAllow))
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            Error = "Fixture setup did not produce the required historical deny and vendor allow rules.",
            OrphanRules = DescribeRules(beforeOrphan),
            VendorAllowRules = DescribeRules(beforeVendorAllow),
            UnrelatedRules = DescribeRules(beforeUnrelated)
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 1;
    }

    platform.ResetToDefault(new List<GameInfo>());

    var afterOrphan = GetExplicitRules(orphanPath, usersSid);
    var afterVendorAllow = GetExplicitRules(orphanPath, vendorAllowSid);
    var afterPayload = GetExplicitRules(orphanPayload, usersSid);
    var afterUnrelated = GetExplicitRules(unrelatedPath, usersSid);
    var lifecyclePassed = !HasDeleteDeny(afterOrphan) &&
                 !HasDeleteDeny(afterPayload) &&
                 HasFullControlAllow(afterVendorAllow) &&
                 HasDeleteDeny(afterUnrelated) &&
                 logs.Any(line => line.Contains("recovered orphan path from backup manifest", StringComparison.Ordinal));
    var passed = classifier.Values.All(value => value) &&
                 policyTransition.Values.All(value => value) &&
                 lifecyclePassed;

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Test = "Epic activity classifier and backup-only orphan lifecycle cleanup",
        Passed = passed,
        Classifier = classifier,
        PolicyTransitionContinuity = policyTransition,
        LiveManifestPresent = false,
        BackupManifest = backupManifest,
        OrphanDeleteDenyBefore = HasDeleteDeny(beforeOrphan),
        OrphanDeleteDenyAfter = HasDeleteDeny(afterOrphan),
        DescendantDeleteDenyAfter = HasDeleteDeny(afterPayload),
        VendorFullControlAllowPreserved = HasFullControlAllow(afterVendorAllow),
        UnrelatedSiblingDenyPreserved = HasDeleteDeny(afterUnrelated),
        Logs = logs
    }, new JsonSerializerOptions { WriteIndented = true }));
    return passed ? 0 : 1;
}
finally
{
    if (Directory.Exists(fixtureRoot))
    {
        TryIcacls(fixtureRoot, "/remove:d", usersIdentity, "/T", "/C", "/Q");
        TryIcacls(fixtureRoot, "/remove:d", vendorAllowIdentity, "/T", "/C", "/Q");
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

static bool HasFullControlAllow(IEnumerable<FileSystemAccessRule> rules) => rules.Any(rule =>
    rule.AccessControlType == AccessControlType.Allow &&
    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

static object[] DescribeRules(IEnumerable<FileSystemAccessRule> rules) => rules.Select(rule => new
{
    Type = rule.AccessControlType.ToString(),
    Rights = rule.FileSystemRights.ToString(),
    Inheritance = rule.InheritanceFlags.ToString(),
    Propagation = rule.PropagationFlags.ToString(),
    IsInherited = rule.IsInherited
}).Cast<object>().ToArray();

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
