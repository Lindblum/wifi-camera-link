using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>Where 3D depth comes from: an AI model on the GPU, or a fast heuristic on the CPU.</summary>
public enum DepthSource { DepthAnythingV2, DepthAnythingV3, MiDaS, SceneCues, GroundPlane, MotionParallax }

/// <summary>Estimates relative depth from one frame. Called from the depth thread only.</summary>
interface IDepthModel : IDisposable
{
    /// <summary>Where it runs, for the log: "GPU (DirectML)", "CPU".</summary>
    string Device { get; }

    /// <summary>
    /// Relative depth (CV_32FC1, bigger = nearer, any range) at whatever resolution the model works at.
    /// AI models with a flexible input size work at about <paramref name="detail"/>² pixels.
    /// </summary>
    Mat Estimate(Mat bgr, int detail);
}

static class DepthModels
{
    public static string Name(DepthSource source) => source switch
    {
        DepthSource.DepthAnythingV2 => "Depth Anything V2",
        DepthSource.DepthAnythingV3 => "Depth Anything V3",
        DepthSource.MiDaS => "MiDaS",
        DepthSource.SceneCues => "Scene cues",
        DepthSource.GroundPlane => "Ground plane",
        _ => "Motion parallax",
    };

    public static bool IsHeuristic(DepthSource source) => source >= DepthSource.SceneCues;

    public static async Task<IDepthModel> CreateAsync(DepthSource source, Action<string> log, CancellationToken ct) =>
        IsHeuristic(source) ? new HeuristicDepth(source) : await OnnxDepthModel.CreateAsync(source, log, ct);
}

/// <summary>
/// A depth model run through ONNX Runtime, on the GPU via DirectML when it can. All three are small enough to
/// keep up with a camera: Depth Anything V2 Small (Apache-2.0), Depth Anything V3 Small (Apache-2.0; smoother
/// backgrounds, ~1.5x slower) and MiDaS v2.1 Small (MIT; about twice as fast, blurrier).
/// </summary>
sealed class OnnxDepthModel : IDepthModel
{
    /// <param name="Files">Download URL and local path of each file; the first is the model.</param>
    /// <param name="OutputIsDistance">The model outputs distance (far = big) rather than inverse depth.</param>
    sealed record Spec(string Label, int Megabytes, (string Url, string Path)[] Files, bool OutputIsDistance = false);

    static readonly string ModelDir = Path.Combine(AppPaths.AppDir, "models");

