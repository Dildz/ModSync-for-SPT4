using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using ModSync.Utility;
using Mono.Cecil;
using Newtonsoft.Json;

namespace ModSync.Patcher;

/// <summary>
/// BepInEx preloader patcher — runs before any plugin DLLs are loaded into memory.
///
/// Applies ModSync_Data/PendingUpdates to the game directory while almost no files are
/// locked, solving the "File has a user-mapped section" IOException that occurs when the
/// plugin tries to overwrite loaded DLLs in-process after downloading updates.
///
/// The one thing still locked at this stage is patcher DLLs themselves — BepInEx loads
/// every DLL in BepInEx/patchers/ into memory *before* running any patcher's Finish(),
/// so a restart never unlocks them. Windows won't let a loaded DLL be overwritten or
/// deleted, but it CAN be renamed: locked files are moved aside as "*.modsync-old" and
/// swept up by CleanupOldFiles() on the next boot.
///
/// Everything the patcher does is also appended to ModSync_Data/ModSync.log — the same
/// file the Windows Updater writes — giving headless setups a persistent log that
/// survives BepInEx overwriting LogOutput.log on every boot.
///
/// BepInEx 5.4.21+ discovers patchers via a static TargetDLLs *property* (get_TargetDLLs),
/// not the older static method. Finish() is the post-patching lifecycle hook.
/// </summary>
public static class Patcher
{
    private static readonly ManualLogSource Log = Logger.CreateLogSource("ModSync.Patcher");

    private static readonly string GameDir = Directory.GetCurrentDirectory();
    private static readonly string PendingUpdatesDir = Path.Combine(GameDir, "ModSync_Data", "PendingUpdates");
    private static readonly string RemovedFilesPath = Path.Combine(GameDir, "ModSync_Data", "RemovedFiles.json");
    private static readonly string LogFilePath = Path.Combine(GameDir, "ModSync_Data", "ModSync.log");

    // Suffix for locked DLLs renamed aside during apply/remove; deleted on the next boot.
    private const string OldFileSuffix = ".modsync-old";

    // Property (not method) — BepInEx 5.4.21+ looks for get_TargetDLLs via reflection.
    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

    // Required by BepInEx 5.x — a class with no Patch methods is silently skipped (Finish never fires).
    public static void Patch(AssemblyDefinition assembly) { }

    public static void Finish()
    {
        try
        {
            CleanupOldFiles();
            ApplyPending();
            RemoveDeleted();
        }
        catch (Exception e)
        {
            // Never let the patcher take down the BepInEx preloader — log and let the game boot.
            Warn($"Unexpected error: {e}");
        }
    }

