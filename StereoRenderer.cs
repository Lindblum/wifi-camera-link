using OpenCvSharp;
using OpenCvSharp.XImgProc;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>How the two eyes' views (or the depth) are arranged in the output.</summary>
public enum StereoLayout
{
    SideBySide, HalfSideBySide, TopBottom, HalfTopBottom, Interlaced, Anaglyph, DepthMap, VideoAndDepth,
}

/// <summary>What fills background that a shifted object uncovers, which the camera never saw.</summary>
public enum HoleFill { Stretch, Sharp, Soft, Mirror }

/// <summary>
/// Builds each eye's view by moving every pixel sideways in proportion to its depth (near things outward so
/// they appear in front of the screen, far things behind), then arranges the views for the chosen display.
/// Pixels are moved forward with a depth test, the way the 2D-3D add-on's renderer works, so near objects
/// cover far ones and the background they uncover is found and can be filled.
/// </summary>
sealed class StereoRenderer
{
    /// <summary>Sideways magnification beyond which a stretch counts as uncovered background, not surface.</summary>
    const float HoleSpan = 2f;

    public Mat Render(Mat frame, Mat? rawDepth, EnhanceOptions o)
    {
        using var depth = rawDepth == null ? null : Refine(frame, rawDepth, o);
        switch (o.Layout)
        {
            case StereoLayout.DepthMap:
                return DepthImage(frame.Size(), depth);
            case StereoLayout.VideoAndDepth:
            {
                using var image = DepthImage(frame.Size(), depth);
                var both = new Mat();
                Cv2.HConcat(o.SwapEyes ? [image, frame] : [frame, image], both);
                return both;
            }
        }

        using var first = new Mat();
        using var second = new Mat();
        if (depth == null)
        {
            frame.CopyTo(first); // depth not ready yet: show it flat rather than jump the layout later
            frame.CopyTo(second);
        }
        else
        {
            float maxDisparity = frame.Width * 0.006f * o.Depth3D; // 0.6%..9% of the width from far to near
            Warp(frame, depth, first, eye: 1, maxDisparity, o);
            Warp(frame, depth, second, eye: -1, maxDisparity, o);
        }
        var (left, right) = o.SwapEyes ? (second, first) : (first, second);

        var result = new Mat();
        switch (o.Layout)
        {
            case StereoLayout.HalfSideBySide:
            case StereoLayout.HalfTopBottom:
            {
                bool sideBySide = o.Layout == StereoLayout.HalfSideBySide;
                var half = sideBySide ? new Size(frame.Width / 2, frame.Height) : new Size(frame.Width, frame.Height / 2);
                Cv2.Resize(left, left, half, 0, 0, InterpolationFlags.Area);
                Cv2.Resize(right, right, half, 0, 0, InterpolationFlags.Area);
                if (sideBySide)
                    Cv2.HConcat([left, right], result);
                else
                    Cv2.VConcat([left, right], result);
                break;
            }
            case StereoLayout.TopBottom:
                Cv2.VConcat([left, right], result);
                break;
            case StereoLayout.Interlaced:
                // Passive (polarized) 3D TVs: even rows go to the left eye, odd rows to the right.
                left.CopyTo(result);
                for (int y = 1; y < result.Height; y += 2)
                {
                    using var row = result.Row(y);
                    using var source = right.Row(y);
                    source.CopyTo(row);
                }
                break;
            case StereoLayout.Anaglyph:
                Anaglyph(left, right, result);
                break;
            default:
                Cv2.HConcat([left, right], result);
                break;
        }
        return result;
    }

