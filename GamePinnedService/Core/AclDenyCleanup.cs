using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// Discovers explicit deny ACE identities without changing the vendor's allow ACLs.
    /// Callers perform the actual mutation through icacls /remove:d, which is the proven
    /// GamePinned ACL mutation path.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static class AclDenyCleanup
    {
        private const FileSystemRights GamePinnedDeleteRights =
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

        public static List<string> DiscoverExplicitDeleteDenyIdentities(
            string path,
            bool recursive,
            Action<string> debugLogger)
        {
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(path) && !File.Exists(path))
                return identities.ToList();

            InspectPath(path, identities, debugLogger);
            if (!recursive || !Directory.Exists(path))
                return identities.ToList();

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(directory).GetFileSystemInfos();
                }
                catch (Exception ex)
                {
                    debugLogger($"[ACL cleanup] Could not enumerate {directory}: {ex.Message}");
                    continue;
                }

                foreach (var entry in entries)
                {
                    InspectPath(entry.FullName, identities, debugLogger);
                    if ((entry.Attributes & FileAttributes.Directory) != 0 &&
                        (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }

            return identities.OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void InspectPath(
            string path,
            HashSet<string> identities,
            Action<string> debugLogger)
        {
            try
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

                foreach (var rule in security
                    .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>())
                {
                    if (rule.AccessControlType == AccessControlType.Deny &&
                        (rule.FileSystemRights & GamePinnedDeleteRights) != 0 &&
                        rule.IdentityReference is SecurityIdentifier sid)
                    {
                        identities.Add("*" + sid.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                debugLogger($"[ACL cleanup] Could not inspect {path}: {ex.Message}");
            }
        }
    }
}
