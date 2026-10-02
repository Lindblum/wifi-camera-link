using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>
/// Estimates depth from a single image with Depth Anything V2 Small (Apache-2.0), run through ONNX Runtime
/// on the GPU via DirectML. Inference (~110 ms on an RX 570) runs on its own thread; the video keeps its
/// frame rate by reusing the most recent depth map, which is also smoothed over time so it doesn't flicker.
/// </summary>
public sealed class DepthEstimator : IDisposable
{
    const string ModelUrl = "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx";
    static readonly string ModelPath = Path.Combine(AppPaths.AppDir, "models", "depth_anything_v2_small.onnx");

    const int InputSize = 518;          // the model's native size; a multiple of its 14-pixel patches
    const float DepthBlend = 0.5f;      // weight of each new depth map against the running one
    const float RangeBlend = 0.2f;      // how fast the near/far normalization range follows the scene
    static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    readonly InferenceSession _session;
    readonly string _inputName;
    readonly Thread _worker;
    readonly AutoResetEvent _wake = new(false);
    readonly object _lock = new();
    Mat? _pending;      // newest frame waiting for inference
    Mat? _depth;        // latest depth, frame-sized CV_32F, 0 = far .. 1 = near
    float _near = float.NaN, _far = float.NaN;
    int _completed;
    volatile bool _disposed;

    DepthEstimator(InferenceSession session, string device)
    {
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
        Device = device;
        _worker = new Thread(Run) { IsBackground = true, Name = "Depth", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public string Device { get; }

    public string? LastError { get; private set; }

    /// <summary>Depth maps finished since the last call (for a depth-rate readout).</summary>
    public int TakeCompletedCount() => Interlocked.Exchange(ref _completed, 0);

    public static async Task<DepthEstimator> CreateAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!File.Exists(ModelPath))
            await DownloadModelAsync(log, ct);

        return await Task.Run(() =>
        {
            try
            {
                var options = new SessionOptions
                {
                    EnableMemoryPattern = false, // required by DirectML
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                    // The GPU does the work; by default ONNX Runtime would also keep a busy-waiting thread
                    // on every core, which halves the speed of the video processing running alongside.
                    IntraOpNumThreads = 1,
                    InterOpNumThreads = 1,
                };
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
                options.AppendExecutionProvider_DML(0); // default adapter: the RX 570 here, ~8x faster than the Intel GPU
                return new DepthEstimator(new InferenceSession(ModelPath, options), "GPU (DirectML)");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                log($"GPU depth isn't available ({ex.Message.Split('\n')[0]}); using the CPU, which manages only a few depth maps per second.");
                var options = new SessionOptions
                {
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                return new DepthEstimator(new InferenceSession(ModelPath, options), "CPU");
            }
        }, ct);
    }

    static async Task DownloadModelAsync(Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        string partial = ModelPath + ".part";
        log("Downloading the 3D depth model (99 MB, first time only)...");

        using var http = new HttpClient();
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int lastReported = 0, read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent >= lastReported + 25)
                {
                    lastReported = percent / 25 * 25;
                    log($"  ...{lastReported}%");
                }
            }
        }
        File.Move(partial, ModelPath, overwrite: true);
        log("Depth model downloaded.");
    }

    /// <summary>Queues a frame for depth estimation. If the GPU is still busy, it replaces any older waiting frame.</summary>
    public void Submit(Mat frame)
    {
        var copy = frame.Clone();
        lock (_lock)
        {
            _pending?.Dispose();
            _pending = copy;
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

            Mat? frame;
            lock (_lock)
            {
                frame = _pending;
                _pending = null;
            }
            if (frame == null)
                continue;

            using (frame)
            {
                try
                {
                    var depth = Infer(frame);
                    lock (_lock)
                    {
                        if (_depth != null && _depth.Size() == depth.Size())
                        {
                            Cv2.AddWeighted(depth, DepthBlend, _depth, 1 - DepthBlend, 0, _depth);
                            depth.Dispose();
                        }
                        else
                        {
                            _depth?.Dispose();
                            _depth = depth;
                        }
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

    Mat Infer(Mat bgr)
    {
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        using (var resized = bgr.Resize(new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Area))
            FillTensor(resized, tensor.Buffer.Span);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results[0].AsTensor<float>();
        int height = output.Dimensions[^2], width = output.Dimensions[^1];
        float[] values = output.ToArray();

        using var raw = Mat.FromPixelData(height, width, MatType.CV_32FC1, values);
        var depth = new Mat();
        Cv2.Resize(raw, depth, bgr.Size(), 0, 0, InterpolationFlags.Linear);
        // Soft depth edges give fewer tearing artifacts when pixels are shifted for each eye.
        Cv2.GaussianBlur(depth, depth, new Size(0, 0), 2.5);
        Normalize(depth);
        return depth;
    }

    /// <summary>RGB, scaled to 0..1, ImageNet mean/std normalized, planar (NCHW).</summary>
    static unsafe void FillTensor(Mat bgr, Span<float> buffer)
    {
        int size = bgr.Width, plane = size * size;
        for (int y = 0; y < size; y++)
        {
            byte* row = (byte*)bgr.Ptr(y);
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                buffer[i] = (row[x * 3 + 2] / 255f - Mean[0]) / Std[0];
                buffer[plane + i] = (row[x * 3 + 1] / 255f - Mean[1]) / Std[1];
                buffer[2 * plane + i] = (row[x * 3] / 255f - Mean[2]) / Std[2];
            }
        }
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
        _wake.Set();
        _worker.Join(2000);
        _session.Dispose();
        lock (_lock)
        {
            _pending?.Dispose();
            _depth?.Dispose();
        }
        _wake.Dispose();
    }
}
