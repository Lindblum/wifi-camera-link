using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WiFiCameraLink;

/// <summary>
/// VStarcam cameras: the ones the O-KAM and Eye4 apps talk to, with Wi-Fi names like "@IPC-8735500".
///
/// Port 81 answers CGI requests (get_status.cgi, ...) authenticated with loginuse/loginpas query parameters;
/// the factory password is 888888. livestream.cgi?streamid=10 answers with no HTTP header at all, just
/// a run of frames, each a 32-byte little-endian header (magic 55 AA 15 A8, frame type, ms, unix seconds,
/// frame number, payload length at offset 16, ...) followed by H.264 Annex B data. The app strips the
/// headers and relays the H.264 over loopback TCP to FFmpeg, which decodes it like any other stream.
/// </summary>
/// <remarks>
/// The camera's tiny CGI server stops answering for a while when it gets too many requests, so this asks
/// for as little as possible: a status request, the stream, and a battery reading every 15 seconds.
/// The camera also ends each livestream.cgi connection after about two minutes; the relay reconnects
/// behind the decoder's back, so the video only pauses briefly.
/// Background: https://palant.info/2025/11/05/an-overview-of-the-pppp-protocol-for-iot-cameras/
/// </remarks>
public sealed class VStarcamCamera : ICameraSource
{
    public const int CgiPort = 81;
    const uint FrameMagic = 0xA815AA55;
    const int FrameHeaderSize = 32;
    const int MaxFrameSize = 4 * 1024 * 1024;
    static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(15);
    // H.264 access unit delimiter. FFmpeg's raw H.264 reader only knows a frame is complete when the next one
    // starts; sending this right after each frame tells it at once, instead of a frame (67 ms) later.
    static readonly byte[] AccessUnitDelimiter = [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];

    readonly IPAddress _local;
    readonly IPAddress _camera;
    readonly string _password;

    public VStarcamCamera(IPAddress local, IPAddress camera, string password)
    {
        _local = local;
        _camera = camera;
        _password = password;
    }

    public string Description => $"VStarcam camera at {_camera}";

    /// <summary>Whether the camera draws its date/time on the picture; null until the stream has started.</summary>
    public bool? TimestampShown { get; private set; }

    public event Action<CameraFrame>? FrameReceived;
    public event Action<string>? Log;
    public event Action<int>? BatteryChanged;

    public async Task<bool> StreamAsync(CancellationToken ct)
    {
        string? status = await CgiAsync("get_status.cgi", ct);
        if (status == null || !status.Contains("var "))
        {
            Log?.Invoke($"The camera at {_camera} didn't answer VStarcam requests on TCP {CgiPort}.");
            return false;
        }
        if (status.Contains("Auth Failed"))
        {
            // It is a VStarcam camera, so don't fall through to other kinds; the password is what needs fixing.
            Log?.Invoke($"The VStarcam camera at {_camera} rejected the password. If it was changed in the O-KAM app, " +
                        $"put it in \"VStarcamPassword\" in {AppPaths.AppDir}\\settings.json.");
            return true;
        }
        Log?.Invoke($"Camera: VStarcam {Field(status, "deviceid")}, firmware {Field(status, "sys_ver")}, " +
                    $"sensor {Field(status, "sensor_name")} {Field(status, "sensor_width")}x{Field(status, "sensor_hight")}");
        ReportBattery(status);
        if (await CgiAsync("get_camera_params.cgi", ct) is { } parameters && Field(parameters, "OSDEnable") is "0" or "1")
            TimestampShown = Field(parameters, "OSDEnable") == "1";

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int relayPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var relay = Task.Run(() => RelayAsync(listener, stop.Token));
        var batteryPoll = Task.Run(() => PollBatteryAsync(stop.Token));

        var decoder = new StreamCameraSource($"tcp://127.0.0.1:{relayPort}", "the camera's H.264 video");
        decoder.FrameReceived += Forward;
        decoder.Log += ForwardLog;
        try
        {
            if (!await decoder.StreamAsync(stop.Token))
                Log?.Invoke("The camera's video couldn't be decoded.");
            return true;
        }
        finally
        {
            decoder.FrameReceived -= Forward;
            decoder.Log -= ForwardLog;
            stop.Cancel();
            try { await Task.WhenAll(relay, batteryPoll); }
            catch (OperationCanceledException) { }
        }

        void Forward(CameraFrame frame) => FrameReceived?.Invoke(frame);
        void ForwardLog(string message) => Log?.Invoke(message);
    }

    /// <summary>
    /// Waits for the decoder to connect, then copies the camera's H.264 to it without the frame headers,
    /// reconnecting to the camera whenever it ends the stream (it does so every two minutes).
    /// Leaving closes the decoder's connection, so its read ends and StreamAsync returns.
    /// </summary>
    async Task RelayAsync(TcpListener listener, CancellationToken ct)
    {
        using var decoder = await listener.AcceptTcpClientAsync(ct);
        var output = decoder.GetStream();
        bool reconnectLogged = false;
        long total = 0;
        for (int failures = 0; failures < 3 && !ct.IsCancellationRequested;)
        {
            var (frames, error) = await RelayConnectionAsync(output, total == 0, ct);
            total += frames;
            if (ct.IsCancellationRequested || error == null)
                return; // stopped, or the decoder went away
            if (frames == 0)
            {
                failures++;
                Log?.Invoke($"The camera's video didn't start: {error}");
                await Task.Delay(1000, ct);
                continue;
            }
            failures = 0;
            if (!reconnectLogged)
                Log?.Invoke($"The camera ended its video connection ({error}); it does this every two minutes. " +
                            "Reconnecting without stopping the video.");
            reconnectLogged = true;
        }
    }

