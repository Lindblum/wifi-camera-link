# WiFi Camera Link

A Windows app for viewing and recording cheap Wi-Fi cameras on a PC. It joins the camera's own Wi-Fi hotspot, shows live video, and can turn the picture into 3D.

![Screenshot of the application](docs/screenshot.png)

## Features

- **Connect:** finds the camera's Wi-Fi and joins it. It can reconnect on its own and puts your normal network back afterwards.
- **Cameras:** i4Season (YPC) and VStarcam (O-KAM) cameras, plus any RTSP or HTTP stream URL. It can also detect the camera type.
- **Video:** rotate, full screen, stretch, and resampling to a chosen resolution. There is also camera light control where the camera supports it.
- **Capture:** snapshots and recordings.
- **Enhance:** denoise, sharpen, stabilize and frame interpolation.
- **3D:** depth comes from an AI model (Depth Anything V2/V3, MiDaS) or a fast heuristic. Output can be side by side, top and bottom, interlaced, anaglyph, or a depth map. You can adjust convergence, smoothing, edge cleanup and hole filling.

The AI depth models are downloaded the first time you pick one. They run on the GPU through DirectML, or on the CPU if no GPU is available.

## Tested with

- ANESOK cameras that use the YPC app (i4Season)
- A RIDFDC camera that uses the O-KAM app (VStarcam)

## Build

You need Windows and the .NET 8 SDK.

```
dotnet build -c Release
```
