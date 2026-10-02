using System.Diagnostics;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>
/// Writes frames to an MP4 at a fixed rate (20 fps, or 40 with frame doubling), in real time: if frames arrive late the last one is repeated,
/// if they arrive early they're skipped, so the file plays back at the speed it was recorded.
/// Uses Windows' own H.264 encoder (Media Foundation), falling back to MPEG-4 Part 2.
/// </summary>
public sealed class Recorder : IDisposable
{
    readonly double _framesPerSecond;

    readonly VideoWriter _writer;
    readonly Size _size;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _lock = new();
    long _written;
    bool _closed;

    Recorder(VideoWriter writer, string path, Size size, double framesPerSecond)
    {
        _framesPerSecond = framesPerSecond;
        _writer = writer;
        Path = path;
        _size = size;
    }

    public string Path { get; }

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Opens a recording sized for the first frame; later frames of another size are letterboxed to fit.</summary>
    public static Recorder Start(string path, Size size, double framesPerSecond)
    {
        var writer = new VideoWriter(path, VideoCaptureAPIs.MSMF, FourCC.FromString("H264"), framesPerSecond, size);
        if (!writer.IsOpened())
        {
            writer.Dispose();
            writer = new VideoWriter(path, VideoCaptureAPIs.ANY, FourCC.FromString("mp4v"), framesPerSecond, size);
        }
        if (!writer.IsOpened())
        {
            writer.Dispose();
            throw new InvalidOperationException("No MP4 video encoder is available on this PC.");
        }
        return new Recorder(writer, path, size, framesPerSecond);
    }

    public void Write(Mat frame)
    {
        lock (_lock)
        {
            if (_closed)
                return;
            long due = (long)(_clock.Elapsed.TotalSeconds * _framesPerSecond) + 1; // frames that should exist by now
            if (due <= _written)
                return;

            using var fitted = frame.Size() == _size ? null : Letterbox(frame, _size);
            var output = fitted ?? frame;
            while (_written < due)
            {
                _writer.Write(output);
                _written++;
            }
        }
    }

    /// <summary>Scales the frame to fit inside the recording's size, centered on black.</summary>
    static Mat Letterbox(Mat frame, Size size)
    {
        double scale = Math.Min((double)size.Width / frame.Width, (double)size.Height / frame.Height);
        var scaled = new Size(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale)));
        var canvas = new Mat(size, MatType.CV_8UC3, Scalar.Black);
        using var resized = frame.Resize(scaled, 0, 0, InterpolationFlags.Area);
        using var target = new Mat(canvas, new Rect((size.Width - scaled.Width) / 2, (size.Height - scaled.Height) / 2, scaled.Width, scaled.Height));
        resized.CopyTo(target);
        return canvas;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed)
                return;
            _closed = true;
            _writer.Release();
            _writer.Dispose();
        }
    }
}
