using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WiFiCameraLink;

public sealed record CameraFrame(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Client for i4season ("YPC") Wi-Fi cameras, the family the ANESOK app talks to.
///
/// Control is UDP with a 12-byte little-endian header (magic FFEEFFEE, seq, cmd, 0x01, status, payload length).
/// Device info goes to port 10005. OpenVideo (port 10006) names a UDP port of ours; the camera then sends JPEG
/// frames there, one chunk per datagram with a 16-byte header. The app re-sends OpenVideo about once a second
/// whenever video stops arriving. Protocol notes: https://github.com/rbeilvert/otoscope/issues/38
/// </summary>
public sealed class I4SeasonCamera : ICameraSource
{
    const uint Magic = 0xFFEEFFEE;
    const int InfoPort = 10005;
    const int VideoControlPort = 10006;
    const ushort CmdDeviceInfo = 0x0001;
    const ushort CmdOpenVideo = 0x0004;
    const int BatteryOffset = 0x77; // u16 in the device-info payload; percent is the top 7 bits
    static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(15);
    const int SioUdpConnReset = -1744830452;

    readonly IPAddress _local;
    readonly IPAddress _camera;
    readonly uint _clientId = (uint)Random.Shared.Next();
    int _seq;
    long _lastVideoTick;

    public I4SeasonCamera(IPAddress local, IPAddress camera)
    {
        _local = local;
        _camera = camera;
    }

    public string Description => $"i4Season camera at {_camera}";

    public event Action<CameraFrame>? FrameReceived;
    public event Action<string>? Log;
    public event Action<int>? BatteryChanged;

    /// <summary>
    /// Handshakes with the camera and streams frames until cancelled.
    /// Returns false (without streaming) if the camera doesn't answer the handshake.
    /// </summary>
    public async Task<bool> StreamAsync(CancellationToken ct)
    {
        // Sockets are bound to our address on the camera network, so Windows sends through the Wi-Fi
        // adapter even if Ethernet is on a home network that uses the same 192.168.1.x addresses.
        using var control = NewUdpClient();
        using var video = NewUdpClient();
        video.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        int picPort = ((IPEndPoint)video.Client.LocalEndPoint!).Port;

        var info = await GetDeviceInfoAsync(control, ct);
        if (info == null)
        {
            Log?.Invoke($"The camera at {_camera} didn't answer the i4season handshake on UDP {InfoPort}.");
            return false;
        }
        Log?.Invoke($"Camera: {DescribeDeviceInfo(info)}");
        ReportBattery(info);

        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), (ushort)picPort);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 0); // no audio
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), _clientId);
        var videoControl = new IPEndPoint(_camera, VideoControlPort);

        // OpenVideo goes out from the video socket itself. The camera streams from the port we sent to,
        // so Windows Firewall sees the video as replies to our own traffic instead of blocking it as unsolicited.
        using var stopKeepAlive = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAlive = Task.Run(async () =>
        {
            while (!stopKeepAlive.IsCancellationRequested)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastVideoTick) > 1000)
                    await video.SendAsync(Packet(CmdOpenVideo, payload), videoControl, stopKeepAlive.Token);
                await Task.Delay(1000, stopKeepAlive.Token);
            }
        });

        // The camera also pushes status to UDP 10007, but the firewall drops that unsolicited traffic,
        // so the battery is polled with device-info requests instead.
        var batteryPoll = Task.Run(async () =>
        {
            while (!stopKeepAlive.IsCancellationRequested)
            {
                await Task.Delay(BatteryPollInterval, stopKeepAlive.Token);
                try
                {
                    if (await GetDeviceInfoAsync(control, stopKeepAlive.Token) is { } latest)
                        ReportBattery(latest);
                }
                catch (SocketException) { } // a missed poll is fine; try again next time
            }
        });

        Log?.Invoke($"Requested video on UDP port {picPort}.");
        var assembler = new FrameAssembler();
        long packets = 0, frames = 0;
        try
        {
            while (true)
            {
                var result = await video.ReceiveAsync(ct);
                if (result.Buffer.Length > 0 && result.Buffer[0] != 1)
                    continue; // command acknowledgement, not video
                Interlocked.Exchange(ref _lastVideoTick, Environment.TickCount64);
                if (++packets == 1)
                    Log?.Invoke("Video packets are arriving.");
                if (assembler.Add(result.Buffer) is { } frame)
                {
                    if (++frames == 1)
                        Log?.Invoke($"First frame: {frame.Width}x{frame.Height}, {frame.Jpeg.Length / 1024} KB.");
                    FrameReceived?.Invoke(frame);
                }
                else if (packets == 500 && frames == 0)
                {
                    Log?.Invoke("500 video packets but no complete frame; the chunk format may differ from what's expected.");
                }
            }
        }
        finally
        {
            stopKeepAlive.Cancel();
            try { await Task.WhenAll(keepAlive, batteryPoll); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Returns the device-info payload, or null if the camera doesn't answer.</summary>
    async Task<byte[]?> GetDeviceInfoAsync(UdpClient control, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await control.SendAsync(Packet(CmdDeviceInfo, []), new IPEndPoint(_camera, InfoPort), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            try
            {
                while (true)
                {
                    var reply = await control.ReceiveAsync(timeout.Token);
                    var data = reply.Buffer;
                    if (data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic &&
                        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) == CmdDeviceInfo)
                        return data[12..];
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    void ReportBattery(byte[] info)
    {
        if (info.Length >= BatteryOffset + 2)
            BatteryChanged?.Invoke(Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(BatteryOffset)) >> 9, 0, 100));
    }

    /// <summary>The info block is a set of NUL-padded text fields (vendor, product, firmware, SSID...).</summary>
    static string DescribeDeviceInfo(ReadOnlySpan<byte> payload)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        foreach (byte b in payload)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                current.Append((char)b);
                continue;
            }
            if (current.Length >= 3 && !fields.Contains(current.ToString()))
                fields.Add(current.ToString());
            current.Clear();
        }
        if (current.Length >= 3)
            fields.Add(current.ToString());
        return fields.Count > 0 ? string.Join(", ", fields) : "(no info)";
    }

    byte[] Packet(ushort cmd, byte[] payload)
    {
        var packet = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)Interlocked.Increment(ref _seq));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), cmd);
        packet[8] = 0x01; // request
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), (ushort)payload.Length);
        payload.CopyTo(packet, 12);
        return packet;
    }

    UdpClient NewUdpClient()
    {
        var udp = new UdpClient(new IPEndPoint(_local, 0));
        udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        return udp;
    }

    /// <summary>
    /// Rebuilds JPEGs from video datagrams. Chunk header: type(1), packet seq (wraps at 256), frame id,
    /// last-chunk flag, chunk count, flags, u32 accelerometer, 2-byte MAC tail, i16 width, i16 height.
    /// </summary>
    sealed class FrameAssembler
    {
        const int HeaderSize = 16;
        readonly MemoryStream _jpeg = new();
        int _frameId = -1;
        int _expectedChunks;
        int _chunks;
        int _lastSeq;
        bool _broken;

        public CameraFrame? Add(byte[] d)
        {
            if (d.Length <= HeaderSize || d[0] != 1)
                return null;

            int seq = d[1], frameId = d[2], chunkCount = d[4];
            bool last = d[3] != 0;
            if (frameId != _frameId)
            {
                _frameId = frameId;
                _expectedChunks = chunkCount;
                _chunks = 0;
                _broken = false;
                _jpeg.SetLength(0);
            }
            else if (seq != ((_lastSeq + 1) & 0xFF))
            {
                _broken = true; // a chunk was lost or reordered
            }
            _lastSeq = seq;
            _jpeg.Write(d, HeaderSize, d.Length - HeaderSize);
            _chunks++;

            if (!last)
                return null;
            _frameId = -1;
            if (_broken || _chunks != _expectedChunks)
                return null;

            // The last chunk is zero-padded after the JPEG end marker.
            var bytes = _jpeg.GetBuffer().AsSpan(0, (int)_jpeg.Length);
            int end = bytes.LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]);
            if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8 || end < 0)
                return null;
            return new CameraFrame(
                bytes[..(end + 2)].ToArray(),
                BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(12)),
                BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(14)));
        }
    }
}
