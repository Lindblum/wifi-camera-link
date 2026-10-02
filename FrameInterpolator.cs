using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace WiFiCameraLink;

/// <summary>
/// Makes an in-between frame from two consecutive frames: Farneback optical flow measures where every
/// pixel moved, then both frames are warped halfway along that motion and blended. The motion is measured
/// at half resolution, which is plenty for a 640x480 camera and keeps it to a few milliseconds of CPU,
/// leaving the GPU to the 3D depth model.
/// </summary>
sealed class FrameInterpolator : IDisposable
{
    const double FlowScale = 0.25; // 160x120: ~4 ms, and as accurate as half size on this camera
    Mat? _previous, _previousGray; // previous frame, and its small grayscale copy for measuring motion
    Mat? _gridX, _gridY; // each pixel's own coordinates, the base for the warp maps

    public void Reset()
    {
        _previous?.Dispose();
        _previousGray?.Dispose();
        _previous = _previousGray = null;
    }

    /// <summary>Remembers this frame and returns the frame halfway between it and the previous one (null for the first).</summary>
    public Mat? Interpolate(Mat current)
    {
        using var fullGray = current.CvtColor(ColorConversionCodes.BGR2GRAY);
        var gray = fullGray.Resize(new Size(0, 0), FlowScale, FlowScale, InterpolationFlags.Area);
        if (_previous == null || _previousGray == null || _previous.Size() != current.Size())
        {
            Reset();
            _previous = current.Clone();
            _previousGray = gray;
            return null;
        }

        // For each pixel of the previous frame: how far it moved by the current one.
        using var smallFlow = new Mat();
        Cv2.CalcOpticalFlowFarneback(_previousGray, gray, smallFlow, 0.5, 3, 15, 3, 5, 1.2, OpticalFlowFlags.None);
        using var flow = new Mat();
        Cv2.Resize(smallFlow, flow, current.Size(), 0, 0, InterpolationFlags.Linear);
        flow.ConvertTo(flow, MatType.CV_32FC2, 1 / FlowScale); // back to full-resolution pixel distances
        EnsureGrid(current.Size());
        Cv2.Split(flow, out Mat[] parts);
        using var flowX = parts[0];
        using var flowY = parts[1];

        // Halfway in time, a pixel is half its motion along: look half a step back into the previous
        // frame and half a step forward into the current one, then average the two.
        using var backX = new Mat();
        using var backY = new Mat();
        using var forwardX = new Mat();
        using var forwardY = new Mat();
        Cv2.ScaleAdd(flowX, -0.5, _gridX!, backX);
        Cv2.ScaleAdd(flowY, -0.5, _gridY!, backY);
        Cv2.ScaleAdd(flowX, 0.5, _gridX!, forwardX);
        Cv2.ScaleAdd(flowY, 0.5, _gridY!, forwardY);

        using var fromPrevious = new Mat();
        using var fromCurrent = new Mat();
        Cv2.Remap(_previous, fromPrevious, backX, backY, InterpolationFlags.Linear, BorderTypes.Replicate);
        Cv2.Remap(current, fromCurrent, forwardX, forwardY, InterpolationFlags.Linear, BorderTypes.Replicate);
        var middle = new Mat();
        Cv2.AddWeighted(fromPrevious, 0.5, fromCurrent, 0.5, 0, middle);

        _previous.Dispose();
        _previous = current.Clone();
        _previousGray.Dispose();
        _previousGray = gray;
        return middle;
    }

    unsafe void EnsureGrid(Size size)
    {
        if (_gridX != null && _gridX.Size() == size)
            return;
        _gridX?.Dispose();
        _gridY?.Dispose();
        _gridX = new Mat(size, MatType.CV_32FC1);
        _gridY = new Mat(size, MatType.CV_32FC1);
        for (int y = 0; y < size.Height; y++)
        {
            float* gx = (float*)_gridX.Ptr(y);
            float* gy = (float*)_gridY.Ptr(y);
            for (int x = 0; x < size.Width; x++)
            {
                gx[x] = x;
                gy[x] = y;
            }
        }
    }

    public void Dispose()
    {
        Reset();
        _gridX?.Dispose();
        _gridY?.Dispose();
    }
}
