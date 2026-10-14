using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public class TriggerFireCoalescerTests
{
    private static TriggerMatchResult Match(string text, CaptureAction action, Guid? id = null) =>
        new()
        {
            Trigger = new TriggerConfig
            {
                Id = id ?? Guid.NewGuid(),
                Text = text,
                Action = action,
                Enabled = true
            },
            MatchedPortion = text
        };

    [Fact]
    public void SelectFires_keeps_every_distinct_phrase()
    {
        var matches = new[]
        {
            Match("error", CaptureAction.Screenshot),
            Match("warning", CaptureAction.Screenshot),
            Match("fatal", CaptureAction.Record)
        };
        var claimed = new List<string>();

        var fires = TriggerFireCoalescer.SelectFires(matches, m =>
        {
            claimed.Add(m.Trigger.Text);
            return true;
        });

        Assert.Equal(3, fires.Count);
        Assert.Equal(["error", "warning", "fatal"], claimed);
    }

    [Fact]
    public void SelectFires_includes_same_phrase_distinct_actions()
    {
        var phrase = "RIP INEXACT";
        var matches = new[]
        {
            Match(phrase, CaptureAction.Screenshot),
            Match(phrase, CaptureAction.Record),
            Match("other", CaptureAction.Screenshot)
        };

        var fires = TriggerFireCoalescer.SelectFires(matches, _ => true);

        Assert.Equal(3, fires.Count);
        Assert.Contains(fires, f => f.Trigger.Text == phrase && f.Trigger.Action == CaptureAction.Screenshot);
        Assert.Contains(fires, f => f.Trigger.Text == phrase && f.Trigger.Action == CaptureAction.Record);
        Assert.Contains(fires, f => f.Trigger.Text == "other");
    }

    [Fact]
    public void SelectFires_skips_duplicate_same_action_for_same_phrase()
    {
        var phrase = "dup";
        var matches = new[]
        {
            Match(phrase, CaptureAction.Screenshot),
            Match(phrase, CaptureAction.Screenshot),
            Match(phrase, CaptureAction.Record)
        };
        var claimed = new List<CaptureAction>();

        var fires = TriggerFireCoalescer.SelectFires(matches, m =>
        {
            claimed.Add(m.Trigger.Action);
            return true;
        });

        Assert.Equal(2, fires.Count);
        Assert.Equal(new[] { CaptureAction.Screenshot, CaptureAction.Record }, claimed);
    }

    [Fact]
    public void SelectFires_empty_when_gate_rejects_all()
    {
        var matches = new[]
        {
            Match("a", CaptureAction.Screenshot),
            Match("b", CaptureAction.Screenshot)
        };

        var fires = TriggerFireCoalescer.SelectFires(matches, _ => false);

        Assert.Empty(fires);
    }

    [Fact]
    public void SelectFires_skips_gated_phrase_and_keeps_the_next()
    {
        var matches = new[]
        {
            Match("blocked", CaptureAction.Screenshot),
            Match("allowed", CaptureAction.Screenshot)
        };

        var fires = TriggerFireCoalescer.SelectFires(
            matches,
            m => m.Trigger.Text == "allowed");

        Assert.Single(fires);
        Assert.Equal("allowed", fires[0].Trigger.Text);
    }
}