    static Spec SpecFor(DepthSource source) => source switch
    {
        DepthSource.DepthAnythingV2 => new("Depth Anything V2", 99,
            [("https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx",
                Path.Combine(ModelDir, "depth_anything_v2_small.onnx"))]),
        // V3 keeps its weights in a second file, which the model finds by the name model.onnx_data.
        DepthSource.DepthAnythingV3 => new("Depth Anything V3", 105,
            [("https://huggingface.co/onnx-community/depth-anything-v3-small/resolve/main/onnx/model.onnx",
                Path.Combine(ModelDir, "depth_anything_v3_small", "model.onnx")),
             ("https://huggingface.co/onnx-community/depth-anything-v3-small/resolve/main/onnx/model.onnx_data",
                Path.Combine(ModelDir, "depth_anything_v3_small", "model.onnx_data"))],
            OutputIsDistance: true),
        DepthSource.MiDaS => new("MiDaS", 66,
            [("https://huggingface.co/InDepthDesigns/midas_v21_small_256/resolve/main/midas_v21_small_256.onnx",
                Path.Combine(ModelDir, "midas_v2.1_small_256.onnx"))]),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    readonly InferenceSession _session;
    readonly string _inputName, _outputName;
    readonly int[] _inputDims; // -1 where the size is flexible
    readonly bool _outputIsDistance;

    OnnxDepthModel(InferenceSession session, string device, bool outputIsDistance)
    {
        _session = session;
        Device = device;
        _outputIsDistance = outputIsDistance;
        _inputName = session.InputMetadata.Keys.First();
        _outputName = session.OutputMetadata.Keys.First(); // V3 also outputs confidence and camera pose
        _inputDims = session.InputMetadata[_inputName].Dimensions;
    }

    public string Device { get; }

    public static async Task<IDepthModel> CreateAsync(DepthSource source, Action<string> log, CancellationToken ct)
    {
        var spec = SpecFor(source);
        if (spec.Files.Any(f => !File.Exists(f.Path)))
        {
            log($"Downloading the {spec.Label} depth model ({spec.Megabytes} MB, first time only)...");
            foreach (var (url, path) in spec.Files)
                if (!File.Exists(path))
                    await DownloadAsync(url, path, log, ct);
            log("Depth model downloaded.");
        }
        string modelPath = spec.Files[0].Path;

        return await Task.Run<IDepthModel>(() =>
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
                return new OnnxDepthModel(new InferenceSession(modelPath, options), "GPU (DirectML)", spec.OutputIsDistance);
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
                return new OnnxDepthModel(new InferenceSession(modelPath, options), "CPU", spec.OutputIsDistance);
            }
        }, ct);
    }

    static async Task DownloadAsync(string url, string path, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".part";

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        if (total < 10_000_000)
        {
            // Small companion files (V3's model.onnx is just the graph) aren't worth progress lines.
            await File.WriteAllBytesAsync(partial, await response.Content.ReadAsByteArrayAsync(ct), ct);
        }
        else
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[1 << 16];
            long done = 0;
            int lastReported = 0, read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                int percent = (int)(done * 100 / total);
                if (percent >= lastReported + 25)
                {
                    lastReported = percent / 25 * 25;
                    log($"  ...{lastReported}%");
                }
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    public Mat Estimate(Mat bgr, int detail)
    {
        var size = InputSize(bgr.Size(), detail);
        int[] shape = _inputDims.Length == 5 ? [1, 1, 3, size.Height, size.Width] : [1, 3, size.Height, size.Width];
        var tensor = new DenseTensor<float>(shape);
        using (var resized = bgr.Resize(size, 0, 0, InterpolationFlags.Area))
            FillTensor(resized, tensor.Buffer.Span);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)], [_outputName]);
        var output = results[0].AsTensor<float>();
        int height = output.Dimensions[^2], width = output.Dimensions[^1];
        var depth = Mat.FromPixelData(height, width, MatType.CV_32FC1, output.ToArray()).Clone();
        if (_outputIsDistance)
        {
            Cv2.Max(depth, 1e-6, depth);
            Cv2.Divide(1.0, depth, depth); // the rest of the pipeline expects near = big
        }
        return depth;
    }

    /// <summary>
    /// The model's fixed input size (MiDaS: 256x256), or for flexible models the frame's proportions with about
    /// <paramref name="detail"/>² pixels (the cost of a detail x detail square), both sides multiples of the
    /// 14-pixel patches they work in.
    /// </summary>
    Size InputSize(Size frame, int detail)
    {
        int fixedH = _inputDims[^2], fixedW = _inputDims[^1];
        if (fixedH > 0 && fixedW > 0)
            return new Size(fixedW, fixedH);
        static int Patches(double pixels) => Math.Max(14, (int)Math.Round(pixels / 14) * 14);
        double scale = detail / Math.Sqrt((double)frame.Width * frame.Height);
        return new Size(Patches(frame.Width * scale), Patches(frame.Height * scale));
    }

    /// <summary>RGB, scaled to 0..1, ImageNet mean/std normalized, planar (NCHW).</summary>
    static unsafe void FillTensor(Mat bgr, Span<float> buffer)
    {
        int width = bgr.Width, height = bgr.Height, plane = width * height;
        for (int y = 0; y < height; y++)
        {
            byte* row = (byte*)bgr.Ptr(y);
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                buffer[i] = (row[x * 3 + 2] / 255f - Mean[0]) / Std[0];
                buffer[plane + i] = (row[x * 3 + 1] / 255f - Mean[1]) / Std[1];
                buffer[2 * plane + i] = (row[x * 3] / 255f - Mean[2]) / Std[2];
            }
        }
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Depth guessed from simple picture cues, in a millisecond or two on the CPU. Far less accurate than the AI
/// models, but instant, steady and needs no download or GPU.
/// </summary>
sealed class HeuristicDepth(DepthSource source) : IDepthModel
{
    const int Width = 192; // working width; depth is smooth, so this is plenty
    Mat? _previousGray;

    public string Device => "CPU";

    public Mat Estimate(Mat bgr, int detail)
    {
        var size = new Size(Width, Math.Max(2, (int)Math.Round((double)Width * bgr.Height / bgr.Width)));
        using var small = bgr.Resize(size, 0, 0, InterpolationFlags.Area);
        return source switch
        {
            DepthSource.SceneCues => SceneCues(small),
            DepthSource.MotionParallax => MotionParallax(small),
            _ => GroundPlane(size),
        };
    }

