using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace MicroCapture.UI.Theming;

/// <summary>Which palette the operator wants. <see cref="System"/> follows the OS setting, which
/// is what most people mean by "just match everything else on this machine".</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System,
}

/// <summary>Owns day/night mode: applies a <see cref="ThemeMode"/> to the running application and
/// remembers the choice.
///
/// <para>Almost all of the switch is declarative — every colour in Styles/Theme.axaml is defined
/// once per variant and referenced with DynamicResource, so setting
/// <c>RequestedThemeVariant</c> repaints the whole interface with no code involved. The one part
/// that can't work that way is the handful of brushes produced by value converters
/// (<c>StatusSeverityConverter</c>, <c>BoolToSelectBrushConverter</c>): a converter runs when its
/// binding's source changes, and the theme changing isn't a change to any of those sources, so a
/// converter that looked up the current variant would simply keep returning yesterday's answer
/// until the status text happened to change. <see cref="SemanticBrushes"/> solves that by handing
/// out brush objects that outlive the switch and having this class recolour them in place —
/// same instance, new colour, immediate repaint.</para></summary>
public static class AppTheme
{
    /// <summary>The mode currently applied. Not necessarily the variant on screen: under
    /// <see cref="ThemeMode.System"/> the OS decides which of the two is showing.</summary>
    public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

    /// <summary>Applies a mode to the running app and recolours the converter brushes to match.
    /// Safe to call before a window exists.</summary>
    public static void Apply(ThemeMode mode)
    {
        Current = mode;

        var app = Application.Current;
        if (app == null) return;

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        // Under System the app follows the OS, so the variant actually in force is the one
        // Avalonia resolved — not the one requested.
        SemanticBrushes.Apply(IsLight(app.ActualThemeVariant));
    }

    /// <summary>Loads the saved mode and applies it. Called once at startup.</summary>
    public static ThemeMode ApplySaved(AppPreferences preferences)
    {
        var mode = Parse(preferences.ThemeMode);
        Apply(mode);
        return mode;
    }

    /// <summary>Applies a mode and writes it to preferences, so the next session opens the way
    /// this one ended.</summary>
    public static void ApplyAndSave(ThemeMode mode, AppPreferences preferences)
    {
        Apply(mode);
        preferences.ThemeMode = mode.ToString();
        preferences.Save();
    }

    /// <summary>The mode after this one, for a single control that cycles rather than three
    /// separate options: Dark → Light → System → Dark.</summary>
    public static ThemeMode Next(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ThemeMode.Light,
        ThemeMode.Light => ThemeMode.System,
        _ => ThemeMode.Dark,
    };

    /// <summary>An unrecognised or missing saved value means dark — the app's long-standing
    /// default, and the right one to fall back to rather than surprising an operator with a
    /// white screen because a preferences file was hand-edited.</summary>
    public static ThemeMode Parse(string? value) =>
        Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var parsed) ? parsed : ThemeMode.Dark;

    private static bool IsLight(ThemeVariant? variant) => variant == ThemeVariant.Light;
}

/// <summary>The brushes value converters hand back. Deliberately mutable and shared: recolouring
/// one instance repaints every control bound through a converter, which is the only way those
/// brushes can follow a theme switch (see <see cref="AppTheme"/>).</summary>
public static class SemanticBrushes
{
    public static SolidColorBrush Success { get; } = new(Color.Parse("#27a644"));
    public static SolidColorBrush Warning { get; } = new(Color.Parse("#d99a3d"));
    public static SolidColorBrush Fail { get; } = new(Color.Parse("#e5484d"));
    public static SolidColorBrush Neutral { get; } = new(Color.Parse("#8a8f98"));
    public static SolidColorBrush InProgress { get; } = new(Color.Parse("#5e6ad2"));
    public static SolidColorBrush Accent { get; } = new(Color.Parse("#5e6ad2"));

    /// <summary>Keep these values in step with the matching keys in Styles/Theme.axaml — the same
    /// severity must not be one green in a status dot and a different green in a border.</summary>
    public static void Apply(bool light)
    {
        Success.Color = Color.Parse(light ? "#1a7f36" : "#27a644");
        Warning.Color = Color.Parse(light ? "#8a5a08" : "#d99a3d");
        Fail.Color = Color.Parse(light ? "#c02026" : "#e5484d");
        Neutral.Color = Color.Parse(light ? "#5d626b" : "#8a8f98");
        InProgress.Color = Color.Parse(light ? "#4f5bc4" : "#5e6ad2");
        Accent.Color = Color.Parse(light ? "#4f5bc4" : "#5e6ad2");
    }
}
