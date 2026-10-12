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
    public void SelectFires_picks_first_winner_only_for_different_texts()
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

        Assert.Single(fires);
        Assert.Equal("error", fires[0].Trigger.Text);
        // Sibling different texts must not be claimed.
        Assert.Equal(new[] { "error" }, claimed);
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

        Assert.Equal(2, fires.Count);
        Assert.Contains(fires, f => f.Trigger.Action == CaptureAction.Screenshot);
        Assert.Contains(fires, f => f.Trigger.Action == CaptureAction.Record);
        Assert.DoesNotContain(fires, f => f.Trigger.Text == "other");
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

        var fires = TriggerFireCoalescer.SelectFires(matches, _ => true);

        Assert.Equal(2, fires.Count);
        Assert.Equal(1, fires.Count(f => f.Trigger.Action == CaptureAction.Screenshot));
        Assert.Equal(1, fires.Count(f => f.Trigger.Action == CaptureAction.Record));
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
    public void SelectFires_skips_gated_winner_and_takes_next()
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
