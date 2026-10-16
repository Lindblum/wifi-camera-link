using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>
/// Estimates depth for 3D with one of the <see cref="DepthSource"/>s. Estimation (~110 ms per map for Depth
/// Anything V2 on an RX 570) runs on its own thread; the video keeps its frame rate by reusing the most recent
/// depth map, which is also smoothed over time so it doesn't flicker.
/// </summary>
public sealed class DepthEstimator : IDisposable
{
    const float RangeBlend = 0.2f;      // how fast the near/far normalization range follows the scene

    readonly IDepthModel _model;
    readonly Thread _worker;
    readonly AutoResetEvent _wake = new(false);
    readonly object _lock = new();
    (Mat Frame, EnhanceOptions Options)? _pending; // newest frame waiting for estimation
    Mat? _depth;        // latest depth, frame-sized CV_32F, 0 = far .. 1 = near
    Mat? _state;        // the smoothed depth at the model's resolution
    Mat? _stateGray;    // the picture's brightness when _state was last updated, to spot motion
    float _near = float.NaN, _far = float.NaN;
    int _completed;
    volatile bool _disposed;

    DepthEstimator(IDepthModel model, DepthSource source)
    {
        _model = model;
        Source = source;
        _worker = new Thread(Run) { IsBackground = true, Name = "Depth", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public DepthSource Source { get; }

    public string Device => _model.Device;

    public string? LastError { get; private set; }

    /// <summary>Depth maps finished since the last call (for a depth-rate readout).</summary>
    public int TakeCompletedCount() => Interlocked.Exchange(ref _completed, 0);

    /// <summary>Loads the source's model, downloading it the first time.</summary>
    public static async Task<DepthEstimator> CreateAsync(DepthSource source, Action<string> log, CancellationToken ct = default) =>
        new(await DepthModels.CreateAsync(source, log, ct), source);

    /// <summary>
    /// Queues a frame for depth estimation. If the estimator is still busy, it replaces any older waiting
    /// frame. Safe to call after <see cref="Dispose"/>, which the UI may do while the pipeline is mid-frame.
    /// </summary>
    public void Submit(Mat frame, EnhanceOptions options)
    {
        if (_disposed)
            return;
        var copy = frame.Clone();
        lock (_lock)
        {
            _pending?.Frame.Dispose();
            _pending = (copy, options);
        }
        _wake.Set();
    }

    /// <summary>A copy of the latest depth map if it matches the frame size, else null.</summary>
    public Mat? GetDepth(Size size)
    {
        lock (_lock)
            return _depth != null && _depth.Size() == size ? _depth.Clone() : null;
    }

    void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed)
                return;

            (Mat Frame, EnhanceOptions Options)? pending;
            lock (_lock)
            {
                pending = _pending;
                _pending = null;
            }
            if (pending is not var (frame, options))
                continue;

            using (frame)
            {
                try
                {
                    var depth = Update(frame, options);
                    lock (_lock)
                    {
                        _depth?.Dispose();
                        _depth = depth;
                    }
                    Interlocked.Increment(ref _completed);
                    LastError = null;
                }
                catch (Exception ex) when (!_disposed)
                {
                    LastError = ex.Message;
                }
            }
        }
    }

    /// <summary>Estimates the frame's depth, blends it into the running depth and returns that at frame size.</summary>
    Mat Update(Mat frame, EnhanceOptions o)
    {
        using var raw = _model.Estimate(frame, o.DepthDetail);
        Normalize(raw);

        using var gray = new Mat();
        using (var small = frame.Resize(raw.Size(), 0, 0, InterpolationFlags.Area))
            Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        gray.ConvertTo(gray, MatType.CV_32FC1, 1 / 255.0);

        if (_state == null || _state.Size() != raw.Size())
        {
            _state?.Dispose();
            _state = raw.Clone();
        }
        else
        {
            // Each new map's weight is 1 - smoothing, but where the picture changed since the last map it rises
            // to 1, so moving things don't drag a ghost of their old depth behind them (as in the 2D-3D add-on).
            using var weight = new Mat();
            Cv2.Absdiff(gray, _stateGray!, weight);
            weight.ConvertTo(weight, MatType.CV_32FC1, 1 / 0.12, -0.03 / 0.12); // 0 at a change of 0.03, 1 at 0.15
            Cv2.Max(weight, 1 - o.Smoothing / 100.0, weight);
            Cv2.Min(weight, 1, weight);
            using var change = new Mat();
            Cv2.Subtract(raw, _state, change);
            Cv2.Multiply(change, weight, change);
            Cv2.Add(_state, change, _state);
        }
        _stateGray?.Dispose();
        _stateGray = gray.Clone();

        var depth = new Mat();
        Cv2.Resize(_state, depth, frame.Size(), 0, 0, InterpolationFlags.Linear);
        // Soft depth edges give fewer tearing artifacts when pixels are shifted for each eye.
        Cv2.GaussianBlur(depth, depth, new Size(0, 0), 2.5);
        return depth;
    }

    /// <summary>
    /// Maps the model's relative depth (bigger = nearer) to 0..1, using the 2nd-98th percentile so a few
    /// extreme pixels don't flatten the scene, and easing that range over time so the 3D doesn't "breathe".
    /// </summary>
    void Normalize(Mat depth)
    {
        using var sample = depth.Resize(new Size(64, 48), 0, 0, InterpolationFlags.Area);
        sample.GetArray(out float[] values);
        Array.Sort(values);
        float far = values[(int)(values.Length * 0.02)], near = values[(int)(values.Length * 0.98)];

        _far = float.IsNaN(_far) ? far : _far + RangeBlend * (far - _far);
        _near = float.IsNaN(_near) ? near : _near + RangeBlend * (near - _near);
        float range = Math.Max(_near - _far, 1e-6f);

        depth.ConvertTo(depth, MatType.CV_32FC1, 1 / range, -_far / range);
        Cv2.Max(depth, 0, depth);
        Cv2.Min(depth, 1, depth);
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set(); // not disposed itself: the pipeline may still call Submit, which only checks _disposed
        _worker.Join(2000);
        _model.Dispose();
        lock (_lock)
        {
            _pending?.Frame.Dispose();
            _pending = null;
            _depth?.Dispose();
            _depth = null;
        }
        _state?.Dispose();
        _stateGray?.Dispose();
    }
}
