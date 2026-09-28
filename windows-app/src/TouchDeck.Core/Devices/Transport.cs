using System.IO.Ports;
using System.Text;

namespace TouchDeck.Core.Devices;

/// <summary>Byte stream to a board, read as protocol lines.</summary>
public interface ISerialTransport : IDisposable
{
    void Open();
    void WriteLine(string line);
    void Write(byte[] data);
    /// <summary>Next complete line, or null when none arrived within the timeout.</summary>
    string? ReadLine(TimeSpan timeout);
}

/// <summary>Accumulates bytes and hands back complete lines (CR dropped, LF terminated).</summary>
public sealed class LineSplitter
{
    private readonly StringBuilder partial = new();

    public IEnumerable<string> Feed(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        foreach (byte b in bytes)
        {
            if (b == '\r') continue;
            if (b == '\n')
            {
                lines.Add(partial.ToString());
                partial.Clear();
            }
            else
            {
                partial.Append((char)b);
            }
        }
        return lines;
    }
}

public sealed class SerialPortTransport(DeviceCandidate device) : ISerialTransport
{
    private readonly SerialPort port = new(device.Port, 115200)
    {
        // Set before Open: on the ESP32-C3 these lines reset the chip.
        DtrEnable = BoardKinds.DtrHigh(device.Kind),
        RtsEnable = false,
        ReadTimeout = 50,
        WriteTimeout = 1000,
    };
    private readonly LineSplitter splitter = new();
    private readonly Queue<string> ready = new();
    private readonly byte[] buffer = new byte[512];

    public DeviceCandidate Device => device;

    public void Open() => port.Open();

    public void WriteLine(string line) => Write(Encoding.ASCII.GetBytes(line + "\n"));

    public void Write(byte[] data) => port.Write(data, 0, data.Length);

    public string? ReadLine(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ready.Count == 0)
        {
            try
            {
                int n = port.Read(buffer, 0, buffer.Length);
                foreach (var line in splitter.Feed(buffer.AsSpan(0, n))) ready.Enqueue(line);
            }
            catch (TimeoutException)
            {
                if (DateTime.UtcNow >= deadline) return null;
            }
        }
        return ready.Dequeue();
    }

    public void Dispose() => port.Dispose();
}
