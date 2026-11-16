using System.Text;

namespace LocalAppManager.Core.Logs;

public enum AnsiColorKind : byte { Default, Palette, Rgb }

/// <summary>A terminal color: default, one of the 256 palette entries, or 24-bit RGB.</summary>
public readonly record struct AnsiColor(AnsiColorKind Kind, byte Index = 0, byte R = 0, byte G = 0, byte B = 0)
{
    public static readonly AnsiColor Default = new(AnsiColorKind.Default);
    public static AnsiColor Palette(int index) => new(AnsiColorKind.Palette, (byte)index);
    public static AnsiColor FromRgb(byte r, byte g, byte b) => new(AnsiColorKind.Rgb, 0, r, g, b);
    public bool IsDefault => Kind == AnsiColorKind.Default;
}

[Flags]
public enum AnsiStyle : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strike = 32,
}

public readonly record struct AnsiSpan(string Text, AnsiColor Foreground, AnsiColor Background, AnsiStyle Style)
{
    public bool IsPlain => Foreground.IsDefault && Background.IsDefault && Style == AnsiStyle.None;
}

/// <summary>
/// Parses ANSI escape sequences in already-split lines. SGR (<c>ESC[…m</c>) becomes styled spans;
/// every other CSI/OSC/ESC sequence is dropped. Graphic state carries over between lines,
/// as it does in a terminal. Not thread-safe; one instance per stream.
/// </summary>
public sealed class AnsiParser
{
    private AnsiColor _fg = AnsiColor.Default;
    private AnsiColor _bg = AnsiColor.Default;
    private AnsiStyle _style;

    public void Reset()
    {
        _fg = _bg = AnsiColor.Default;
        _style = AnsiStyle.None;
    }

    /// <summary>Parses one line. Returns the plain text and its spans (empty when the whole line is unstyled).</summary>
    public (string Text, AnsiSpan[] Spans) Parse(string line)
    {
        if (line.IndexOf('\x1b') < 0 && line.IndexOf('\t') < 0 && IsDefaultState && !HasControlChars(line))
            return (line, []);

        var spans = new List<AnsiSpan>();
        var plain = new StringBuilder(line.Length);
        var run = new StringBuilder();
        int column = 0;

        void FlushRun()
        {
            if (run.Length == 0) return;
            spans.Add(new AnsiSpan(run.ToString(), _fg, _bg, _style));
            run.Clear();
        }

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\x1b')
            {
                if (i + 1 >= line.Length) break;
                char next = line[i + 1];
                if (next == '[')
                {
                    int start = i + 2, j = start;
                    while (j < line.Length && line[j] >= 0x20 && line[j] <= 0x3F) j++; // parameter + intermediate bytes
                    while (j < line.Length && line[j] >= 0x20 && line[j] <= 0x2F) j++;
                    if (j >= line.Length) { i = line.Length; break; }
                    if (line[j] == 'm')
                    {
                        FlushRun();
                        ApplySgr(line.AsSpan(start, j - start));
                    }
                    i = j;
                }
                else if (next == ']')
                {
                    // OSC … BEL or OSC … ESC \  (window titles, OSC 8 hyperlinks: the visible text stays)
                    int j = i + 2;
                    while (j < line.Length && line[j] != '\a' && !(line[j] == '\x1b' && j + 1 < line.Length && line[j + 1] == '\\')) j++;
                    i = j < line.Length && line[j] == '\x1b' ? j + 1 : j;
                }
                else if (next is '(' or ')' or '*' or '+')
                {
                    i += 2; // charset designation: ESC ( B
                }
                else
                {
                    i += 1; // two-character escape, e.g. ESC 7 / ESC M
                }
                continue;
            }

            if (c == '\t')
            {
                int spaces = 8 - column % 8;
                run.Append(' ', spaces);
                plain.Append(' ', spaces);
                column += spaces;
                continue;
            }
            if (c < 0x20 || c == 0x7F || c == '\u009b') continue; // other C0 controls (BEL, BS…) are invisible

            run.Append(c);
            plain.Append(c);
            column++;
        }
        FlushRun();

