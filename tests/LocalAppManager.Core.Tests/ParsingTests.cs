using System.Text;
using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;

namespace LocalAppManager.Core.Tests;

public class LineDecoderTests
{
    private static List<string> Decode(params byte[][] chunks)
    {
        var d = new LineDecoder();
        var lines = new List<string>();
        foreach (var c in chunks) d.Feed(c, lines);
        d.Flush(lines);
        return lines;
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Splits_lf_and_crlf() =>
        Assert.Equal(["a", "b", "c"], Decode(U("a\nb\r\nc")));

    [Fact]
    public void Crlf_split_across_chunks() =>
        Assert.Equal(["a", "b"], Decode(U("a\r"), U("\nb")));

    [Fact]
    public void Bare_cr_keeps_the_last_frame() =>
        Assert.Equal(["progress 100%", "done"], Decode(U("progress 10%\rprogress 50%\rprogress 100%\ndone\n")));

    [Fact]
    public void Utf8_split_mid_character() =>
        Assert.Equal(["한글"], Decode(U("한글\n")[..2], U("한글\n")[2..]));

    [Fact]
    public void Falls_back_to_oem_codepage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949).GetBytes("지정된 경로를 찾을 수 없습니다.\n");
        var lines = Decode(cp949);
        // On a Korean system the OEM code page is 949; elsewhere at least it must not throw.
        if (Native.NativeMethods.GetOEMCP() == 949) Assert.Equal("지정된 경로를 찾을 수 없습니다.", lines[0]);
        else Assert.Single(lines);
    }
}

public class AnsiParserTests
{
    [Fact]
    public void Plain_text_has_no_spans()
    {
        var (text, spans) = new AnsiParser().Parse("hello");
        Assert.Equal("hello", text);
        Assert.Empty(spans);
    }

    [Fact]
    public void Basic_colors_and_reset()
    {
        var (text, spans) = new AnsiParser().Parse("\x1b[32m➜\x1b[39m  \x1b[1mLocal\x1b[22m:   \x1b[36mhttp://localhost:\x1b[1m4100\x1b[22m/\x1b[39m");
        Assert.Equal("➜  Local:   http://localhost:4100/", text);
        Assert.Equal(AnsiColor.Palette(2), spans[0].Foreground);
        Assert.Equal("➜", spans[0].Text);
        Assert.Contains(spans, s => s.Text == "Local" && s.Style.HasFlag(AnsiStyle.Bold));
        Assert.Contains(spans, s => s.Text == "4100" && s.Style.HasFlag(AnsiStyle.Bold) && s.Foreground == AnsiColor.Palette(6));
    }

    [Fact]
    public void Bright_256_and_truecolor()
    {
        var (_, spans) = new AnsiParser().Parse("\x1b[91ma\x1b[38;5;208mb\x1b[38;2;10;20;30mc\x1b[48;2;1;2;3md\x1b[38:2::5:6:7me");
        Assert.Equal(AnsiColor.Palette(9), spans[0].Foreground);
        Assert.Equal(AnsiColor.Palette(208), spans[1].Foreground);
        Assert.Equal(AnsiColor.FromRgb(10, 20, 30), spans[2].Foreground);
        Assert.Equal(AnsiColor.FromRgb(1, 2, 3), spans[3].Background);
        Assert.Equal(AnsiColor.FromRgb(5, 6, 7), spans[4].Foreground);
    }

    [Fact]
    public void State_carries_to_next_line()
    {
        var p = new AnsiParser();
        p.Parse("\x1b[31mred starts");
        var (_, spans) = p.Parse("still red");
        Assert.Equal(AnsiColor.Palette(1), spans[0].Foreground);
        var (_, after) = p.Parse("\x1b[0mplain");
        Assert.Empty(after);
    }

    [Fact]
    public void Non_sgr_sequences_are_dropped()
    {
        var (text, _) = new AnsiParser().Parse("\x1b[2K\x1b[1Gbuilding \x1b]0;title\x07\x1b]8;;http://x\x1b\\link\x1b]8;;\x1b\\ \x1b(Bdone\x1b[?25h");
        Assert.Equal("building link done", text);
    }

