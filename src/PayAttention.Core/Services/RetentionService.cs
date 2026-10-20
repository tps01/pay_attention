namespace PayAttention.Core.Services;

public static class RetentionService
{
    private static readonly string[] CaptureExtensions = [".png", ".jpg", ".jpeg", ".avi", ".gif", ".json"];

    public static int Enforce(string captureDirectory, int maxAgeDays, int maxFiles, AppLogger? logger = null)
    {
        if (!Directory.Exists(captureDirectory))
            return 0;

        // Companion files that share a base name (still + video + sidecar) are one unit.
        // Each owned sequence folder is one unit.
        var units = new List<RetentionUnit>();

        var groups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(captureDirectory)
                     .Where(f => CaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
                                 CaptureOwnership.IsOwnedName(CaptureOwnership.FileStem(f))))
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    File.Exists(Path.Combine(captureDirectory, CaptureOwnership.FileStem(file) + ".pending")))
                    continue;
                // Still + video + sidecar share a base name and are one capture.
                var key = CaptureOwnership.FileStem(info.Name);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add(info);
            }
            catch
            {
                // skip inaccessible
            }
        }

        foreach (var group in groups.Values)
            units.Add(RetentionUnit.ForFiles(group));

        foreach (var dir in Directory.EnumerateDirectories(captureDirectory))
        {
            if (!CaptureOwnership.IsOwnedName(Path.GetFileName(dir)) ||
                !Path.GetFileName(dir).EndsWith("_sequence", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(dir, ".pending")))
                continue;
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && !ContainsReparsePoint(info.FullName))
                    units.Add(RetentionUnit.ForDirectory(info));
            }
            catch
            {
                // skip inaccessible
            }
        }

        units = units.OrderByDescending(u => u.CreatedUtc).ToList();

        var deleted = 0;
        var cutoff = maxAgeDays > 0
            ? DateTime.UtcNow.AddDays(-maxAgeDays)
            : (DateTime?)null;

        foreach (var unit in units.ToList())
        {
            var tooOld = cutoff.HasValue && unit.CreatedUtc < cutoff.Value;
            if (!tooOld)
                continue;

            if (TryDelete(unit, logger))
            {
                deleted++;
                units.Remove(unit);
            }
        }

        if (maxFiles > 0 && units.Count > maxFiles)
        {
            foreach (var unit in units.Skip(maxFiles).ToList())
            {
                if (TryDelete(unit, logger))
                    deleted++;
            }
        }

        if (deleted > 0)
            logger?.Info($"Retention removed {deleted} item(s).");

        return deleted;
    }

    private static bool TryDelete(RetentionUnit unit, AppLogger? logger)
    {
        var deletedAny = false;
        foreach (var path in unit.Paths)
        {
            try
            {
                if (unit.IsDirectory)
                {
                    // Never traverse a link in an owned sequence folder.
                    if (ContainsReparsePoint(path) || File.Exists(Path.Combine(path, ".pending")))
                        return false;
                    Directory.Delete(path, recursive: true);
                }
                else
                    File.Delete(path);
                deletedAny = true;
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to delete capture {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (!deletedAny)
            return false;

        // A leftover companion still counts until a later pass removes it.
        return unit.Paths.All(path => unit.IsDirectory ? !Directory.Exists(path) : !File.Exists(path));
    }

    private static bool ContainsReparsePoint(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(directory));
        while (pending.TryPop(out var path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        return false;
    }

    private sealed class RetentionUnit
    {
        public required IReadOnlyList<string> Paths { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required bool IsDirectory { get; init; }

        public static RetentionUnit ForFiles(IReadOnlyList<FileInfo> files) => new()
        {
            Paths = files.Select(f => f.FullName).ToList(),
            CreatedUtc = files.Max(f => f.CreationTimeUtc),
            IsDirectory = false
        };

        public static RetentionUnit ForDirectory(DirectoryInfo info) => new()
        {
            Paths = [info.FullName],
            CreatedUtc = info.CreationTimeUtc,
            IsDirectory = true
        };
    }
}