        if (spans.Count == 1 && spans[0].IsPlain) return (plain.ToString(), []);
        return (plain.ToString(), spans.ToArray());
    }

    private bool IsDefaultState => _fg.IsDefault && _bg.IsDefault && _style == AnsiStyle.None;

    private static bool HasControlChars(string s)
    {
        foreach (char c in s)
            if (c < 0x20 || c == 0x7F) return true;
        return false;
    }

    private void ApplySgr(ReadOnlySpan<char> parameters)
    {
        if (parameters.IsEmpty)
        {
            Reset();
            return;
        }

        // Reject private sequences like ESC[?25m; they aren't SGR.
        if (parameters[0] is '?' or '>' or '<' or '=') return;

        var codes = new List<int>(8);
        var colonGroups = new List<int[]?>(8);
        foreach (var range in parameters.Split(';'))
        {
            var part = parameters[range];
            if (part.Contains(':'))
            {
                // ITU T.416 sub-parameters: 38:2::r:g:b or 38:5:n
                var subs = new List<int>();
                foreach (var sr in part.Split(':')) subs.Add(ParseInt(part[sr]));
                codes.Add(subs.Count > 0 ? subs[0] : 0);
                colonGroups.Add(subs.ToArray());
            }
            else
            {
                codes.Add(ParseInt(part));
                colonGroups.Add(null);
            }
        }

        for (int i = 0; i < codes.Count; i++)
        {
            int code = codes[i];
            switch (code)
            {
                case 0: Reset(); break;
                case 1: _style |= AnsiStyle.Bold; break;
                case 2: _style |= AnsiStyle.Dim; break;
                case 3: _style |= AnsiStyle.Italic; break;
                case 4: _style |= AnsiStyle.Underline; break;
                case 7: _style |= AnsiStyle.Inverse; break;
                case 9: _style |= AnsiStyle.Strike; break;
                case 21: _style |= AnsiStyle.Underline; break;
                case 22: _style &= ~(AnsiStyle.Bold | AnsiStyle.Dim); break;
                case 23: _style &= ~AnsiStyle.Italic; break;
                case 24: _style &= ~AnsiStyle.Underline; break;
                case 27: _style &= ~AnsiStyle.Inverse; break;
                case 29: _style &= ~AnsiStyle.Strike; break;
                case >= 30 and <= 37: _fg = AnsiColor.Palette(code - 30); break;
                case 39: _fg = AnsiColor.Default; break;
                case >= 40 and <= 47: _bg = AnsiColor.Palette(code - 40); break;
                case 49: _bg = AnsiColor.Default; break;
                case >= 90 and <= 97: _fg = AnsiColor.Palette(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = AnsiColor.Palette(code - 100 + 8); break;
                case 38 or 48:
                {
                    AnsiColor? color;
                    if (colonGroups[i] is { } sub) color = ExtendedFromColon(sub);
                    else color = ExtendedFromSemicolon(codes, ref i);
                    if (color is { } col)
                    {
                        if (code == 38) _fg = col;
                        else _bg = col;
                    }
                    break;
                }
            }
        }
    }

    private static AnsiColor? ExtendedFromSemicolon(List<int> codes, ref int i)
    {
        if (i + 1 >= codes.Count) return null;
        int mode = codes[i + 1];
        if (mode == 5 && i + 2 < codes.Count)
        {
            int n = codes[i + 2];
            i += 2;
            return AnsiColor.Palette(Math.Clamp(n, 0, 255));
        }
        if (mode == 2 && i + 4 < codes.Count)
        {
            var c = AnsiColor.FromRgb(Clamp(codes[i + 2]), Clamp(codes[i + 3]), Clamp(codes[i + 4]));
            i += 4;
            return c;
        }
        i = codes.Count; // malformed: stop interpreting this sequence
        return null;
    }

    private static AnsiColor? ExtendedFromColon(int[] sub)
    {
        // sub[0] = 38/48, sub[1] = mode
        if (sub.Length >= 3 && sub[1] == 5) return AnsiColor.Palette(Math.Clamp(sub[2], 0, 255));
        if (sub.Length >= 5 && sub[1] == 2)
        {
            // 38:2:<colorspace>:r:g:b (6 items) or 38:2:r:g:b (5 items)
            int o = sub.Length >= 6 ? 3 : 2;
            return AnsiColor.FromRgb(Clamp(sub[o]), Clamp(sub[o + 1]), Clamp(sub[o + 2]));
        }
        return null;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private static int ParseInt(ReadOnlySpan<char> s)
    {
        int v = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return v;
            v = Math.Min(v * 10 + (c - '0'), 100000);
        }
        return v;
    }

    /// <summary>Removes all escape sequences, keeping only visible text.</summary>
    public static string Strip(string line) => new AnsiParser().Parse(line).Text;
}