    /// <summary>Lower in the picture = nearer, as for a camera looking out over the ground.</summary>
    static unsafe Mat GroundPlane(Size size)
    {
        var depth = new Mat(size, MatType.CV_32FC1);
        for (int y = 0; y < size.Height; y++)
        {
            float v = (float)y / (size.Height - 1);
            float* row = (float*)depth.Ptr(y);
            for (int x = 0; x < size.Width; x++)
                row[x] = v;
        }
        return depth;
    }

    /// <summary>
    /// The 2D-3D add-on's fallback: mostly ground plane, plus bright, saturated and central areas brought
    /// forward, since subjects tend to be all three.
    /// </summary>
    static unsafe Mat SceneCues(Mat small)
    {
        using var blurred = new Mat();
        Cv2.Blur(small, blurred, new Size(5, 5));
        var depth = new Mat(small.Size(), MatType.CV_32FC1);
        int width = small.Width, height = small.Height;
        for (int y = 0; y < height; y++)
        {
            byte* c = (byte*)blurred.Ptr(y);
            float* d = (float*)depth.Ptr(y);
            float vertical = (float)y / (height - 1);
            for (int x = 0; x < width; x++)
            {
                float b = c[x * 3] / 255f, g = c[x * 3 + 1] / 255f, r = c[x * 3 + 2] / 255f;
                float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                float saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                float dx = (float)x / (width - 1) - 0.5f, dy = (vertical - 0.5f) * 1.3f;
                float t = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / 0.75f, 0, 1);
                float center = 1 - t * t * (3 - 2 * t); // 1 - smoothstep(0, 0.75, distance)
                d[x] = Math.Clamp(0.5f * vertical + 0.2f * luma + 0.15f * saturation + 0.25f * center - 0.05f, 0, 1);
            }
        }
        return depth;
    }

    /// <summary>
    /// When the camera moves sideways, near things slide across the picture faster than far ones; and with a
    /// still camera, whatever moves is usually the subject. So depth is how far each spot moved since the last
    /// frame, beyond the picture's overall pan, over a ground-plane base so a still scene isn't flat.
    /// </summary>
    unsafe Mat MotionParallax(Mat small)
    {
        var gray = small.CvtColor(ColorConversionCodes.BGR2GRAY);
        var depth = GroundPlane(small.Size());
        if (_previousGray == null || _previousGray.Size() != gray.Size())
        {
            _previousGray?.Dispose();
            _previousGray = gray;
            return depth;
        }

        using var flow = new Mat();
        Cv2.CalcOpticalFlowFarneback(_previousGray, gray, flow, 0.5, 3, 15, 3, 5, 1.2, OpticalFlowFlags.None);
        _previousGray.Dispose();
        _previousGray = gray;

        var channels = flow.Split();
        using var fx = channels[0];
        using var fy = channels[1];
        Cv2.Subtract(fx, new Scalar(Median(fx)), fx);
        Cv2.Subtract(fy, new Scalar(Median(fy)), fy);
        using var motion = new Mat();
        Cv2.Magnitude(fx, fy, motion);
        Cv2.GaussianBlur(motion, motion, new Size(0, 0), 3);
        // Scale by the strong motion in this frame, but never below half a pixel so sensor noise in a still
        // scene doesn't get blown up into depth.
        float scale = 1 / Math.Max(Percentile(motion, 0.95f), 0.5f);
        for (int y = 0; y < depth.Height; y++)
        {
            float* d = (float*)depth.Ptr(y);
            float* m = (float*)motion.Ptr(y);
            for (int x = 0; x < depth.Width; x++)
                d[x] = 0.35f * d[x] + 0.65f * Math.Min(m[x] * scale, 1);
        }
        return depth;
    }

    static float Median(Mat values) => Percentile(values, 0.5f);

    static float Percentile(Mat values, float q)
    {
        using var sample = values.Resize(new Size(48, 36), 0, 0, InterpolationFlags.Nearest);
        sample.GetArray(out float[] v);
        Array.Sort(v);
        return v[(int)(q * (v.Length - 1))];
    }

    public void Dispose() => _previousGray?.Dispose();
}