    [Fact]
    public void Tabs_expand_to_columns() =>
        Assert.Equal("ab      c", new AnsiParser().Parse("ab\tc").Text);
}

public class UrlDetectorTests
{
    [Theory]
    [InlineData("  ➜  Local:   http://localhost:4100/", "http://localhost:4100/", 4100)]
    [InlineData("  - Local:        http://localhost:3000", "http://localhost:3000", 3000)]
    [InlineData("INFO:     Uvicorn running on http://127.0.0.1:8000 (Press CTRL+C to quit)", "http://127.0.0.1:8000", 8000)]
    [InlineData("Server listening at http://0.0.0.0:4200.", "http://localhost:4200", 4200)]
    [InlineData("Starting development server at http://[::1]:8001/", "http://[::1]:8001/", 8001)]
    public void Finds_local_urls(string line, string url, int port)
    {
        var found = UrlDetector.Find(line);
        Assert.NotNull(found);
        Assert.Equal(url, found.Value.Url);
        Assert.Equal(port, found.Value.Port);
        Assert.True(found.Value.IsLocal);
    }

    [Fact]
    public void Lan_url_is_not_local()
    {
        var found = UrlDetector.Find("  ➜  Network: http://192.168.0.10:4100/");
        Assert.NotNull(found);
        Assert.False(found.Value.IsLocal);
    }

    [Fact]
    public void Ignores_lines_without_ports() => Assert.Null(UrlDetector.Find("see https://vite.dev/config"));
}

public class LogBufferTests
{
    [Fact]
    public void Ring_keeps_newest_and_reports_first_seq()
    {
        var b = new LogBuffer(3);
        for (int i = 0; i < 5; i++) b.System($"l{i}");
        Assert.Equal(3, b.Count);
        Assert.Equal(2, b.FirstSeq);
        Assert.Equal(["l2", "l3", "l4"], b.Snapshot().Select(l => l.Text));
        Assert.Equal(["l4"], b.GetSince(4).Select(l => l.Text));
        Assert.Equal(["l2", "l3", "l4"], b.GetSince(0).Select(l => l.Text));
    }

    [Fact]
    public void Clear_bumps_generation_and_keeps_sequence()
    {
        var b = new LogBuffer(10);
        b.System("a");
        int gen = b.Generation;
        b.Clear();
        Assert.NotEqual(gen, b.Generation);
        Assert.Equal(0, b.Count);
        b.System("b");
        Assert.Equal(1, b.Snapshot()[0].Seq);
    }

    [Fact]
    public void Resize_keeps_newest()
    {
        var b = new LogBuffer(5);
        for (int i = 0; i < 5; i++) b.System($"l{i}");
        b.Resize(2);
        Assert.Equal(["l3", "l4"], b.Snapshot().Select(l => l.Text));
        b.System("l5");
        Assert.Equal(["l4", "l5"], b.Snapshot().Select(l => l.Text));
    }
}

public class PortAllocatorTests
{
    [Fact]
    public void Skips_assigned_and_busy_ports()
    {
        var busy = new HashSet<int> { 4101 };
        var alloc = new PortAllocator(p => !busy.Contains(p));
        Assert.Equal(4102, alloc.Allocate(4100, 4110, [4100]));
    }

    [Fact]
    public void Wraps_after_preferred_and_returns_null_when_full()
    {
        var alloc = new PortAllocator(_ => true);
        Assert.Equal(4100, alloc.Allocate(4100, 4102, [4101], preferAfter: 4102));
        Assert.Equal(4102, alloc.Allocate(4100, 4102, [], preferAfter: 4101));
        Assert.Null(alloc.Allocate(4100, 4101, [4100, 4101]));
    }

    [Fact]
    public void Real_probe_sees_a_bound_listener()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        try
        {
            Assert.False(PortProbe.IsFree(port));
            var owners = PortProbe.FindOwners(port);
            Assert.Contains(owners, o => o.ProcessId == Environment.ProcessId);
            Assert.Contains(port, PortProbe.ListeningPortsOf([Environment.ProcessId]));
        }
        finally
        {
            l.Stop();
        }
        Assert.True(PortProbe.IsFree(port));
    }
}