    /// <summary>
    /// Depth edges snapped to the picture's edges (a guided filter), then near depth grown outward so object
    /// rims, which mix object and background colors and tend to get background depth, move with the object.
    /// </summary>
    static Mat Refine(Mat frame, Mat depth, EnhanceOptions o)
    {
        var refined = depth.Clone();
        if (o.EdgeSnap > 0)
        {
            using var guide = new Mat();
            Cv2.CvtColor(frame, guide, ColorConversionCodes.BGR2GRAY);
            guide.ConvertTo(guide, MatType.CV_32FC1, 1 / 255.0);
            double tolerance = 0.2 + (0.01 - 0.2) * o.EdgeSnap / 10.0; // how different colors may be within one depth
            CvXImgProc.GuidedFilter(guide, depth, refined, Math.Max(2, frame.Width / 80), tolerance * tolerance);
        }
        if (o.EdgeDilation > 0)
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * o.EdgeDilation + 1, 2 * o.EdgeDilation + 1));
            Cv2.Dilate(refined, refined, kernel);
        }
        return refined;
    }

    static Mat DepthImage(Size size, Mat? depth)
    {
        if (depth == null)
            return new Mat(size, MatType.CV_8UC3, Scalar.All(128));
        var image = new Mat();
        depth.ConvertTo(image, MatType.CV_8UC1, 255);
        Cv2.CvtColor(image, image, ColorConversionCodes.GRAY2BGR);
        return image;
    }

    /// <summary>
    /// One eye's view. <paramref name="eye"/> is +1 for the left eye, -1 for the right. Each row is drawn by
    /// moving the span between neighboring source pixels to where their depths put it; where two spans land
    /// on the same pixel, the nearer wins. A span stretched past <see cref="HoleSpan"/> is background the
    /// camera never saw, left empty and then filled by <see cref="FillHoles"/>.
    /// </summary>
    static unsafe void Warp(Mat frame, Mat depth, Mat output, int eye, float maxDisparity, EnhanceOptions o)
    {
        output.Create(frame.Size(), MatType.CV_8UC3);
        int width = frame.Width, height = frame.Height;
        float shift = eye * 0.5f * maxDisparity, convergence = o.Convergence / 100f;
        float holeSpan = o.HoleFill == HoleFill.Stretch ? float.MaxValue : HoleSpan;
        // Background sampled for fills starts this far past the edge, so the object's own rim isn't picked up.
        float margin = Math.Max(1, width / 320f);
        var frameData = (IntPtr)frame.Data;
        long frameStep = frame.Step(), depthStep = depth.Step(), outputStep = output.Step();
        var depthData = (IntPtr)depth.Data;
        var outputData = (IntPtr)output.Data;

        Parallel.For(0, height, () => (new float[width], new float[width]), (y, _, buffers) =>
        {
            var (z, sourceX) = buffers;
            byte* src = (byte*)frameData + y * frameStep;
            float* d = (float*)((byte*)depthData + y * depthStep);
            byte* dst = (byte*)outputData + y * outputStep;
            Array.Fill(z, -1f); // empty

            float t0 = shift * (d[0] - convergence);
            for (int x = 0; x < width - 1; x++)
            {
                float t1 = x + 1 + shift * (d[x + 1] - convergence);
                float span = t1 - t0;
                // Spans that fold back (span < 0) are hidden behind their neighbors; wide ones are holes.
                if (span >= 0 && span <= holeSpan)
                {
                    int start = Math.Max(0, (int)MathF.Ceiling(t0));
                    int end = Math.Min(width - 1, span == 0 ? start : (int)MathF.Ceiling(t1) - 1);
                    for (int tx = start; tx <= end; tx++)
                    {
                        float f = span > 0 ? Math.Clamp((tx - t0) / span, 0, 1) : 0;
                        float zv = d[x] + f * (d[x + 1] - d[x]);
                        if (zv <= z[tx])
                            continue;
                        z[tx] = zv;
                        sourceX[tx] = x + f;
                        byte* a = src + x * 3;
                        dst[tx * 3] = (byte)(a[0] + f * (a[3] - a[0]) + 0.5f);
                        dst[tx * 3 + 1] = (byte)(a[1] + f * (a[4] - a[1]) + 0.5f);
                        dst[tx * 3 + 2] = (byte)(a[2] + f * (a[5] - a[2]) + 0.5f);
                    }
                }
                t0 = t1;
            }
            FillHoles(frame, y, dst, z, sourceX, o.HoleFill, margin);
            return buffers;
        }, _ => { });
    }

    /// <summary>
    /// Fills each run of empty pixels in a row. Stretch blends the two sides (or repeats the one side at the
    /// frame's edge). The others take the background, the farther side, from the source picture: Sharp repeats
    /// one background color, Soft averages a few nearby samples (less streaky), Mirror reflects the background
    /// across the hole's edge so its texture continues into it.
    /// </summary>
    static unsafe void FillHoles(Mat frame, int y, byte* dst, float[] z, float[] sourceX, HoleFill mode, float margin)
    {
        int width = frame.Width;
        byte* sample = stackalloc byte[3];
        for (int a = 0; a < width; a++)
        {
            if (z[a] >= 0)
                continue;
            int b = a;
            while (b < width && z[b] < 0)
                b++;
            // The hole is [a, b). Its neighbors a - 1 and b are drawn pixels, unless it reaches the frame's edge.
            bool hasLeft = a > 0, hasRight = b < width;
            if (!hasLeft && !hasRight)
            {
                Buffer.MemoryCopy((byte*)frame.Ptr(y), dst, width * 3, width * 3);
                return;
            }
            bool backgroundLeft = hasLeft && (!hasRight || z[a - 1] < z[b]);
            int edge = backgroundLeft ? a - 1 : b;
            int away = backgroundLeft ? -1 : 1; // from the hole into the background

            for (int x = a; x < b; x++)
            {
                byte* p = dst + x * 3;
                if (mode == HoleFill.Stretch || !(hasLeft && hasRight))
                {
                    if (hasLeft && hasRight)
                    {
                        float f = (float)(x - a + 1) / (b - a + 1);
                        byte* l = dst + (a - 1) * 3, r = dst + b * 3;
                        for (int c = 0; c < 3; c++)
                            p[c] = (byte)(l[c] + f * (r[c] - l[c]) + 0.5f);
                    }
                    else
                    {
                        byte* s = dst + edge * 3;
                        p[0] = s[0];
                        p[1] = s[1];
                        p[2] = s[2];
                    }
                    continue;
                }

                float from = sourceX[edge] + away * margin;
                switch (mode)
                {
                    case HoleFill.Sharp:
                        Sample(frame, from, y, p);
                        break;
                    case HoleFill.Mirror:
                        // The hole pixel k from the edge takes the background k - 1 past it, on the far side.
                        Sample(frame, from + away * (Math.Abs(x - edge) - 1), y, p);
                        break;
                    default: // Soft
                    {
                        int b0 = 0, g0 = 0, r0 = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            Sample(frame, from + away * i * 3 * margin, Math.Clamp(y + (i * 2 - 3), 0, frame.Height - 1), sample);
                            b0 += sample[0];
                            g0 += sample[1];
                            r0 += sample[2];
                        }
                        p[0] = (byte)(b0 / 4);
                        p[1] = (byte)(g0 / 4);
                        p[2] = (byte)(r0 / 4);
                        break;
                    }
                }
            }
            a = b;
        }
    }

    static unsafe void Sample(Mat frame, float x, int y, byte* pixel)
    {
        int ix = Math.Clamp((int)MathF.Round(x), 0, frame.Width - 1);
        byte* s = (byte*)frame.Ptr(y) + ix * 3;
        pixel[0] = s[0];
        pixel[1] = s[1];
        pixel[2] = s[2];
    }

    /// <summary>
    /// Red/cyan glasses, with Dubois' least-squares matrices: less ghosting and truer color than putting one
    /// eye in the red channel and the other in green and blue.
    /// </summary>
    static unsafe void Anaglyph(Mat left, Mat right, Mat result)
    {
        result.Create(left.Size(), MatType.CV_8UC3);
        int width = left.Width;
        for (int y = 0; y < left.Height; y++)
        {
            byte* l = (byte*)left.Ptr(y), r = (byte*)right.Ptr(y), o = (byte*)result.Ptr(y);
            for (int x = 0; x < width * 3; x += 3)
            {
                float lr = l[x + 2], lg = l[x + 1], lb = l[x], rr = r[x + 2], rg = r[x + 1], rb = r[x];
                float red = 0.456f * lr + 0.500f * lg + 0.176f * lb - 0.043f * rr - 0.088f * rg - 0.002f * rb;
                float green = -0.040f * lr - 0.038f * lg - 0.016f * lb + 0.378f * rr + 0.734f * rg - 0.018f * rb;
                float blue = -0.015f * lr - 0.021f * lg - 0.005f * lb - 0.072f * rr - 0.113f * rg + 1.226f * rb;
                o[x + 2] = (byte)Math.Clamp(red + 0.5f, 0, 255);
                o[x + 1] = (byte)Math.Clamp(green + 0.5f, 0, 255);
                o[x] = (byte)Math.Clamp(blue + 0.5f, 0, 255);
            }
        }
    }
}
