using OpenCvSharp;

namespace WiFiCameraLink;

/// <summary>One kind of camera: how to get frames out of it. Everything after that is shared.</summary>
public interface ICameraSource
{
    /// <summary>Short description for the log, e.g. "i4Season camera at 192.168.1.1".</summary>
    string Description { get; }

    event Action<CameraFrame>? FrameReceived;
    event Action<string>? Log;
    event Action<int>? BatteryChanged;

    /// <summary>
    /// Streams frames until cancelled (throwing OperationCanceledException) or the camera stops.
    /// Returns false, without streaming, if the camera doesn't speak this source's protocol.
    /// </summary>
    Task<bool> StreamAsync(CancellationToken ct);
}

/// <summary>The kinds of camera the app can talk to. Auto tries them in turn.</summary>
public enum CameraType
{
    Auto,
    I4Season,
    StreamUrl,
    VStarcam, // added last: the setting is saved as a number
}

/// <summary>
/// A standard video stream (RTSP, or HTTP MJPEG, as most IP cameras offer), read through OpenCV's FFmpeg backend.
/// Frames are passed on as JPEG like every other source.
/// </summary>
/// <remarks>
/// FFmpeg's sockets can't be pinned to the Wi-Fi adapter, so when Ethernet is on a network with the same
/// addresses as the camera's, this source may reach the wrong device; the i4Season source doesn't have that limit.
/// </remarks>
public sealed class StreamCameraSource : ICameraSource
{
    const int OpenTimeoutProperty = 53;  // CAP_PROP_OPEN_TIMEOUT_MSEC
    const int ReadTimeoutProperty = 54;  // CAP_PROP_READ_TIMEOUT_MSEC
    const int ThreadsProperty = 70;      // CAP_PROP_N_THREADS (set when opening)
    const int TimeoutMs = 5000;

    readonly string _url;
    readonly string _name;

    /// <param name="name">What to call the stream in the log; the URL if not given.</param>
    public StreamCameraSource(string url, string? name = null)
    {
        _url = url;
        _name = name ?? url;
    }

    public string Url => _url;

    public string Description => $"video stream {_url}";

    public event Action<CameraFrame>? FrameReceived;
    public event Action<string>? Log;
    public event Action<int>? BatteryChanged { add { } remove { } } // streams don't report a battery

    public Task<bool> StreamAsync(CancellationToken ct) => Task.Run(() => Stream(ct), ct);

    bool Stream(CancellationToken ct)
    {
        Log?.Invoke($"Opening {_name}...");
        using var capture = new VideoCapture(_url, VideoCaptureAPIs.FFMPEG,
            // One decoding thread: by default FFmpeg decodes several frames in parallel, one per CPU core, and holds
            // that many frames back. On a 12-core PC that delayed 15 fps video by ~800 ms.
            [OpenTimeoutProperty, TimeoutMs, ReadTimeoutProperty, TimeoutMs, ThreadsProperty, 1]);
        if (!capture.IsOpened())
        {
            Log?.Invoke($"Couldn't open {_name}.");
            return false;
        }
        Log?.Invoke($"Stream open: {capture.FrameWidth}x{capture.FrameHeight}, {capture.Fps:0} fps reported.");

        using var frame = new Mat();
        var quality = new ImageEncodingParam(ImwriteFlags.JpegQuality, 92);
        while (!ct.IsCancellationRequested)
        {
            // Read blocks until a frame arrives or the read timeout passes.
            if (!capture.Read(frame) || frame.Empty())
            {
                Log?.Invoke("The stream stopped sending video.");
                return true;
            }
            FrameReceived?.Invoke(new CameraFrame(frame.ImEncode(".jpg", quality), frame.Width, frame.Height));
        }
        ct.ThrowIfCancellationRequested();
        return true;
    }
}
