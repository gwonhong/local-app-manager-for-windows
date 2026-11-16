using System.Text;
using LocalAppManager.Core.Native;

namespace LocalAppManager.Core.Logs;

/// <summary>
/// Splits a raw byte stream into lines.
/// <list type="bullet">
/// <item>Each line is decoded as UTF-8 (what Node, Python with UTF-8 mode and most tools write).
///   If the bytes aren't valid UTF-8, it falls back to the console OEM code page (CP949 on
///   Korean Windows), which is what cmd.exe and many native tools print.</item>
/// <item>A bare <c>\r</c> (progress bars, spinners) rewinds the current line, so only the final
///   state is kept instead of every frame.</item>
/// </list>
/// Not thread-safe; one instance per stream.
/// </summary>
public sealed class LineDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)NativeMethods.GetOEMCP());
        }
        catch
        {
            return Encoding.Latin1;
        }
    });

    private readonly List<byte> _line = new(256);
    private bool _pendingCr;
    private readonly int _maxLineBytes;

    public LineDecoder(int maxLineBytes = 64 * 1024) => _maxLineBytes = maxLineBytes;

    /// <summary>True if there are bytes of an unfinished line.</summary>
    public bool HasPartial => _line.Count > 0;

    public void Feed(ReadOnlySpan<byte> data, List<string> output)
    {
        foreach (byte b in data)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (b == (byte)'\n')
                {
                    Emit(output);
                    continue;
                }
                // Bare CR: the terminal would return to column 0 and overwrite.
                RewindLine();
            }

            if (b == (byte)'\n') Emit(output);
            else if (b == (byte)'\r') _pendingCr = true;
            else
            {
                _line.Add(b);
                if (_line.Count >= _maxLineBytes) Emit(output);
            }
        }
    }

    /// <summary>Emits whatever is left (end of stream or idle flush).</summary>
    public void Flush(List<string> output)
    {
        _pendingCr = false;
        if (_line.Count > 0) Emit(output);
    }

    /// <summary>Decodes the unfinished line without consuming it (for showing prompts that have no newline yet).</summary>
    public string PeekPartial() => Decode(_line);

    // A terminal would overwrite the line from column 0; keeping only what follows the CR is the closest line-based equivalent.
    private void RewindLine() => _line.Clear();

    private void Emit(List<string> output)
    {
        output.Add(Decode(_line));
        _line.Clear();
    }

    internal static string Decode(List<byte> bytes)
    {
        if (bytes.Count == 0) return "";
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Oem.Value.GetString(span);
        }
    }
}
