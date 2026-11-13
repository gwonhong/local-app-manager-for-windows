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

    private static Brush Resource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
