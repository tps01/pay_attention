namespace PayAttention.Core.Services;

public static class RetentionService
{
    private static readonly string[] CaptureExtensions = [".png", ".jpg", ".jpeg", ".avi", ".gif", ".json"];

    public static int Enforce(string captureDirectory, int maxAgeDays, int maxFiles, AppLogger? logger = null)
    {
        if (!Directory.Exists(captureDirectory))
            return 0;

        // Top-level capture files + per-event *_sequence folders (each folder is one unit).
        var units = new List<RetentionUnit>();

        foreach (var file in Directory.EnumerateFiles(captureDirectory)
                     .Where(f => CaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                    units.Add(RetentionUnit.ForFile(info));
            }
            catch
            {
                // skip inaccessible
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(captureDirectory))
        {
            if (!Path.GetFileName(dir).EndsWith("_sequence", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists)
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
        try
        {
            if (unit.IsDirectory)
                Directory.Delete(unit.Path, recursive: true);
            else
                File.Delete(unit.Path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Failed to delete capture {Path.GetFileName(unit.Path)}: {ex.Message}");
            return false;
        }
    }

    private sealed class RetentionUnit
    {
        public required string Path { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required bool IsDirectory { get; init; }

        public static RetentionUnit ForFile(FileInfo info) => new()
        {
            Path = info.FullName,
            CreatedUtc = info.CreationTimeUtc,
            IsDirectory = false
        };

        public static RetentionUnit ForDirectory(DirectoryInfo info) => new()
        {
            Path = info.FullName,
            CreatedUtc = info.CreationTimeUtc,
            IsDirectory = true
        };
    }
}
