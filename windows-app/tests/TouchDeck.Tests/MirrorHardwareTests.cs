using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

/// <summary>
/// Real hardware (firmware 1.7.0+): drives the board through a DeviceSession, builds the mirror
/// from what it streams, renders it with the native renderer, and compares with the board's own
/// framebuffer (FBCRC). Nothing is typed or clicked on the PC; the jiggler only animates (ANIM 1).
/// </summary>
[Collection("Board")]   // one test at a time on the real serial port
public class MirrorHardwareTests
{
    [SkippableFact]
    public void The_mirror_draws_exactly_what_the_board_shows()
    {
        Skip.If(DeviceScanner.Scan().Count == 0, "no Touch Deck connected");
        using var m = DeviceManager.CreateDefault(new FakeSelection("", "select"), new RecordingSink());
        m.Tick();
        var link = HardwareLinks.First(m);
        Skip.If(link.State.Status == LinkStatus.PortBusy, "port busy (Touch Deck app running?)");
        Skip.If(link.State.Firmware?.SemVer is not { } v || v < new Version(1, 7, 0), "firmware older than 1.7.0");
        var s = link.Session!;
        var kind = UiModels.For(link.State.Device!.Kind, link.State.Firmware?.Board);
        var mirror = new MirrorState(kind);
        var gate = new object();
        string? crc = null;
        var dots = new HashSet<(int, int)>();
        void Apply(BoardMessage msg)
        {
            lock (gate)
            {
                mirror.Apply(msg);
                if (msg is StateReport st) dots.Add((st.X, st.Y));
            }
        }
        s.StateReceived += Apply;
        s.TextReceived += Apply;
        s.ClipTextReceived += Apply;
        s.Log += text => { if (text.StartsWith("fbcrc ", StringComparison.Ordinal)) crc = text[6..]; };
        s.SendRaw("WATCH 1");                                  // everything again, now that we listen

        int clipPage = kind.ClipPage(), jigPage = clipPage + 1;
        void GoTo(int page)
        {
            for (int i = 0; i < 3; i++) s.Swipe(left: false);
            for (int i = 0; i < page; i++) s.Swipe(left: true);
        }
        void AssertSame(string what)
        {
            crc = null;
            s.SendRaw($"FBCRC 0 0 {NativeUi.Size(kind).Width} {NativeUi.Size(kind).Height}");
            Assert.True(SpinWait.SpinUntil(() => crc is not null, 2000), "no FBCRC reply");
            ushort[] pixels;
            lock (gate) pixels = NativeUi.Render(kind, mirror.State);
            Assert.True(Crc32(pixels).ToString("x8") == crc, $"{what}: mirror {Crc32(pixels):x8} != board {crc}");
        }

        try
        {
            GoTo(clipPage);
            s.SendText("Mirror test: the app draws\nwhat the board shows.");
            Thread.Sleep(3200);                                // the "Copied" message expires after 2.5 s
            Assert.True(mirror.Complete, "no full-mirror STATE");
            Assert.Equal(clipPage, mirror.State.Screen);
            AssertSame("clipboard");

            GoTo(jigPage);
            Thread.Sleep(900);                                 // a full jiggler frame takes ~290 ms
            Assert.Equal(jigPage, mirror.State.Screen);
            AssertSame("jiggler");

            lock (gate) dots.Clear();
            s.SendRaw("ANIM 1");                               // the dot moves; no HID
            Thread.Sleep(1200);
            lock (gate) Assert.True(dots.Count >= 10, $"only {dots.Count} dot positions in 1.2 s");
        }
        finally
        {
            s.SendRaw("ANIM 0");
            s.ClearClip();
            GoTo(0);
            Thread.Sleep(300);
        }
    }

    private static uint Crc32(ushort[] pixels)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var p in pixels)
            foreach (var b in new[] { (byte)p, (byte)(p >> 8) })
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
        return ~crc;
    }
}
