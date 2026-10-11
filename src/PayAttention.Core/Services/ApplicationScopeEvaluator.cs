using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public static class ApplicationScopeEvaluator
{
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n;
    }

    public static bool ProcessNamesMatch(string? a, string? b) =>
        string.Equals(NormalizeProcessName(a), NormalizeProcessName(b), StringComparison.OrdinalIgnoreCase);

    public static ApplicationConfig? FindMatchingRow(
        AppConfig config,
        string? foregroundProcessName,
        ApplicationListMode? modeFilter = null)
    {
        var fg = NormalizeProcessName(foregroundProcessName);
        if (fg.Length == 0)
            return null;

        foreach (var app in config.Applications ?? [])
        {
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.ProcessName))
                continue;
            if (modeFilter is not null && app.ListMode != modeFilter)
                continue;
            if (ProcessNamesMatch(app.ProcessName, fg))
                return app;
        }

        return null;
    }

    public static bool ShouldSkipOcrCycle(AppConfig config, string? foregroundProcessName)
    {
        return config.ApplicationScope switch
        {
            ApplicationScopeMode.Whitelisted =>
                FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Whitelist) is null,
            ApplicationScopeMode.AllMinusBlacklisted =>
                IsFullBlacklistBlock(config, foregroundProcessName),
            _ => false
        };
    }

    public static bool IsFullBlacklistBlock(AppConfig config, string? foregroundProcessName)
    {
        var row = FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Blacklist);
        if (row is null)
            return false;
        return row.TriggerIds is null || row.TriggerIds.Count == 0;
    }

    public static IReadOnlyList<string> GetBlacklistProcessNamesToMask(AppConfig config)
    {
        if (config.ApplicationScope != ApplicationScopeMode.AllMinusBlacklisted)
            return Array.Empty<string>();

        return (config.Applications ?? [])
            .Where(a => a.Enabled &&
                        a.ListMode == ApplicationListMode.Blacklist &&
                        !string.IsNullOrWhiteSpace(a.ProcessName))
            .Select(a => NormalizeProcessName(a.ProcessName))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ApplicationConfig? FindRelevantRow(AppConfig config, string? foregroundProcessName)
    {
        return config.ApplicationScope switch
        {
            ApplicationScopeMode.Whitelisted =>
                FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Whitelist),
            ApplicationScopeMode.AllMinusBlacklisted =>
                FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Blacklist)
                ?? FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Whitelist),
            _ => FindMatchingRow(config, foregroundProcessName)
        };
    }

    public static bool IsTriggerAllowed(AppConfig config, string? foregroundProcessName, Guid triggerId)
    {
        var row = FindRelevantRow(config, foregroundProcessName);
        if (row is null)
            return true;

        var hasFilter = row.TriggerIds is { Count: > 0 };
        if (row.ListMode == ApplicationListMode.Whitelist)
            return !hasFilter || row.TriggerIds!.Contains(triggerId);

        // Blacklist: empty = suppress all; non-empty = suppress listed ids only
        return hasFilter && !row.TriggerIds!.Contains(triggerId);
    }

    public static int GetEffectiveDebounceSeconds(AppConfig config, string? foregroundProcessName)
    {
        var row = FindRelevantRow(config, foregroundProcessName);
        return row?.DebounceSeconds ?? config.DebounceSeconds;
    }

    public static void PruneTriggerIds(AppConfig config)
    {
        var valid = (config.Triggers ?? []).Select(t => t.Id).ToHashSet();
        foreach (var app in config.Applications ?? [])
        {
            app.TriggerIds ??= [];
            app.TriggerIds.RemoveAll(id => !valid.Contains(id));
        }
    }

    public static void RemoveTriggerFromApplications(IEnumerable<ApplicationConfig> applications, Guid triggerId)
    {
        foreach (var app in applications)
            app.TriggerIds?.RemoveAll(id => id == triggerId);
    }

    public static List<ApplicationConfig> CreateDefaultApplications() =>
    [
        new ApplicationConfig
        {
            Enabled = true,
            ProcessName = "PayAttention",
            ListMode = ApplicationListMode.Blacklist,
            DebounceSeconds = 30,
            TriggerIds = []
        }
    ];

    public static string FormatTriggersSummary(ApplicationConfig app, IEnumerable<TriggerConfig> triggers)
    {
        if (app.TriggerIds is null || app.TriggerIds.Count == 0)
            return "All";

        var byId = triggers.ToDictionary(t => t.Id, t => t.Text);
        var names = app.TriggerIds
            .Where(byId.ContainsKey)
            .Select(id => string.IsNullOrWhiteSpace(byId[id]) ? "(unnamed)" : byId[id])
            .ToList();

        if (names.Count == 0)
            return "All";
        if (names.Count <= 2)
            return string.Join(", ", names);
        return $"{names.Count} selected";
    }
}
