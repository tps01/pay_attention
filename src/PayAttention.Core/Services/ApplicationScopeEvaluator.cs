using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public static class ApplicationScopeEvaluator
{
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var n = name.Trim().Trim('"');
        // Browse-for-exe may yield a full path; matching uses the process base name.
        if (n.Contains('\\') || n.Contains('/'))
        {
            try { n = Path.GetFileName(n); }
            catch { /* keep trimmed input */ }
        }

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

    /// <param name="runningProcessNames">
    /// Optional snapshot of running process names (normalized or raw). Used only when
    /// <see cref="ApplicationScopeMode.Whitelisted"/> — skip OCR if none of the
    /// configured whitelist processes are running. Ignored for All / AllMinusBlacklisted.
    /// </param>
    public static bool ShouldSkipOcrCycle(
        AppConfig config,
        string? foregroundProcessName,
        IEnumerable<string>? runningProcessNames = null)
    {
        return config.ApplicationScope switch
        {
            // Capture is still PrintWindow of whitelist HWNDs (works while other apps have
            // focus). Skip when nothing is configured, or none of those processes are alive.
            ApplicationScopeMode.Whitelisted =>
                ShouldSkipWhitelistOcrCycle(config, runningProcessNames),
            ApplicationScopeMode.AllMinusBlacklisted =>
                IsFullBlacklistBlock(config, foregroundProcessName),
            _ => false
        };
    }

    private static bool ShouldSkipWhitelistOcrCycle(
        AppConfig config,
        IEnumerable<string>? runningProcessNames)
    {
        var whitelist = GetWhitelistProcessNames(config);
        if (whitelist.Count == 0)
            return true;

        // Caller did not supply a running-process snapshot — do not skip (legacy / tests).
        if (runningProcessNames is null)
            return false;

        var running = new HashSet<string>(
            runningProcessNames
                .Select(NormalizeProcessName)
                .Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in whitelist)
        {
            if (running.Contains(name))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<string> GetWhitelistProcessNames(AppConfig config)
    {
        if (config.ApplicationScope != ApplicationScopeMode.Whitelisted)
            return Array.Empty<string>();

        return (config.Applications ?? [])
            .Where(a => a.Enabled &&
                        a.ListMode == ApplicationListMode.Whitelist &&
                        !string.IsNullOrWhiteSpace(a.ProcessName))
            .Select(a => NormalizeProcessName(a.ProcessName))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsFullBlacklistBlock(AppConfig config, string? foregroundProcessName)
    {
        var row = FindMatchingRow(config, foregroundProcessName, ApplicationListMode.Blacklist);
        if (row is null)
            return false;
        return ResolveTriggerFilter(row) == ApplicationTriggerFilter.All;
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
            // Only the foreground whitelist app's Selected/All/None filter applies.
            // Do not fall back to the first whitelist row — that blocks other listed apps.
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
        {
            // Whitelist capture without a matching FG app: do not fire Selected/All rows
            // belonging to another listed process.
            if (config.ApplicationScope == ApplicationScopeMode.Whitelisted)
                return false;
            return true;
        }

        return ResolveTriggerFilter(row) switch
        {
            ApplicationTriggerFilter.All => row.ListMode == ApplicationListMode.Whitelist,
            ApplicationTriggerFilter.None => row.ListMode == ApplicationListMode.Blacklist,
            ApplicationTriggerFilter.Selected =>
                row.ListMode == ApplicationListMode.Whitelist
                    ? row.TriggerIds is { Count: > 0 } && row.TriggerIds.Contains(triggerId)
                    : row.TriggerIds is not { Count: > 0 } || !row.TriggerIds.Contains(triggerId),
            _ => true
        };
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

            // Legacy configs: non-empty ids without an explicit Selected filter.
            if (app.TriggerFilter == ApplicationTriggerFilter.All && app.TriggerIds.Count > 0)
                app.TriggerFilter = ApplicationTriggerFilter.Selected;

            if (app.TriggerFilter is ApplicationTriggerFilter.All or ApplicationTriggerFilter.None)
                app.TriggerIds.Clear();
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
            TriggerFilter = ApplicationTriggerFilter.All,
            TriggerIds = []
        }
    ];

    public static string FormatTriggersSummary(ApplicationConfig app, IEnumerable<TriggerConfig> triggers)
    {
        return ResolveTriggerFilter(app) switch
        {
            ApplicationTriggerFilter.None => "None",
            ApplicationTriggerFilter.All => "All",
            _ => FormatSelectedSummary(app, triggers)
        };
    }

    private static string FormatSelectedSummary(ApplicationConfig app, IEnumerable<TriggerConfig> triggers)
    {
        if (app.TriggerIds is null || app.TriggerIds.Count == 0)
            return "Selected";

        var byId = triggers.ToDictionary(t => t.Id, t => t.Text);
        var names = app.TriggerIds
            .Where(byId.ContainsKey)
            .Select(id => string.IsNullOrWhiteSpace(byId[id]) ? "(unnamed)" : byId[id])
            .ToList();

        if (names.Count == 0)
            return "Selected";
        if (names.Count <= 2)
            return string.Join(", ", names);
        return $"{names.Count} selected";
    }

    /// <summary>
    /// Treats Selected with no remaining ids as None for evaluation.
    /// </summary>
    public static ApplicationTriggerFilter ResolveTriggerFilter(ApplicationConfig app)
    {
        if (app.TriggerFilter == ApplicationTriggerFilter.Selected &&
            (app.TriggerIds is null || app.TriggerIds.Count == 0))
            return ApplicationTriggerFilter.None;
        return app.TriggerFilter;
    }
}
