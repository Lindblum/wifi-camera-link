using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>What the pipeline does to each frame. Immutable; the UI swaps in a new one when a setting changes.</summary>
public sealed record EnhanceOptions
{
    public int Rotation { get; init; }              // degrees clockwise: 0, 90, 180, 270
    public bool Denoise { get; init; }
    public int DenoiseStrength { get; init; } = 5;  // 1..10
    public bool Sharpen { get; init; }
    public int SharpenAmount { get; init; } = 4;    // 1..10
    public bool Stabilize { get; init; }
    public bool Interpolate { get; init; }          // double the frame rate with in-between frames
    public bool Sbs3D { get; init; }
    public int Depth3D { get; init; } = 5;          // 1..15
    public StereoLayout Layout { get; init; }
    public DepthSource DepthSource { get; init; }
    public int Convergence { get; init; } = 50;     // the depth, in %, that sits at the screen: 0 = all in front, 100 = all behind
    public int Smoothing { get; init; } = 50;       // % of the running depth kept when a new depth map arrives
    public int EdgeSnap { get; init; }              // 0..10: how closely depth edges follow the picture's edges
    public int EdgeDilation { get; init; }          // pixels to grow near depth outward, 0..30
    public HoleFill HoleFill { get; init; }
    public int DepthDetail { get; init; } = 518;    // AI depth models see about this squared in pixels
    public bool HalfSbs { get; init; }              // replaced by Layout; still read from older settings
    public bool SwapEyes { get; init; }
    public bool Stretch { get; init; }              // reshape to the display's aspect ratio, filling it
    public int MaxLines { get; init; }              // scale frames down so the shorter side is at most this; 0 = as sent

    public bool IsPassthrough => Rotation == 0 && !Denoise && !Sharpen && !Stabilize && !Sbs3D;
}

/// <summary>
/// Turns camera JPEGs into display bitmaps on a worker thread: rotate, stabilize, reduce grain, sharpen,
/// then optionally render 3D. Only the newest frame is processed if the worker falls behind.
/// </summary>
/// <summary>A frame's size as the camera sent it, and after resampling.</summary>
public sealed record FrameSizes(Size Native, Size Resampled);

/// <summary>Pipeline measurements since the last call to <see cref="VideoPipeline.TakeStats"/>.</summary>
public readonly record struct PipelineStats(int CameraFrames, int Dropped, int Output, double AverageLatencyMs, double MaxLatencyMs);

public sealed class VideoPipeline : IDisposable
{
    sealed record PendingFrame(CameraFrame Frame, long Arrival);

    readonly Thread _worker;
    readonly AutoResetEvent _wake = new(false);
    readonly Stabilizer _stabilizer = new();
    readonly Denoiser _denoiser = new();
    readonly StereoRenderer _stereo = new();
    readonly FrameInterpolator _interpolator = new();
    readonly Thread _presenter;
    readonly AutoResetEvent _presentWake = new(false);
    readonly object _presentLock = new();
    readonly Queue<(Mat Frame, long Due, long Arrival)> _queue = new(); // finished frames waiting for their display time
    long _lastDue;              // Stopwatch timestamp of the last queued frame, to keep the order
    double _processingTime = 0.02; // seconds from a frame's arrival to both of its outputs being ready (recent peak)
    long _lastArrival;
    double _frameInterval = 0.05; // seconds between camera frames, smoothed
    bool _highResolutionTimer;
    double _displayAspect;      // width / height of the video area, for Stretch
    readonly object _statsLock = new();
    int _statCamera, _statDropped, _statOutput;
    double _statLatencySum, _statLatencyMax;
    PendingFrame? _pending;
    volatile bool _disposed;
    volatile EnhanceOptions _options = new();
    volatile DepthEstimator? _depth;
    string? _lastError;
    readonly object _recordLock = new();
    string? _recordPath;    // set while recording is wanted; the file opens on the next frame
    Recorder? _recorder;

