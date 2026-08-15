using System;
using System.Collections.Generic;
using System.IO;
using ModSync.Utility;
using Newtonsoft.Json;

namespace ModSync.Updater;

public static class Updater
{
    /// <summary>
    /// Folders holding BASE-GAME files that mods replace rather than add to. Files here get
    /// backed up to .modsync-bak before being overwritten, and restored (never deleted) on
    /// removal - losing one of these bricks the client.
    ///   • Managed/          - Unity assemblies (DynamicMaps replaces two of them)
    ///   • Plugins/x86_64/   - native Unity plugins (Tarkov DLSS 4.5 replaces nvngx_dlss.dll)
    /// </summary>
    private static bool IsInProtectedBaseFolder(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("EscapeFromTarkov_Data/Managed/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("EscapeFromTarkov_Data/Plugins/x86_64/", StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveFilesRecursively(string source, string target) => MoveFilesRecursively(new DirectoryInfo(source), new DirectoryInfo(target));

    private static void MoveFilesRecursively(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var dir in source.GetDirectories())
            MoveFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
        foreach (var file in source.GetFiles())
        {
            var destPath = Path.Combine(target.FullName, file.Name);

            // Windows MAX_PATH: the source (under the staged update dir) is the longest path here,
            // and .NET on Windows still defers to the OS 260-char limit unless paths are
            // \\?\-prefixed. Apply the prefix to the actual filesystem ops so deep installs don't
            // throw DirectoryNotFoundException. (No-op on short paths / non-Windows.)
            var srcExt = LongPath.Extended(file.FullName);
            var destExt = LongPath.Extended(destPath);

            // Back up any existing base-game file before overwriting (Managed / Plugins.x86_64).
            if (File.Exists(destExt) && IsInProtectedBaseFolder(destPath))
            {
                Logger.Log($"Backing up: {destPath}");
                File.Copy(destExt, LongPath.Extended(destPath + ".modsync-bak"), overwrite: true);
            }

            Logger.Log($"Copying file: {destPath}");
            File.Move(srcExt, destExt, overwrite: true);
        }
    }

    public static void ReplaceUpdatedFiles()
    {
        if (!Directory.Exists(Program.UPDATE_DIR))
            return;

        MoveFilesRecursively(Program.UPDATE_DIR, Directory.GetCurrentDirectory());
        Logger.Log($"Deleting update directory: {Program.UPDATE_DIR}");
        Directory.Delete(Program.UPDATE_DIR, true);
    }

    public static void DeleteRemovedFiles()
    {
        if (!File.Exists(Program.REMOVED_FILES_PATH))
            return;

        var filesToDelete = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(Program.REMOVED_FILES_PATH)) ?? [];

        foreach (var file in filesToDelete)
        {
            if (Path.IsPathRooted(file))
                throw new Exception("[Corter-ModSync Updater]: Paths to delete cannot be absolute.");

            if (!Path.GetFullPath(file).StartsWith(Directory.GetCurrentDirectory()))
                throw new Exception("[Corter-ModSync Updater]: Path to delete is not relative to the current directory.");

            if (!File.Exists(file))
                continue;

            var bakPath = file + ".modsync-bak";
            if (IsInProtectedBaseFolder(file))
            {
                // A file here is only ever a REPLACEMENT for a base-game one, and we back the
                // original up at install time. So a backup means "restore what was here
                // before"; NO backup means we never installed this and it is base-game -
                // deleting it would brick the install. Leave it.
                if (!File.Exists(bakPath))
                {
                    Logger.Log($"Skipping delete of base-game file with no backup (leaving in place): {file}");
                    continue;
                }

                Logger.Log($"Restoring backup: {file}");
                File.Move(bakPath, file, overwrite: true);
            }
            else
            {
                Logger.Log($"Deleting file: {file}");
                File.Delete(file);
            }

            if (Directory.GetParent(file)!.GetFiles("*", SearchOption.AllDirectories).Length == 0)
            {
                Logger.Log($"Deleting directory: {Directory.GetParent(file)!.FullName}");
                Directory.GetParent(file)!.Delete();
            }
        }

        Logger.Log($"Deleting removed files list: {Program.REMOVED_FILES_PATH}");
        File.Delete(Program.REMOVED_FILES_PATH);
    }
}
