using System;
using System.Collections.Generic;
using System.Linq;

namespace Klipp.Core.Input;

[Flags]
public enum EditorShortcutModifiers : uint
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4
}

public static class EditorShortcutActions
{
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string Duplicate = "duplicate";
    public const string Split = "split";
    public const string Delete = "delete";
    public const string PlayPause = "play-pause";
    public const string ReviewReverse = "review-reverse";
    public const string ReviewPause = "review-pause";
    public const string ReviewForward = "review-forward";
    public const string AddMarker = "add-marker";
    public const string OpenLibrary = "open-library";
    public const string Copy = "copy-clips";
    public const string Cut = "cut-clips";
    public const string Paste = "paste-clips";
    public const string RippleDelete = "ripple-delete";
    public const string GoToTimecode = "go-to-timecode";
    public const string ShowShortcuts = "show-shortcuts";
}

public sealed class EditorShortcutBinding
{
    public string Action { get; set; } = string.Empty;
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    public EditorShortcutBinding Clone() => new()
    {
        Action = Action,
        Modifiers = Modifiers,
        VirtualKey = VirtualKey
    };
}

public sealed record EditorShortcutDefinition(string Action, string Label, uint Modifiers, uint VirtualKey);

public static class EditorShortcutCatalog
{
    private const uint KeyBack = 8;
    private const uint KeyShift = 16;
    private const uint KeyControl = 17;
    private const uint KeyAlt = 18;
    private const uint KeyEscape = 27;
    private const uint KeySpace = 32;
    private const uint KeyEnd = 35;
    private const uint KeyHome = 36;
    private const uint KeyLeft = 37;
    private const uint KeyUp = 38;
    private const uint KeyRight = 39;
    private const uint KeyDown = 40;
    private const uint KeyDelete = 46;
    private const uint KeyLeftWindows = 91;
    private const uint KeyRightWindows = 92;
    private const uint KeyA = 65;
    private const uint KeyC = 67;
    private const uint KeyD = 68;
    private const uint KeyG = 71;
    private const uint KeyI = 73;
    private const uint KeyJ = 74;
    private const uint KeyK = 75;
    private const uint KeyL = 76;
    private const uint KeyM = 77;
    private const uint KeyO = 79;
    private const uint KeyS = 83;
    private const uint KeyV = 86;
    private const uint KeyX = 88;
    private const uint KeyY = 89;
    private const uint KeyZ = 90;
    private const uint KnownModifierMask = (uint)(
        EditorShortcutModifiers.Control |
        EditorShortcutModifiers.Shift |
        EditorShortcutModifiers.Alt);

    public static IReadOnlyList<EditorShortcutDefinition> Definitions { get; } =
    [
        new(EditorShortcutActions.Undo, "Undo", (uint)EditorShortcutModifiers.Control, KeyZ),
        new(EditorShortcutActions.Redo, "Redo", (uint)EditorShortcutModifiers.Control, KeyY),
        new(EditorShortcutActions.Duplicate, "Duplicate", (uint)EditorShortcutModifiers.Control, KeyD),
        new(EditorShortcutActions.Split, "Split at playhead", 0, KeyS),
        new(EditorShortcutActions.Delete, "Delete selection", 0, KeyDelete),
        new(EditorShortcutActions.PlayPause, "Play / pause", 0, KeySpace),
        new(EditorShortcutActions.ReviewReverse, "Review backward (press again for faster)", 0, KeyJ),
        new(EditorShortcutActions.ReviewPause, "Pause review", 0, KeyK),
        new(EditorShortcutActions.ReviewForward, "Review forward (press again for faster)", 0, KeyL),
        new(EditorShortcutActions.AddMarker, "Add marker", 0, KeyM),
        new(EditorShortcutActions.OpenLibrary, "Open media library", (uint)EditorShortcutModifiers.Control, KeyL),
        new(EditorShortcutActions.Copy, "Copy clips", (uint)EditorShortcutModifiers.Control, KeyC),
        new(EditorShortcutActions.Cut, "Cut clips", (uint)EditorShortcutModifiers.Control, KeyX),
        new(EditorShortcutActions.Paste, "Paste clips at playhead", (uint)EditorShortcutModifiers.Control, KeyV),
        new(EditorShortcutActions.RippleDelete, "Ripple delete", (uint)EditorShortcutModifiers.Shift, KeyDelete),
        new(EditorShortcutActions.GoToTimecode, "Go to timecode", (uint)EditorShortcutModifiers.Control, KeyG),
        new(EditorShortcutActions.ShowShortcuts, "Editor shortcuts", 0, 112)
    ];

    public static List<EditorShortcutBinding> CreateDefaults()
    {
        return Definitions.Select(definition => new EditorShortcutBinding
        {
            Action = definition.Action,
            Modifiers = definition.Modifiers,
            VirtualKey = definition.VirtualKey
        }).ToList();
    }