    public VideoPipeline()
    {
        // Priorities matter when 3D is on and the CPU is busy: frame timing first, then video
        // processing; the depth model runs below normal, as a slightly older depth map is harmless.
        _worker = new Thread(Run) { IsBackground = true, Name = "Video pipeline", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        _presenter = new Thread(RunPresenter) { IsBackground = true, Name = "Frame presenter", Priority = ThreadPriority.Highest };
        _presenter.Start();
    }

    public event Action<Bitmap>? FrameReady;
    public event Action<string>? Log;

    public EnhanceOptions Options
    {
        get => _options;
        set => _options = value;
    }

    /// <summary>Set once the depth model has loaded; until then 3D shows both eyes flat.</summary>
    public DepthEstimator? Depth
    {
        get => _depth;
        set => _depth = value;
    }

    public bool IsRecording
    {
        get { lock (_recordLock) return _recordPath != null; }
    }

    public TimeSpan RecordingElapsed
    {
        get { lock (_recordLock) return _recorder?.Elapsed ?? TimeSpan.Zero; }
    }

    /// <summary>Records what's displayed (after enhancement and 3D) to an MP4 at the given path.</summary>
    public void StartRecording(string path)
    {
        lock (_recordLock)
        {
            _recorder?.Dispose();
            _recorder = null;
            _recordPath = path;
        }
    }

    /// <summary>Stops recording and returns the file written, or null if nothing was recorded.</summary>
    public string? StopRecording()
    {
        lock (_recordLock)
        {
            string? path = _recorder?.Path;
            _recorder?.Dispose();
            _recorder = null;
            _recordPath = null;
            return path;
        }
    }

    void Record(Mat frame)
    {
        lock (_recordLock)
        {
            if (_recordPath == null)
                return;
            try
            {
                _recorder ??= Recorder.Start(_recordPath, frame.Size(), _options.Interpolate ? 40 : 20);
                _recorder.Write(frame);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Recording stopped: {ex.Message}");
                _recorder?.Dispose();
                _recorder = null;
                _recordPath = null;
            }
        }
    }

    /// <summary>Width divided by height of the area the video is shown in; Stretch reshapes frames to it.</summary>
    public double DisplayAspect
    {
        get => Volatile.Read(ref _displayAspect);
        set => Volatile.Write(ref _displayAspect, value);
    }

    /// <summary>Counts and latency (camera frame arriving to the finished frame being handed over for display).</summary>
    /// <summary>The latest frame's size before and after resampling; null before the first frame.</summary>
    public FrameSizes? Sizes { get; private set; }

    public PipelineStats TakeStats()
    {
        lock (_statsLock)
        {
            var stats = new PipelineStats(_statCamera, _statDropped, _statOutput,
                _statOutput > 0 ? _statLatencySum / _statOutput : 0, _statLatencyMax);
            _statCamera = _statDropped = _statOutput = 0;
            _statLatencySum = _statLatencyMax = 0;
            return stats;
        }
    }

    public void Submit(CameraFrame frame)
    {
        if (_disposed)
            return; // a frame that arrived while the app was closing
        // Stamp the arrival time now: frame timing must count any wait for the worker to free up.
        var replaced = Interlocked.Exchange(ref _pending, new PendingFrame(frame, Stopwatch.GetTimestamp()));
        lock (_statsLock)
        {
            _statCamera++;
            if (replaced != null)
                _statDropped++; // the worker was still busy with an earlier frame
        }
        try { _wake.Set(); }
        catch (ObjectDisposedException) { } // Dispose ran between the check above and here
    }

    void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed)
                return;
            if (Interlocked.Exchange(ref _pending, null) is not { } pending)
                continue;
            try
            {
                Process(pending.Frame, pending.Arrival);
                _lastError = null;
            }
            catch (Exception ex) when (!_disposed)
            {
                if (ex.Message != _lastError) // report each distinct problem once, not 20 times a second
                    Log?.Invoke($"Video processing error: {ex.Message}");
                _lastError = ex.Message;
            }
        }
    }

    void Process(CameraFrame frame, long arrival)
    {
        var o = _options;
        if (_lastArrival != 0)
        {
            double gap = (arrival - _lastArrival) / (double)Stopwatch.Frequency;
            if (gap < 0.2) // ignore pauses in the stream
                _frameInterval += 0.1 * (gap - _frameInterval);
        }
        _lastArrival = arrival;

        var current = Cv2.ImDecode(frame.Jpeg, ImreadModes.Color);
        try
        {
            if (current.Empty())
                return;

            // Scale down first, so every later step (and 3D especially) has fewer pixels to work on.
            var native = current.Size();
            int shorter = Math.Min(current.Width, current.Height);
            if (o.MaxLines > 0 && shorter > o.MaxLines)
            {
                double scale = (double)o.MaxLines / shorter;
                Replace(ref current, current.Resize(new Size(0, 0), scale, scale, InterpolationFlags.Area));
            }
            if (Sizes is not { } sizes || sizes.Native != native || sizes.Resampled != current.Size())
                Sizes = new FrameSizes(native, current.Size());

            if (o.Rotation != 0)
                Replace(ref current, Rotate(current, o.Rotation));

            // Stabilize first so the grain filter compares pixels that line up between frames.
            if (o.Stabilize)
                Replace(ref current, _stabilizer.Apply(current));
            else
                _stabilizer.Reset();

            if (o.Denoise)
                Replace(ref current, _denoiser.Apply(current, o.DenoiseStrength));
            else
                _denoiser.Reset();

            // Sharpen after denoising, or it would sharpen the grain too.
            if (o.Sharpen)
                Replace(ref current, Sharpen(current, o.SharpenAmount));

            using var middle = o.Interpolate ? _interpolator.Interpolate(current) : null;
            if (!o.Interpolate)
                _interpolator.Reset();

            if (middle != null)
            {
                UseHighResolutionTimer();
                var between = Finish(middle, o, realFrame: false);
                var real = Finish(current, o, realFrame: true);

                // Time both from when the camera frame arrived, not from when processing happened to
                // finish, so uneven processing (3D especially) doesn't make the motion uneven: the
                // in-between frame after a short fixed delay, the real one half a frame later. The delay
                // follows the recent slowest frame (easing down when things speed up), so an occasional
                // slow frame still makes its slot instead of causing a stutter.
                double took = (Stopwatch.GetTimestamp() - arrival) / (double)Stopwatch.Frequency;
                _processingTime = Math.Max(took, _processingTime * 0.997); // halves in ~10 s
                double delay = Math.Clamp(_processingTime + 0.005, 0.01, 0.12);
                long due = arrival + (long)(delay * Stopwatch.Frequency);
                Enqueue(between, due, arrival);
                Enqueue(real, due + (long)(_frameInterval / 2 * Stopwatch.Frequency), arrival);
            }
            else if (HasQueuedFrames())
            {
                Enqueue(Finish(current, o, realFrame: true), 0, arrival); // doubling just turned off: keep the order
            }
            else
            {
                Present(Finish(current, o, realFrame: true), arrival);
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>Final steps for display: 3D, then stretching. Returns a new Mat the caller owns.</summary>
    Mat Finish(Mat frame, EnhanceOptions o, bool realFrame)
    {
        Mat result;
        if (o.Sbs3D)
        {
            var depthEstimator = _depth;
            if (realFrame)
                depthEstimator?.Submit(frame, o); // in-between frames reuse the real frames' depth
            using var depth = depthEstimator?.GetDepth(frame.Size());
            result = _stereo.Render(frame, depth, o);
        }
        else
        {
            result = frame.Clone();
        }

        // Last: reshape to the display's proportions. Done here rather than by the display so snapshots
        // and recordings match what's on screen. Height stays; width changes (e.g. half side-by-side
        // 640x480 becomes 853x480 for a 16:9 screen, the way 3D TVs expect it).
        double aspect = DisplayAspect;
        if (o.Stretch && aspect > 0.1)
        {
            int width = Math.Max(2, (int)Math.Round(result.Height * aspect / 2) * 2); // even, for the encoder
            if (Math.Abs(width - result.Width) > 2)
            {
                var stretched = result.Resize(new Size(width, result.Height), 0, 0, InterpolationFlags.Linear);
                result.Dispose();
                result = stretched;
            }
        }
        return result;
    }

    /// <summary>Records and displays a finished frame, then frees it. Called from the worker or presenter thread.</summary>
    void Present(Mat finished, long arrival)
    {
        try
        {
            Record(finished);
            FrameReady?.Invoke(ToBitmap(finished));
            double latency = (Stopwatch.GetTimestamp() - arrival) * 1000.0 / Stopwatch.Frequency;
            lock (_statsLock)
            {
                _statOutput++;
                _statLatencySum += latency;
                _statLatencyMax = Math.Max(_statLatencyMax, latency);
            }
        }
        finally
        {
            finished.Dispose();
        }
    }

    void Enqueue(Mat finished, long due, long arrival)
    {
        lock (_presentLock)
        {
            due = Math.Max(due, _lastDue + 1); // never ahead of a frame queued earlier
            _lastDue = due;
            _queue.Enqueue((finished, due, arrival));
        }
        _presentWake.Set();
    }

    bool HasQueuedFrames()
    {
        lock (_presentLock)
            return _queue.Count > 0;
    }

    /// <summary>Shows each queued frame at its time, to the millisecond, so 40 fps comes out evenly spaced.</summary>
    void RunPresenter()
    {
        while (true)
        {
            _presentWake.WaitOne();
            while (!_disposed)
            {
                (Mat Frame, long Due, long Arrival) next;
                lock (_presentLock)
                {
                    if (_queue.Count == 0)
                        break;
                    next = _queue.Peek();
                }
                long remaining = next.Due - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    if (remaining * 1000 / Stopwatch.Frequency > 2)
                        Thread.Sleep(1);
                    else
                        Thread.Yield();
                    continue;
                }
                lock (_presentLock)
                    _queue.Dequeue();
                try
                {
                    Present(next.Frame, next.Arrival);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Video processing error: {ex.Message}");
                }
            }
            if (_disposed)
                return;
        }
    }

    /// <summary>Windows sleeps in ~16 ms steps by default; 1 ms steps keep the in-between frames evenly timed.</summary>
    void UseHighResolutionTimer()
    {
        if (_highResolutionTimer)
            return;
        timeBeginPeriod(1);
        _highResolutionTimer = true;
    }

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint milliseconds);

    static void Replace(ref Mat current, Mat next)
    {
        if (!ReferenceEquals(current, next))
            current.Dispose();
        current = next;
    }

    static Mat Rotate(Mat frame, int degrees)
    {
        var rotated = new Mat();
        Cv2.Rotate(frame, rotated, degrees switch
        {
            90 => RotateFlags.Rotate90Clockwise,
            180 => RotateFlags.Rotate180,
            _ => RotateFlags.Rotate90Counterclockwise,
        });
        return rotated;
    }

    /// <summary>Unsharp mask: add back the difference between the frame and a blurred copy.</summary>
    static Mat Sharpen(Mat frame, int amount)
    {
        double weight = amount * 0.15; // 0.15 .. 1.5
        using var blurred = new Mat();
        Cv2.GaussianBlur(frame, blurred, new Size(0, 0), 1.2);
        var sharpened = new Mat();
        Cv2.AddWeighted(frame, 1 + weight, blurred, -weight, 0, sharpened);
        return sharpened;
    }

    static unsafe Bitmap ToBitmap(Mat bgr)
    {
        var bitmap = new Bitmap(bgr.Width, bgr.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bgr.Width, bgr.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            long rowBytes = bgr.Width * 3L;
            for (int y = 0; y < bgr.Height; y++)
                Buffer.MemoryCopy((void*)bgr.Ptr(y), (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _worker.Join(2000);
        _presentWake.Set();
        _presenter.Join(2000);
        lock (_presentLock)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Frame.Dispose();
        }
        StopRecording();
        _interpolator.Dispose();
        if (_highResolutionTimer)
            timeEndPeriod(1);
        _stabilizer.Reset();
        _denoiser.Reset();
        _wake.Dispose();
        _presentWake.Dispose();
    }
}

/// <summary>
/// Removes shake: tracks corner features between frames to measure how the view moved, keeps a smoothed
/// version of that path, and shifts/rotates each frame onto it. A slight zoom hides the moving edges.
/// </summary>
sealed class Stabilizer
{
    const double Smoothing = 0.9;   // per frame; higher is steadier but follows deliberate moves more slowly
    const double Zoom = 1.08;       // 8% crop gives room to shift without showing borders
    const double MaxAngle = 0.05;   // radians of rotation correction (~3 degrees)

    Mat? _previousGray;
    double _x, _y, _angle;                          // where the camera actually went
    double _smoothX, _smoothY, _smoothAngle;        // where a steady hand would have gone

    public void Reset()
    {
        _previousGray?.Dispose();
        _previousGray = null;
        _x = _y = _angle = _smoothX = _smoothY = _smoothAngle = 0;
    }

    public Mat Apply(Mat frame)
    {
        var gray = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        if (_previousGray == null || _previousGray.Size() != gray.Size())
        {
            Reset();
            _previousGray = gray;
            return Warp(frame, 0, 0, 0);
        }

        var (dx, dy, da) = EstimateMotion(_previousGray, gray);
        _previousGray.Dispose();
        _previousGray = gray;

        _x += dx;
        _y += dy;
        _angle += da;
        _smoothX = Smoothing * _smoothX + (1 - Smoothing) * _x;
        _smoothY = Smoothing * _smoothY + (1 - Smoothing) * _y;
        _smoothAngle = Smoothing * _smoothAngle + (1 - Smoothing) * _angle;

        // Never correct further than the zoom margin; pulling the smoothed path along on big moves
        // also stops a deliberate pan from lagging behind for long.
        double maxX = frame.Width * (Zoom - 1) / 2, maxY = frame.Height * (Zoom - 1) / 2;
        double correctX = Math.Clamp(_smoothX - _x, -maxX, maxX);
        double correctY = Math.Clamp(_smoothY - _y, -maxY, maxY);
        double correctAngle = Math.Clamp(_smoothAngle - _angle, -MaxAngle, MaxAngle);
        _smoothX = _x + correctX;
        _smoothY = _y + correctY;
        _smoothAngle = _angle + correctAngle;

        return Warp(frame, correctX, correctY, correctAngle);
    }

    static (double Dx, double Dy, double Da) EstimateMotion(Mat previous, Mat current)
    {
        var from = Cv2.GoodFeaturesToTrack(previous, 200, 0.01, 15, null!, 3, false, 0.04);
        if (from.Length < 8)
            return (0, 0, 0); // too dark or featureless to measure; assume no shake

        var to = new Point2f[from.Length];
        Cv2.CalcOpticalFlowPyrLK(previous, current, from, ref to, out byte[] status, out _);
        var tracked = Enumerable.Range(0, from.Length).Where(i => status[i] == 1).ToArray();
        if (tracked.Length < 8)
            return (0, 0, 0);

        using var fromPoints = InputArray.Create(tracked.Select(i => from[i]).ToArray());
        using var toPoints = InputArray.Create(tracked.Select(i => to[i]).ToArray());
        using var transform = Cv2.EstimateAffinePartial2D(fromPoints, toPoints);
        if (transform == null || transform.Empty())
            return (0, 0, 0);
        return (transform.At<double>(0, 2), transform.At<double>(1, 2),
                Math.Atan2(transform.At<double>(1, 0), transform.At<double>(0, 0)));
    }

    static Mat Warp(Mat frame, double shiftX, double shiftY, double angle)
    {
        var center = new Point2f(frame.Width / 2f, frame.Height / 2f);
        // GetRotationMatrix2D turns the other way from the measured angle, hence the minus.
        using var matrix = Cv2.GetRotationMatrix2D(center, -angle * 180 / Math.PI, Zoom);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + shiftX * Zoom);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + shiftY * Zoom);
        var warped = new Mat();
        Cv2.WarpAffine(frame, warped, matrix, frame.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        return warped;
    }
}

/// <summary>
/// Reduces grain by averaging over time where the picture is still (grain changes every frame, the scene
/// doesn't) while following new frames where things move, then a light edge-preserving blur.
/// </summary>
sealed class Denoiser
{
    Mat? _average; // running average, CV_32FC3

    public void Reset()
    {
        _average?.Dispose();
        _average = null;
    }

    public Mat Apply(Mat frame, int strength)
    {
        using var current = new Mat();
        frame.ConvertTo(current, MatType.CV_32FC3);

        if (_average == null || _average.Size() != current.Size())
        {
            Reset();
            _average = current.Clone();
        }
        else
        {
            float motionThreshold = 8 + 3f * strength;  // per-pixel change (0..255) that counts as motion
            float maxHistory = 0.5f + 0.04f * strength; // share of the average a still pixel keeps

            using var difference = new Mat();
            Cv2.Absdiff(current, _average, difference);
            using var change = difference.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(change, change, new Size(5, 5), 0);

            // keep = maxHistory * (1 - change / threshold), floored at 0
            using var keep = new Mat();
            change.ConvertTo(keep, MatType.CV_32FC1, -maxHistory / motionThreshold, maxHistory);
            Cv2.Max(keep, 0, keep);
            using var keep3 = new Mat();
            Cv2.Merge([keep, keep, keep], keep3);

            // average = current + keep * (average - current)
            using var delta = new Mat();
            Cv2.Subtract(_average, current, delta);
            Cv2.Multiply(delta, keep3, delta);
            Cv2.Add(current, delta, _average);
        }

        using var averaged = new Mat();
        _average.ConvertTo(averaged, MatType.CV_8UC3);
        var result = new Mat();
        Cv2.BilateralFilter(averaged, result, 5, 10 + 4 * strength, 5);
        return result;
    }
}
