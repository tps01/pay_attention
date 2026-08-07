namespace PayAttention.Core.Services;

public static class RetentionService
{
    private static readonly string[] CaptureExtensions = [".png", ".jpg", ".jpeg", ".avi", ".gif", ".json"];

    public static int Enforce(string captureDirectory, int maxAgeDays, int maxFiles, AppLogger? logger = null)
    {
        if (!Directory.Exists(captureDirectory))
            return 0;

        var files = Directory.EnumerateFiles(captureDirectory)
            .Where(f => CaptureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        var deleted = 0;
        var cutoff = maxAgeDays > 0
            ? DateTime.UtcNow.AddDays(-maxAgeDays)
            : (DateTime?)null;

        foreach (var file in files.ToList())
        {
            var tooOld = cutoff.HasValue && file.CreationTimeUtc < cutoff.Value;
            if (!tooOld)
                continue;

            try
            {
                file.Delete();
                deleted++;
                files.Remove(file);
            }
            catch (Exception ex)
            {
                logger?.Warn($"Failed to delete old capture {file.Name}: {ex.Message}");
            }
        }

        if (maxFiles > 0 && files.Count > maxFiles)
        {
            foreach (var file in files.Skip(maxFiles).ToList())
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    logger?.Warn($"Failed to delete excess capture {file.Name}: {ex.Message}");
                }
            }
        }

        if (deleted > 0)
            logger?.Info($"Retention removed {deleted} file(s).");

        return deleted;
    }
}