    public static List<EditorShortcutBinding> Normalize(IEnumerable<EditorShortcutBinding>? bindings)
    {
        var result = CreateDefaults();
        if (bindings is null)
            return result;

        var sources = bindings.ToList();
        var legacyLibraryGesture = !sources.Any(binding => binding?.Action is
            EditorShortcutActions.ReviewReverse or EditorShortcutActions.ReviewPause or EditorShortcutActions.ReviewForward);
        var knownActions = Definitions.Select(definition => definition.Action).ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null || !knownActions.Contains(source.Action) || !IsValidGesture(source.Modifiers, source.VirtualKey))
                continue;
            // Upgrade the former default L without displacing the new review keys.
            if (legacyLibraryGesture && source.Action == EditorShortcutActions.OpenLibrary &&
                source.Modifiers == 0 && source.VirtualKey == KeyL) continue;

            var current = result.Single(binding => binding.Action == source.Action);
            if (current.Modifiers == source.Modifiers && current.VirtualKey == source.VirtualKey)
                continue;

            var conflict = result.FirstOrDefault(binding =>
                binding.Action != source.Action &&
                binding.Modifiers == source.Modifiers &&
                binding.VirtualKey == source.VirtualKey);
            if (conflict is not null)
            {
                conflict.Modifiers = current.Modifiers;
                conflict.VirtualKey = current.VirtualKey;
            }

            current.Modifiers = source.Modifiers;
            current.VirtualKey = source.VirtualKey;
        }

        return result;
    }

    public static List<EditorShortcutBinding> Rebind(
        IEnumerable<EditorShortcutBinding>? bindings,
        string action,
        uint modifiers,
        uint virtualKey)
    {
        var result = Normalize(bindings);
        if (!IsValidGesture(modifiers, virtualKey))
            return result;

        var current = result.FirstOrDefault(binding => binding.Action == action);
        if (current is null ||
            (current.Modifiers == modifiers && current.VirtualKey == virtualKey))
        {
            return result;
        }

        var previousModifiers = current.Modifiers;
        var previousVirtualKey = current.VirtualKey;
        var conflict = result.FirstOrDefault(binding =>
            binding.Action != action &&
            binding.Modifiers == modifiers &&
            binding.VirtualKey == virtualKey);
        if (conflict is not null)
        {
            conflict.Modifiers = previousModifiers;
            conflict.VirtualKey = previousVirtualKey;
        }

        current.Modifiers = modifiers & KnownModifierMask;
        current.VirtualKey = virtualKey;
        return result;
    }

    public static EditorShortcutBinding? FindMatch(
        IEnumerable<EditorShortcutBinding>? bindings,
        uint modifiers,
        uint virtualKey)
    {
        return bindings?.FirstOrDefault(binding =>
            binding.Modifiers == (modifiers & KnownModifierMask) &&
            binding.VirtualKey == virtualKey);
    }

    public static bool IsValidGesture(uint modifiers, uint virtualKey)
    {
        if ((modifiers & ~KnownModifierMask) != 0 || virtualKey is 0 or > 254)
            return false;
        if (virtualKey is KeyShift or KeyControl or KeyAlt or KeyLeftWindows or KeyRightWindows or KeyEscape)
            return false;
        return !IsReservedTimelineGesture(modifiers, virtualKey);
    }

    public static bool IsReservedTimelineGesture(uint modifiers, uint virtualKey)
    {
        modifiers &= KnownModifierMask;
        if (modifiers == 0 && virtualKey is KeyLeft or KeyRight or KeyUp or KeyDown or KeyHome or KeyEnd or KeyI or KeyO)
            return true;
        if (modifiers == (uint)EditorShortcutModifiers.Shift && virtualKey is KeyLeft or KeyRight)
            return true;
        return modifiers == (uint)EditorShortcutModifiers.Control && virtualKey == KeyA;
    }

    public static string FormatGesture(EditorShortcutBinding binding) =>
        FormatGesture(binding.Modifiers, binding.VirtualKey);

    public static string FormatGesture(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        var flags = (EditorShortcutModifiers)(modifiers & KnownModifierMask);
        if (flags.HasFlag(EditorShortcutModifiers.Control))
            parts.Add("Ctrl");
        if (flags.HasFlag(EditorShortcutModifiers.Shift))
            parts.Add("Shift");
        if (flags.HasFlag(EditorShortcutModifiers.Alt))
            parts.Add("Alt");
        parts.Add(FormatKey(virtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatKey(uint virtualKey)
    {
        if (virtualKey is >= 65 and <= 90)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 48 and <= 57)
            return ((char)virtualKey).ToString();

        return virtualKey switch
        {
            KeyBack => "Backspace",
            KeySpace => "Space",
            KeyDelete => "Delete",
            112 => "F1",
            113 => "F2",
            114 => "F3",
            115 => "F4",
            116 => "F5",
            117 => "F6",
            118 => "F7",
            119 => "F8",
            120 => "F9",
            121 => "F10",
            122 => "F11",
            123 => "F12",
            _ => $"Key {virtualKey}"
        };
    }
}
