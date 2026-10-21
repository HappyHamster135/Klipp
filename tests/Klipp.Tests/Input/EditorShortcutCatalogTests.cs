using Klipp.Core.Input;

namespace Klipp.Tests.Input;

public sealed class EditorShortcutCatalogTests
{
    [Fact]
    public void Defaults_AreCompleteUniqueAndReadable()
    {
        var defaults = EditorShortcutCatalog.CreateDefaults();

        Assert.Equal(EditorShortcutCatalog.Definitions.Count, defaults.Count);
        Assert.Equal(defaults.Count, defaults.Select(binding => binding.Action).Distinct().Count());
        Assert.Equal(
            defaults.Count,
            defaults.Select(binding => (binding.Modifiers, binding.VirtualKey)).Distinct().Count());
        Assert.All(defaults, binding => Assert.DoesNotContain("Key ", EditorShortcutCatalog.FormatGesture(binding)));
    }

    [Fact]
    public void Normalize_OldDefaultLibraryL_MigratesToControlL_AndKeepsReviewKeys()
    {
        var old = EditorShortcutCatalog.CreateDefaults().Where(binding => binding.Action is not
            (EditorShortcutActions.ReviewReverse or EditorShortcutActions.ReviewPause or EditorShortcutActions.ReviewForward)).ToList();
        var library = old.Single(binding => binding.Action == EditorShortcutActions.OpenLibrary);
        library.Modifiers = 0;
        library.VirtualKey = 76;
        var upgraded = EditorShortcutCatalog.Normalize(old);
        Assert.Equal(EditorShortcutActions.ReviewReverse, EditorShortcutCatalog.FindMatch(upgraded, 0, 74)?.Action);
        Assert.Equal(EditorShortcutActions.ReviewPause, EditorShortcutCatalog.FindMatch(upgraded, 0, 75)?.Action);
        Assert.Equal(EditorShortcutActions.ReviewForward, EditorShortcutCatalog.FindMatch(upgraded, 0, 76)?.Action);
        Assert.Equal(EditorShortcutActions.OpenLibrary, EditorShortcutCatalog.FindMatch(upgraded, 1, 76)?.Action);
    }

    [Fact]
    public void Rebind_SwapsTheConflictingActionsPreviousGesture()
    {
        var defaults = EditorShortcutCatalog.CreateDefaults();
        var undoBefore = defaults.Single(binding => binding.Action == EditorShortcutActions.Undo).Clone();
        var splitBefore = defaults.Single(binding => binding.Action == EditorShortcutActions.Split).Clone();

        var rebound = EditorShortcutCatalog.Rebind(
            defaults,
            EditorShortcutActions.Split,
            undoBefore.Modifiers,
            undoBefore.VirtualKey);

        var splitAfter = rebound.Single(binding => binding.Action == EditorShortcutActions.Split);
        var undoAfter = rebound.Single(binding => binding.Action == EditorShortcutActions.Undo);
        Assert.Equal((undoBefore.Modifiers, undoBefore.VirtualKey), (splitAfter.Modifiers, splitAfter.VirtualKey));
        Assert.Equal((splitBefore.Modifiers, splitBefore.VirtualKey), (undoAfter.Modifiers, undoAfter.VirtualKey));
        Assert.Equal(
            rebound.Count,
            rebound.Select(binding => (binding.Modifiers, binding.VirtualKey)).Distinct().Count());
    }

    [Theory]
    [InlineData((uint)EditorShortcutModifiers.Control, 65u)]
    [InlineData((uint)EditorShortcutModifiers.None, 37u)]
    [InlineData((uint)EditorShortcutModifiers.None, 73u)]
    [InlineData((uint)EditorShortcutModifiers.None, 27u)]
    [InlineData((uint)EditorShortcutModifiers.Shift, 37u)]
    [InlineData((uint)EditorShortcutModifiers.Shift, 39u)]
    public void Normalize_IgnoresReservedOrInvalidGestures(uint modifiers, uint virtualKey)
    {
        var normalized = EditorShortcutCatalog.Normalize(
        [
            new EditorShortcutBinding
            {
                Action = EditorShortcutActions.Split,
                Modifiers = modifiers,
                VirtualKey = virtualKey
            }
        ]);

        var split = normalized.Single(binding => binding.Action == EditorShortcutActions.Split);
        Assert.Equal("S", EditorShortcutCatalog.FormatGesture(split));
    }

    [Fact]
    public void Normalize_LegacyBindingsRetainCustomCommandsAndGainClipboardCommands()
    {
        var oldBindings = EditorShortcutCatalog.CreateDefaults()
            .Where(binding => binding.Action is not (EditorShortcutActions.Copy or EditorShortcutActions.Cut or
                EditorShortcutActions.Paste or EditorShortcutActions.RippleDelete or
                EditorShortcutActions.GoToTimecode or EditorShortcutActions.ShowShortcuts)).ToList();
        var split = oldBindings.Single(binding => binding.Action == EditorShortcutActions.Split);
        split.Modifiers = (uint)EditorShortcutModifiers.Control;
        split.VirtualKey = 73; // Modified I must remain available to custom commands.

        var upgraded = EditorShortcutCatalog.Normalize(oldBindings);

        Assert.Equal("Ctrl + I", EditorShortcutCatalog.FormatGesture(upgraded.Single(binding => binding.Action == EditorShortcutActions.Split)));
        Assert.Equal("Ctrl + C", EditorShortcutCatalog.FormatGesture(upgraded.Single(binding => binding.Action == EditorShortcutActions.Copy)));
        Assert.Equal("Ctrl + V", EditorShortcutCatalog.FormatGesture(upgraded.Single(binding => binding.Action == EditorShortcutActions.Paste)));
        Assert.Equal("Ctrl + G", EditorShortcutCatalog.FormatGesture(upgraded.Single(binding => binding.Action == EditorShortcutActions.GoToTimecode)));
    }

    [Theory]
    [InlineData(37u)]
    [InlineData(36u)]
    [InlineData(73u)]
    public void ModifiedTimelineKeys_CanBeConfiguredWithoutChangingPlainGestures(uint key)
    {
        var rebound = EditorShortcutCatalog.Rebind(EditorShortcutCatalog.CreateDefaults(),
            EditorShortcutActions.Copy, (uint)EditorShortcutModifiers.Control, key);

        Assert.Equal(EditorShortcutActions.Copy,
            EditorShortcutCatalog.FindMatch(rebound, (uint)EditorShortcutModifiers.Control, key)?.Action);
        Assert.Null(EditorShortcutCatalog.FindMatch(rebound, 0, key));
    }
}
