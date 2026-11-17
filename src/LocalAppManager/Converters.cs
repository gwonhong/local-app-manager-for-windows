using LocalAppManager.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LocalAppManager;

/// <summary>Small pure functions used from x:Bind.</summary>
public static class Converters
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIf(object? value) => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool HasValue(object? value) => value is not null && value is not string { Length: 0 };

    public static Brush StateBrush(ProjectState state) => Resource(state switch
    {
        ProjectState.Starting => "StateStartingBrush",
        ProjectState.Running => "StateRunningBrush",
        ProjectState.Exited => "StateExitedBrush",
        ProjectState.Crashed => "StateCrashedBrush",
        _ => "StateStoppedBrush",
    });

    /// <summary>Glyph for the start/stop toggle (Segoe Fluent Icons).</summary>
    public static string ToggleGlyph(bool active) => active ? "" : "";

    public static string ToggleTooltip(bool active) => active ? "정지" : "시작";

    public static string ChipGlyph(bool running, bool succeeded, bool failed) =>
        running ? "" : failed ? "" : succeeded ? "" : "";

    /// <summary>Pin button: filled pin when a port is pinned, outline otherwise.</summary>
    public static string PinGlyph(bool pinned) => pinned ? "" : "";

    public static string PinTooltip(bool pinned) => pinned ? "고정된 포트 — 바꾸거나 해제" : "포트 고정";

    /// <summary>Monospace for the ":4100" label; the UI font for the Korean "포트 고정" (monospace renders it poorly).</summary>
    public static FontFamily PinFont(bool pinned) =>
        (FontFamily)Application.Current.Resources[pinned ? "LogFontFamily" : "ContentControlThemeFontFamily"];

    /// <summary>The window's effective theme (it can differ from the app theme when the user forces light/dark).</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Dark;

    private static Brush Resource(string key)
    {
        var dicts = Application.Current.Resources.ThemeDictionaries;
        if (dicts.TryGetValue(Theme == ElementTheme.Light ? "Light" : "Dark", out var d) && d is ResourceDictionary rd && rd.TryGetValue(key, out var v) && v is Brush b)
            return b;
        return Application.Current.Resources.TryGetValue(key, out var any) && any is Brush ab ? ab : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