    /// <summary>
    /// Relays one livestream.cgi connection. Returns the frames relayed and why the camera's side ended,
    /// or a null reason if relaying should stop for good (cancelled, decoder closed, or garbled data).
    /// </summary>
    async Task<(long Frames, string? Error)> RelayConnectionAsync(NetworkStream output, bool first, CancellationToken ct)
    {
        using var camera = NewTcpClient();
        var header = new byte[FrameHeaderSize];
        var payload = new byte[256 * 1024];
        bool keyFrameSeen = false;
        long frames = 0;
        try
        {
            await camera.ConnectAsync(_camera, CgiPort, ct);
            var cameraStream = camera.GetStream();
            await cameraStream.WriteAsync(Encoding.ASCII.GetBytes(Request("livestream.cgi", "streamid=10&substream=0")), ct);
            while (true)
            {
                await cameraStream.ReadExactlyAsync(header, ct);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != FrameMagic)
                {
                    Log?.Invoke($"The camera's video stream has an unexpected frame header: {Convert.ToHexString(header, 0, 8)}.");
                    return (frames, null);
                }
                int type = header[4];
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
                if (length is < 0 or > MaxFrameSize)
                {
                    Log?.Invoke($"The camera sent a frame of {length} bytes; stopping.");
                    return (frames, null);
                }
                if (payload.Length < length)
                    payload = new byte[length];
                await cameraStream.ReadExactlyAsync(payload.AsMemory(0, length), ct);

                // 0 = key frame (starts with SPS/PPS), 1 = other video frames; anything else (audio) is skipped.
                // Each connection starts at a key frame, which is also what lets the decoder carry on after a reconnect.
                if (type > 1 || (!keyFrameSeen && type != 0))
                    continue;
                keyFrameSeen = true;
                if (++frames == 1 && first)
                    Log?.Invoke("Video is arriving.");
                try
                {
                    await output.WriteAsync(payload.AsMemory(0, length), ct);
                    if (Environment.GetEnvironmentVariable("WCL_NO_AUD") != "1") // TEST: A/B switch, remove
                        await output.WriteAsync(AccessUnitDelimiter, ct);                }
                catch (IOException)
                {
                    return (frames, null); // the decoder closed its end
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (frames, null); }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            return (frames, ex is EndOfStreamException ? "end of stream" : ex.Message);
        }
    }

    /// <summary>Shows or hides the date/time the camera draws on the picture. Returns false if the camera refused.</summary>
    public async Task<bool> SetTimestampAsync(bool show, CancellationToken ct)
    {
        // camera_control.cgi parameter 10 is the on-screen display (the camera stores it).
        string? reply = await CgiAsync("camera_control.cgi", ct, $"param=10&value={(show ? 1 : 0)}");
        bool ok = reply != null && reply.Contains("\"ok\"");
        if (ok)
            TimestampShown = show;
        return ok;
    }

    async Task PollBatteryAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(BatteryPollInterval, ct);
            if (await CgiAsync("get_status.cgi", ct) is { } status)
                ReportBattery(status);
        }
    }

    void ReportBattery(string status)
    {
        if (int.TryParse(Field(status, "batteryRate"), out int percent) && percent is >= 0 and <= 100)
            BatteryChanged?.Invoke(percent);
    }

    /// <summary>Returns the body of a CGI response, or null if the camera doesn't answer.</summary>
    async Task<string?> CgiAsync(string cgi, CancellationToken ct, string? query = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(4000);
        using var client = NewTcpClient();
        try
        {
            await client.ConnectAsync(_camera, CgiPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Request(cgi, query)), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            string response = await reader.ReadToEndAsync(timeout.Token);
            int body = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return body < 0 ? null : response[(body + 4)..];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or SocketException) { return null; }
    }

    /// <summary>
    /// Sends both credential pairs, as VStarcam's own apps do: firmware versions differ in which one they check.
    /// </summary>
    string Request(string cgi, string? query)
    {
        string user = "admin", password = Uri.EscapeDataString(_password);
        string auth = $"loginuse={user}&loginpas={password}&user={user}&pwd={password}";
        return $"GET /{cgi}?{(query == null ? "" : query + "&")}{auth} HTTP/1.1\r\nHost: {_camera}:{CgiPort}\r\nConnection: close\r\n\r\n";
    }

    /// <summary>Reads one "var name=value;" line from a CGI response.</summary>
    static string Field(string response, string name) =>
        Regex.Match(response, $@"\bvar {Regex.Escape(name)}=""?([^"";\r\n]*)""?;") is { Success: true } m ? m.Groups[1].Value : "?";

    // Bound to our address on the camera network, so it goes through the Wi-Fi adapter even when Ethernet
    // uses the same addresses (see I4SeasonCamera).
    TcpClient NewTcpClient() => new(new IPEndPoint(_local, 0));
}
