using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class ApplicationScopeTests
{
    [Fact]
    public void Defaults_include_PayAttention_blacklist_and_all_minus_blacklisted_scope()
    {
        Assert.Equal(ApplicationScopeMode.AllMinusBlacklisted, new AppConfig().ApplicationScope);

        var apps = ApplicationScopeEvaluator.CreateDefaultApplications();
        Assert.Single(apps);
        Assert.Equal("PayAttention", apps[0].ProcessName);
        Assert.Equal(ApplicationListMode.Blacklist, apps[0].ListMode);
        Assert.True(apps[0].Enabled);
        Assert.Empty(apps[0].TriggerIds);
        Assert.Equal(30, apps[0].DebounceSeconds);
    }

    [Fact]
    public void Normalize_prunes_deleted_trigger_ids_from_applications()
    {
        var keep = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var service = new ConfigService();
        var path = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var config = new AppConfig
            {
                CaptureDirectory = service.DefaultCaptureDirectory,
                Triggers =
                [
                    new TriggerConfig { Id = keep, Text = "keep-me", Enabled = true }
                ],
                Applications =
                [
                    new ApplicationConfig
                    {
                        ProcessName = "firefox",
                        ListMode = ApplicationListMode.Whitelist,
                        TriggerIds = [keep, removed]
                    }
                ]
            };

            service.ExportTo(path, config);
            var imported = service.ImportFrom(path);
            Assert.Single(imported.Applications[0].TriggerIds);
            Assert.Equal(keep, imported.Applications[0].TriggerIds[0]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void RemoveTriggerFromApplications_drops_id_from_all_rows()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var apps = new List<ApplicationConfig>
        {
            new() { ProcessName = "a", TriggerIds = [t1, t2] },
            new() { ProcessName = "b", TriggerIds = [t2] }
        };

        ApplicationScopeEvaluator.RemoveTriggerFromApplications(apps, t2);

        Assert.Equal([t1], apps[0].TriggerIds);
        Assert.Empty(apps[1].TriggerIds);
    }

    [Fact]
    public void Whitelist_scope_skips_when_foreground_not_listed()
    {
        var config = BaseConfig(ApplicationScopeMode.Whitelisted,
        [
            OnApp("firefox", ApplicationListMode.Whitelist)
        ]);

        Assert.True(ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, "chrome"));
        Assert.False(ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, "firefox.exe"));
    }

    [Fact]
    public void Blacklist_scope_skips_when_empty_trigger_ids_mean_all()
    {
        var config = BaseConfig(ApplicationScopeMode.AllMinusBlacklisted,
        [
            OnApp("PayAttention", ApplicationListMode.Blacklist)
        ]);

        Assert.True(ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, "PayAttention"));
        Assert.False(ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, "notepad"));
    }

    [Fact]
    public void Blacklist_partial_trigger_filter_does_not_skip_cycle_but_blocks_listed_triggers()
    {
        var blocked = Guid.NewGuid();
        var allowed = Guid.NewGuid();
        var config = BaseConfig(ApplicationScopeMode.AllMinusBlacklisted,
        [
            new ApplicationConfig
            {
                Enabled = true,
                ProcessName = "PayAttention",
                ListMode = ApplicationListMode.Blacklist,
                TriggerIds = [blocked]
            }
        ]);
        config.Triggers =
        [
            new TriggerConfig { Id = blocked, Text = "blocked" },
            new TriggerConfig { Id = allowed, Text = "allowed" }
        ];

        Assert.False(ApplicationScopeEvaluator.ShouldSkipOcrCycle(config, "PayAttention"));
        Assert.False(ApplicationScopeEvaluator.IsTriggerAllowed(config, "PayAttention", blocked));
        Assert.True(ApplicationScopeEvaluator.IsTriggerAllowed(config, "PayAttention", allowed));
    }

    [Fact]
    public void Whitelist_empty_trigger_ids_allows_all_triggers()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var config = BaseConfig(ApplicationScopeMode.Whitelisted,
        [
            OnApp("putty", ApplicationListMode.Whitelist)
        ]);
        config.Triggers =
        [
            new TriggerConfig { Id = t1, Text = "a" },
            new TriggerConfig { Id = t2, Text = "b" }
        ];

        Assert.True(ApplicationScopeEvaluator.IsTriggerAllowed(config, "putty", t1));
        Assert.True(ApplicationScopeEvaluator.IsTriggerAllowed(config, "putty", t2));
    }

    [Fact]
    public void Whitelist_nonempty_trigger_ids_allows_only_listed()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var config = BaseConfig(ApplicationScopeMode.Whitelisted,
        [
            new ApplicationConfig
            {
                Enabled = true,
                ProcessName = "mobaxterm",
                ListMode = ApplicationListMode.Whitelist,
                TriggerIds = [t1]
            }
        ]);

        Assert.True(ApplicationScopeEvaluator.IsTriggerAllowed(config, "MobaXterm", t1));
        Assert.False(ApplicationScopeEvaluator.IsTriggerAllowed(config, "MobaXterm", t2));
    }

    [Fact]
    public void Effective_debounce_uses_matching_application_row()
    {
        var config = BaseConfig(ApplicationScopeMode.AllMinusBlacklisted,
        [
            new ApplicationConfig
            {
                Enabled = true,
                ProcessName = "firefox",
                ListMode = ApplicationListMode.Whitelist,
                DebounceSeconds = 12
            }
        ]);
        config.DebounceSeconds = 30;

        Assert.Equal(12, ApplicationScopeEvaluator.GetEffectiveDebounceSeconds(config, "firefox"));
        Assert.Equal(30, ApplicationScopeEvaluator.GetEffectiveDebounceSeconds(config, "chrome"));
    }

    [Fact]
    public void Mask_list_includes_enabled_blacklist_processes_only_for_all_minus_blacklisted()
    {
        var config = BaseConfig(ApplicationScopeMode.AllMinusBlacklisted,
        [
            OnApp("PayAttention", ApplicationListMode.Blacklist),
            OnApp("firefox", ApplicationListMode.Whitelist),
            new ApplicationConfig
            {
                Enabled = false,
                ProcessName = "old",
                ListMode = ApplicationListMode.Blacklist
            }
        ]);

        var names = ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config);
        Assert.Equal(["PayAttention"], names);

        config.ApplicationScope = ApplicationScopeMode.All;
        Assert.Empty(ApplicationScopeEvaluator.GetBlacklistProcessNamesToMask(config));
    }

    [Fact]
    public void Export_import_round_trips_applications_and_scope()
    {
        var service = new ConfigService();
        var path = Path.Combine(Path.GetTempPath(), "pa_cfg_" + Guid.NewGuid().ToString("N") + ".json");
        var triggerId = Guid.NewGuid();

        try
        {
            var original = new AppConfig
            {
                CaptureDirectory = service.DefaultCaptureDirectory,
                ApplicationScope = ApplicationScopeMode.Whitelisted,
                DebounceSeconds = 15,
                Triggers =
                [
                    new TriggerConfig
                    {
                        Id = triggerId,
                        Text = "scopeTest",
                        Enabled = true
                    }
                ],
                Applications =
                [
                    new ApplicationConfig
                    {
                        ProcessName = "firefox.exe",
                        ListMode = ApplicationListMode.Whitelist,
                        DebounceSeconds = 45,
                        TriggerIds = [triggerId]
                    }
                ]
            };

            service.ExportTo(path, original);
            var json = File.ReadAllText(path);
            Assert.Contains("\"applicationScope\": \"whitelisted\"", json);
            Assert.Contains("\"applications\"", json);

            var imported = service.ImportFrom(path);
            Assert.Equal(ApplicationScopeMode.Whitelisted, imported.ApplicationScope);
            Assert.Single(imported.Applications);
            Assert.Equal("firefox", imported.Applications[0].ProcessName);
            Assert.Equal(ApplicationListMode.Whitelist, imported.Applications[0].ListMode);
            Assert.Equal(45, imported.Applications[0].DebounceSeconds);
            Assert.Equal([triggerId], imported.Applications[0].TriggerIds);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void FormatTriggersSummary_shows_All_or_names()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var triggers = new[]
        {
            new TriggerConfig { Id = t1, Text = "alpha" },
            new TriggerConfig { Id = t2, Text = "beta" }
        };

        var all = new ApplicationConfig { TriggerIds = [] };
        Assert.Equal("All", ApplicationScopeEvaluator.FormatTriggersSummary(all, triggers));

        var one = new ApplicationConfig { TriggerIds = [t1] };
        Assert.Equal("alpha", ApplicationScopeEvaluator.FormatTriggersSummary(one, triggers));
    }

    private static AppConfig BaseConfig(ApplicationScopeMode scope, List<ApplicationConfig> apps) =>
        new()
        {
            ApplicationScope = scope,
            Applications = apps,
            DebounceSeconds = 30
        };

    private static ApplicationConfig OnApp(string name, ApplicationListMode mode) =>
        new()
        {
            Enabled = true,
            ProcessName = name,
            ListMode = mode,
            DebounceSeconds = 30,
            TriggerIds = []
        };
}