    // True if the game-root-relative path is inside EscapeFromTarkov_Data/Managed/.
    private static bool IsInManagedFolder(string relPath) =>
        relPath.Replace('\\', '/').StartsWith("EscapeFromTarkov_Data/Managed/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes "*.modsync-old" files left behind by a previous boot's locked-file
    /// replacement. Only BepInEx/ and EscapeFromTarkov_Data/Managed/ can contain them
    /// (the sync path roots), so the whole game directory isn't walked.
    /// </summary>
    private static void CleanupOldFiles()
    {
        string[] roots = [Path.Combine(GameDir, "BepInEx"), Path.Combine(GameDir, "EscapeFromTarkov_Data", "Managed")];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var oldFile in Directory.EnumerateFiles(root, "*" + OldFileSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(oldFile);
                    Info($"Cleaned up {RelativeToGameDir(oldFile)}");
                }
                catch (Exception e)
                {
                    Warn($"Could not clean up {RelativeToGameDir(oldFile)}: {e.Message}");
                }
            }
        }
    }

    private static void ApplyPending()
    {
        if (!Directory.Exists(PendingUpdatesDir))
            return;

        var applied = 0;
        var alreadyCurrent = 0;
        List<string> failed = [];

        foreach (var src in Directory.EnumerateFiles(PendingUpdatesDir, "*", SearchOption.AllDirectories))
        {
            var rel = src.Substring(PendingUpdatesDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dest = Path.Combine(GameDir, rel);

            // net472 enforces the 260-char MAX_PATH, and `src` (under ModSync_Data\PendingUpdates\)
            // is the longest path in the whole apply pipeline. Hand every filesystem op the
            // \\?\-extended form so deep installs don't throw DirectoryNotFoundException. `rel`
            // stays raw — it's only used for logging and the IsInManagedFolder match.
            var srcExt = LongPath.Extended(src);
            var destExt = LongPath.Extended(dest);

            try
            {
                // Staged file already matches what's installed (e.g. leftovers of a
                // partially failed earlier run) — nothing to apply, just clear it.
                if (File.Exists(destExt) && FilesAreIdentical(srcExt, destExt))
                {
                    File.Delete(srcExt);
                    alreadyCurrent++;
                    continue;
                }

                Directory.CreateDirectory(LongPath.Extended(Path.GetDirectoryName(dest)));

                if (IsInManagedFolder(rel) && File.Exists(destExt))
                {
                    File.Copy(destExt, LongPath.Extended(dest + ".modsync-bak"), overwrite: true);
                    Info($"Backed up {rel}");
                }

                CopyReplacingLocked(srcExt, destExt);

                // Per-file cleanup: clear each staged file as soon as it's applied, so a
                // failure elsewhere can't cause this one to be re-applied every boot.
                File.Delete(srcExt);
                Info($"Applied {rel}");
                applied++;
            }
            catch (Exception e)
            {
                Warn($"Could not apply {rel}: {e.Message}");
                failed.Add(rel);
            }
        }

        PruneEmptyDirectories(PendingUpdatesDir);

        if (failed.Count == 0)
        {
            if (applied + alreadyCurrent > 0)
                Info($"All pending updates handled: {applied} applied, {alreadyCurrent} already up to date.");
        }
        else
        {
            Warn($"{applied} applied, {alreadyCurrent} already up to date, {failed.Count} failed (kept for next boot): {string.Join(", ", failed)}");
        }
    }

    private static void RemoveDeleted()
    {
        if (!File.Exists(RemovedFilesPath))
            return;

        List<string> toRemove;
        try
        {
            toRemove = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(RemovedFilesPath));
        }
        catch (Exception e)
        {
            Warn($"Could not read RemovedFiles.json: {e.Message}");
            return;
        }

        List<string> failed = [];

        foreach (var rel in toRemove)
        {
            try
            {
                var fullPath = LongPath.Extended(Path.Combine(GameDir, rel));
                if (!File.Exists(fullPath))
                    continue;

                var bakPath = fullPath + ".modsync-bak";
                if (IsInManagedFolder(rel) && File.Exists(bakPath))
                {
                    CopyReplacingLocked(bakPath, fullPath);
                    File.Delete(bakPath);
                    Info($"Restored {rel}");
                }
                else
                {
                    DeleteEvenIfLocked(fullPath);
                    Info($"Removed {rel}");
                }
            }
            catch (Exception e)
            {
                // Per-file fault tolerance — one bad entry must not abort the rest.
                Warn($"Could not remove {rel}: {e.Message}");
                failed.Add(rel);
            }
        }

        // Only genuinely failed entries are retried next boot.
        if (failed.Count == 0)
            File.Delete(RemovedFilesPath);
        else
            File.WriteAllText(RemovedFilesPath, JsonConvert.SerializeObject(failed));
    }

    /// <summary>
    /// File.Copy(overwrite: true) onto a DLL that's loaded in memory throws
    /// ("file has a user-mapped section"). A loaded DLL can't be overwritten or deleted —
    /// but it CAN be renamed. So: move the locked file aside and copy fresh. This is how
    /// the patcher updates itself and sibling patcher DLLs, which BepInEx always loads
    /// before running Finish().
    /// </summary>
    private static void CopyReplacingLocked(string src, string dest)
    {
        try
        {
            File.Copy(src, dest, overwrite: true);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(dest))
        {
            var oldPath = dest + OldFileSuffix;
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(dest, oldPath);
            File.Copy(src, dest);
            Info($"{Path.GetFileName(dest)} was in use — old copy moved aside, cleaned up next boot.");
        }
    }

    // Same trick for deletion: renaming gets a loaded DLL out of BepInEx's way
    // immediately; the actual delete happens in CleanupOldFiles() next boot.
    private static void DeleteEvenIfLocked(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var oldPath = path + OldFileSuffix;
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(path, oldPath);
        }
    }

    // Byte-for-byte comparison; the length check short-circuits most mismatches.
    private static bool FilesAreIdentical(string pathA, string pathB)
    {
        if (new FileInfo(pathA).Length != new FileInfo(pathB).Length)
            return false;

        using var streamA = File.OpenRead(pathA);
        using var streamB = File.OpenRead(pathB);

        var bufferA = new byte[81920];
        var bufferB = new byte[81920];

        // Stream.Read may return fewer bytes than asked — loop until the buffer is full
        // or the stream ends, otherwise equal files could compare as different.
        static int FillBuffer(Stream stream, byte[] buffer)
        {
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            return total;
        }

        while (true)
        {
            var readA = FillBuffer(streamA, bufferA);
            var readB = FillBuffer(streamB, bufferB);

            if (readA != readB)
                return false;
            if (readA == 0)
                return true;

            for (var i = 0; i < readA; i++)
                if (bufferA[i] != bufferB[i])
                    return false;
        }
    }

    /// <summary>
    /// Depth-first delete of now-empty folders, including <paramref name="dir"/> itself.
    /// The plugin treats the existence of the PendingUpdates folder as "there's an
    /// unapplied update", so an empty husk left behind would trigger a false warning
    /// on every boot.
    /// </summary>
    private static void PruneEmptyDirectories(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var sub in Directory.GetDirectories(dir))
            PruneEmptyDirectories(sub);

        if (Directory.GetFileSystemEntries(dir).Length == 0)
            Directory.Delete(dir);
    }

    private static string RelativeToGameDir(string fullPath) =>
        fullPath.Substring(GameDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void Info(string message)
    {
        Log.LogInfo(message);
        AppendToLogFile(message);
    }

    private static void Warn(string message)
    {
        Log.LogWarning(message);
        AppendToLogFile("WARNING: " + message);
    }

    private static bool logFileStarted;

    /// <summary>
    /// Mirrors a line into ModSync_Data/ModSync.log — the same file the Windows Updater
    /// writes — so the patcher's work is diagnosable on headless, where BepInEx
    /// overwrites LogOutput.log on every boot. Only written when the patcher actually
    /// does something; a quiet boot adds nothing.
    /// </summary>
    private static void AppendToLogFile(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath));

            if (!logFileStarted)
            {
                File.AppendAllText(LogFilePath, $"--- [Corter-ModSync Patcher] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
                logFileStarted = true;
            }

            File.AppendAllText(LogFilePath, $"[Corter-ModSync Patcher]: {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the update itself.
        }
    }
}
