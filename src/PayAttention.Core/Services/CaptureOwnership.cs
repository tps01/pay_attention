using System.Globalization;
using System.Text.RegularExpressions;

namespace PayAttention.Core.Services;

/// <summary>The reserved filename namespace used by this application's retention policy.</summary>
public static class CaptureOwnership
{
    private static readonly Regex OwnedName = new(
        @"^pa_\d{8}_\d{6}_\d{3}_[0-9a-f]{32}_.+_(screenshot|record|sequence)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string CreateName(DateTimeOffset timestamp, string label, string action, Guid eventId) =>
        $"pa_{timestamp.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{eventId:N}_{label}_{action}";

    public static bool IsOwnedName(string name) => OwnedName.IsMatch(name);

    public static string FileStem(string path) => path.EndsWith(".capture.json", StringComparison.OrdinalIgnoreCase)
        ? Path.GetFileName(path)[..^".capture.json".Length] : Path.GetFileNameWithoutExtension(path);
}
