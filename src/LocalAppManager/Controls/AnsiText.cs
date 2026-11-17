using System.Text.RegularExpressions;
using LocalAppManager.Core.Logs;
using LocalAppManager.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace LocalAppManager.Controls;

/// <summary>
/// Attached property that renders a <see cref="LogLineViewModel"/> into a TextBlock:
/// one Run per ANSI span, backgrounds and search matches as TextHighlighters
/// (Runs can't have a background in WinUI).
/// </summary>
public static partial class AnsiText
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(object), typeof(AnsiText), new PropertyMetadata(null, OnLineChanged));

    public static object? GetLine(DependencyObject d) => d.GetValue(LineProperty);

    public static void SetLine(DependencyObject d, object? value) => d.SetValue(LineProperty, value);

    private static readonly Dictionary<(ElementTheme, string), Brush> ThemeBrushes = new();

    /// <summary>Brush from App.xaml's ThemeDictionaries for an explicit theme (Application.Resources would give the app theme only).</summary>
    private static Brush Res(ElementTheme theme, string key)
    {
        if (ThemeBrushes.TryGetValue((theme, key), out var cached)) return cached;
        Brush brush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        var dicts = Application.Current.Resources.ThemeDictionaries;
        if (dicts.TryGetValue(theme == ElementTheme.Light ? "Light" : "Dark", out var d) && d is ResourceDictionary rd && rd.TryGetValue(key, out var v) && v is Brush b)
            brush = b;
        ThemeBrushes[(theme, key)] = brush;
        return brush;
    }

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        tb.TextHighlighters.Clear();
        if (e.NewValue is not LogLineViewModel vm) return;
        Render(tb, vm);
    }

    public static void Render(TextBlock tb, LogLineViewModel vm)
    {
        var line = vm.Line;
        // Rows are rendered before they join the tree, so their own ActualTheme isn't settled yet; use the window's.
        var theme = Converters.Theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        int pos = 0;

        if (vm.ShowTimestamp)
        {
            string ts = line.Time.ToString("HH:mm:ss.fff") + "  ";
            tb.Inlines.Add(new Run { Text = ts, Foreground = Res(theme, "LogTimestampBrush") });
            pos += ts.Length;
        }

        if (line.Source is { } source)
        {
            string tag = $"[{source}] ";
            tb.Inlines.Add(new Run { Text = tag, Foreground = Res(theme, "LogSourceBrush") });
            pos += tag.Length;
        }

        int textStart = pos;
        var links = new LinkWriter(tb.Inlines, line.Text);
        switch (line.Kind)
        {
            case LogLineKind.System:
                links.Add(new Run { Text = line.Text, Foreground = Res(theme, "LogSystemBrush"), FontStyle = FontStyle.Italic }, 0);
                break;
            case LogLineKind.Error:
                links.Add(new Run { Text = line.Text, Foreground = Res(theme, "LogErrorBrush") }, 0);
                break;
            default:
                if (line.Spans.Length == 0)
                {
                    links.Add(new Run { Text = line.Text }, 0);
                }
                else
                {
                    var backgrounds = new Dictionary<Brush, TextHighlighter>();
                    foreach (var span in line.Spans)
                    {
                        var run = new Run { Text = span.Text };
                        bool dim = span.Style.HasFlag(AnsiStyle.Dim);
                        SolidColorBrush? fg = AnsiPalette.Foreground(span.Foreground, theme, dim);
                        SolidColorBrush? bg = AnsiPalette.Background(span.Background);
                        if (span.Style.HasFlag(AnsiStyle.Inverse))
                        {
                            var newBg = AnsiPalette.Background(span.Foreground) ?? AnsiPalette.DefaultText(theme);
                            fg = bg is not null ? AnsiPalette.ContrastingText(bg) : AnsiPalette.DefaultBackground(theme);
                            bg = newBg;
                        }
                        if (bg is not null && fg is null) fg = AnsiPalette.ContrastingText(bg);
                        if (fg is not null) run.Foreground = fg;
                        else if (dim) run.Foreground = Res(theme, "LogSystemBrush");
                        if (span.Style.HasFlag(AnsiStyle.Bold)) run.FontWeight = FontWeights.SemiBold;
                        if (span.Style.HasFlag(AnsiStyle.Italic)) run.FontStyle = FontStyle.Italic;
                        var deco = TextDecorations.None;
                        if (span.Style.HasFlag(AnsiStyle.Underline)) deco |= TextDecorations.Underline;
                        if (span.Style.HasFlag(AnsiStyle.Strike)) deco |= TextDecorations.Strikethrough;
                        if (deco != TextDecorations.None) run.TextDecorations = deco;
                        links.Add(run, pos - textStart);

                        if (bg is not null && span.Text.Length > 0)
                        {
                            if (!backgrounds.TryGetValue(bg, out var hl))
                            {
                                hl = new TextHighlighter { Background = bg };
                                if (fg is not null) hl.Foreground = fg;
                                backgrounds[bg] = hl;
                                tb.TextHighlighters.Add(hl);
                            }
                            hl.Ranges.Add(new TextRange { StartIndex = pos, Length = span.Text.Length });
                        }
                        pos += span.Text.Length;
                    }
                }
                break;
        }

        // Search matches on top of everything else.
        if (!string.IsNullOrEmpty(vm.Highlight))
        {
            TextHighlighter? match = null;
            int from = 0;
            while ((from = line.Text.IndexOf(vm.Highlight, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                match ??= new TextHighlighter
                {
                    Background = Res(theme, "LogSearchHighlightBrush"),
                    Foreground = Res(theme, "LogSearchHighlightTextBrush"),
                };
                match.Ranges.Add(new TextRange { StartIndex = textStart + from, Length = vm.Highlight.Length });
                from += vm.Highlight.Length;
            }
            if (match is not null) tb.TextHighlighters.Add(match);
        }
    }

    // ------------------------------------------------------------------ clickable URLs

    // Host is either a bracketed IPv6 literal ("[::]", "[::1]") or anything up to a delimiter; brackets, parens and
    // quotes end the URL so "(http://localhost:3000)" and "'http://…'" don't swallow the closing character.
    [GeneratedRegex(@"\bhttps?://(?:\[[0-9a-f:.]+\]|[^\s'""<>()\[\]{}`/])[^\s'""<>()\[\]{}`]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>
    /// Adds a line's runs to a TextBlock, cutting them at URL boundaries and wrapping the URL parts in a Hyperlink
    /// (one per URL, even when ANSI styling splits it into several runs). A click opens the default browser.
    /// </summary>
    private sealed class LinkWriter
    {
        private readonly InlineCollection _inlines;
        private readonly List<(int Start, int End, Uri Target)> _urls = [];
        private readonly Dictionary<int, Hyperlink> _open = new();

        public LinkWriter(InlineCollection inlines, string text)
        {
            _inlines = inlines;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) return;
            foreach (Match m in UrlRegex().Matches(text))
            {
                string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
                if (Target(url) is { } target) _urls.Add((m.Index, m.Index + url.Length, target));
            }
        }

        /// <summary>Bind-all addresses aren't browsable; open them on localhost like the run bar does.</summary>
        private static Uri? Target(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Host is "0.0.0.0" or "[::]" or "::")
                uri = new UriBuilder(uri) { Host = "localhost" }.Uri;
            return uri;
        }

        /// <summary>Adds <paramref name="run"/>, whose text starts at <paramref name="at"/> in the line's text.</summary>
        public void Add(Run run, int at)
        {
            if (_urls.Count == 0)
            {
                _inlines.Add(run);
                return;
            }
            string text = run.Text;
            int i = 0;
            while (i < text.Length)
            {
                int abs = at + i;
                int index = _urls.FindIndex(u => abs >= u.Start && abs < u.End);
                if (index >= 0)
                {
                    var (_, end, target) = _urls[index];
                    int stop = Math.Min(text.Length, end - at);
                    var part = Copy(run, text[i..stop]);
                    part.ClearValue(TextElement.ForegroundProperty); // link color, not the ANSI one
                    // A run's own TextDecorations replaces the Hyperlink's underline, so underline it explicitly.
                    part.TextDecorations = run.TextDecorations | TextDecorations.Underline;
                    if (!_open.TryGetValue(index, out var link))
                    {
                        link = new Hyperlink { NavigateUri = target, UnderlineStyle = UnderlineStyle.Single };
                        ToolTipService.SetToolTip(link, target.ToString());
                        _open[index] = link;
                        _inlines.Add(link);
                    }
                    link.Inlines.Add(part);
                    i = stop;
                }
                else
                {
                    int next = text.Length;
                    foreach (var u in _urls)
                        if (u.Start > abs) next = Math.Min(next, u.Start - at);
                    _inlines.Add(i == 0 && next == text.Length ? run : Copy(run, text[i..next]));
                    i = next;
                }
            }
        }

        private static Run Copy(Run source, string text)
        {
            var run = new Run { Text = text, FontWeight = source.FontWeight, FontStyle = source.FontStyle, TextDecorations = source.TextDecorations };
            if (source.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue) run.Foreground = source.Foreground;
            return run;
        }
    }
}
