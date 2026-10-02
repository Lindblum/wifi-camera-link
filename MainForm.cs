using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Drawing.Imaging;
using System.Net;
using System.Text;

namespace WiFiCameraLink;

public sealed class MainForm : Form
{
    // Wi-Fi names that cheap cameras commonly broadcast. This only filters the list; auto-connect joins
    // just the cameras that have streamed before, never a network merely because of its name.
    static readonly Regex CameraNamePattern = new(
        @"anesok|cam|ipc|ypc|scope|soulear|depstech|dvr|fpv|wifi[-_ ]?view|^x5|^a9|^hd[-_ ]", RegexOptions.IgnoreCase);

    static readonly string SnapshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppPaths.Name);
    static readonly string RecordingDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppPaths.Name);
    const int MaxIntensity = 15;

    readonly WlanClient _wlan = new();

    readonly ComboBox _adapterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, DropDownWidth = 340 };
    readonly ComboBox _networkBox = new() { Width = 200, DropDownWidth = 340 };
    readonly CheckBox _showAll = new() { Text = "Show all", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    readonly Button _scanButton = new() { Text = "Scan", AutoSize = true };
    readonly TextBox _passwordBox = new() { Width = 140, UseSystemPasswordChar = true, PlaceholderText = "blank if open" };
    readonly Button _connectButton = new() { Text = "Connect", AutoSize = true };
    readonly Button _disconnectButton = new() { Text = "Disconnect", AutoSize = true };
    readonly CheckBox _autoConnect = new() { Text = "Auto-connect", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
    readonly ComboBox _cameraTypeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly TextBox _streamUrlBox = new() { Width = 250, PlaceholderText = "rtsp://... or http://..." };
    readonly Button _startButton = new() { Text = "Start video", AutoSize = true };
    readonly Button _stopButton = new() { Text = "Stop video", AutoSize = true };
    readonly Button _snapshotButton = new() { Text = "Snapshot", AutoSize = true };
    readonly Button _probeButton = new() { Text = "Probe camera", AutoSize = true };
    readonly CheckBox _fullScan = new() { Text = "Deep scan", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    readonly Button _folderButton = new() { Text = "Snapshots", AutoSize = true };
    readonly Button _recordButton = new() { Text = "● Record", AutoSize = true };
    readonly Button _recordingsButton = new() { Text = "Recordings", AutoSize = true };
    readonly ToolStripStatusLabel _recordLabel = new() { ForeColor = Color.Red };
    readonly Button _rotateButton = new() { Text = "Rotate", AutoSize = true };
    readonly Button _fullScreenButton = new() { Text = "Full screen (F11)", AutoSize = true };
    readonly CheckBox _denoiseBox = Check("Reduce grain");
    readonly TrackBar _denoiseSlider = Slider();
    readonly CheckBox _sharpenBox = Check("Sharpen");
    readonly TrackBar _sharpenSlider = Slider();
    readonly CheckBox _stabilizeBox = Check("Stabilize");
    readonly CheckBox _interpolateBox = Check("Double frame rate");
    readonly CheckBox _stretchBox = Check("Stretch to fill");
    readonly Label _perfCamera = Metric("Camera: -");
    readonly Label _perfShown = Metric("Shown: -");
    readonly Label _perfLatency = Metric("Latency: -", 175);
    readonly Label _perfDepth = Metric("Depth: -");
    readonly Label _perfDropped = Metric("Skipped: -");
    readonly CheckBox _sbsBox = Check("Convert to 3D");
    readonly TrackBar _depthSlider = Slider(MaxIntensity);
    readonly CheckBox _halfSbsBox = Check("Half width");
    readonly CheckBox _swapEyesBox = Check("Swap eyes");
    readonly VideoPipeline _pipeline = new();
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    readonly StatusStrip _statusStrip = new();
    readonly List<Control> _rows = []; // everything above the video; hidden in full screen
    readonly PictureBox _picture = new() { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
    readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
    };
    readonly ToolStripStatusLabel _status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _batteryLabel = new();
    readonly System.Windows.Forms.Timer _fpsTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 5000 };
    readonly Settings _settings = Settings.Load();

    readonly List<string> _pendingLog = [];
    List<WifiNetwork> _networks = [];
    CancellationTokenSource? _probeCts;
    CancellationTokenSource? _videoCts;
    CameraFrame? _lastFrame;
    int _framesThisSecond;
    int _shownThisSecond;
    int _drawing; // 1 while a frame is being handed to the UI; newer frames are dropped meanwhile
    volatile int _rotation; // degrees clockwise: 0, 90, 180 or 270
    bool _fullScreen;
    FormWindowState _windowStateBeforeFullScreen;
    Rectangle _boundsBeforeFullScreen;
    long _lastFrameTick;
    DepthEstimator? _depthEstimator;
    bool _depthLoading;
    bool _autoBusy;
    bool _autoPaused;       // set by Disconnect / Stop video, cleared by Connect / Start video
    bool _autoWaitLogged;   // so "waiting for the camera" is logged once, not every tick

    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    public MainForm()
    {
        Text = "WiFi Camera Link";
        ClientSize = new Size(1600, 1000);
        MinimumSize = new Size(800, 500);

        Directory.CreateDirectory(AppPaths.AppDir);
        BuildLayout();
        var tips = new ToolTip();
        tips.SetToolTip(_fullScan, "Probe every TCP and UDP port (takes a few minutes)");
        tips.SetToolTip(_showAll, "List every Wi-Fi network, not just ones that look like cameras");
        tips.SetToolTip(_stretchBox, "Reshape the picture to fill the video area (also applies to snapshots and recordings)");
        tips.SetToolTip(_interpolateBox, "Insert a motion-estimated frame between camera frames: 20 fps becomes 40, about 50 ms later");
        tips.SetToolTip(_perfLatency, "Time from a camera frame arriving to the finished frame reaching the display");
        tips.SetToolTip(_perfDropped, "Camera frames skipped because processing was still busy with the previous one");
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11 || (e.KeyCode == Keys.Escape && _fullScreen))
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
        _picture.DoubleClick += (_, _) => ToggleFullScreen();
        _picture.Resize += (_, _) => UpdateDisplayAspect();
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();
        _rotateButton.Click += (_, _) => Rotate();

        _scanButton.Click += async (_, _) => await ScanAsync();
        _showAll.CheckedChanged += (_, _) => ShowNetworks();
        _connectButton.Click += async (_, _) =>
        {
            ResumeAuto();
            await ConnectAsync(interactive: true);
        };
        _disconnectButton.Click += (_, _) =>
        {
            PauseAuto();
            DisconnectFromCamera();
        };
        _probeButton.Click += async (_, _) => await ProbeAsync();
        _startButton.Click += async (_, _) =>
        {
            ResumeAuto();
            await StartVideoAsync();
        };
        _stopButton.Click += (_, _) =>
        {
            PauseAuto();
            StopVideo();
        };
        _autoConnect.Checked = _settings.AutoConnect;
        _cameraTypeBox.Items.AddRange(["Auto-detect", "i4Season (YPC)", "Stream URL"]);
        _cameraTypeBox.SelectedIndex = (int)_settings.CameraType;
        _streamUrlBox.Text = _settings.StreamUrl ?? "";
        _streamUrlBox.Enabled = _settings.CameraType == CameraType.StreamUrl;
        _cameraTypeBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.CameraType = SelectedCameraType;
            _streamUrlBox.Enabled = SelectedCameraType == CameraType.StreamUrl;
            _settings.Save();
        };
        _streamUrlBox.TextChanged += (_, _) =>
        {
            _settings.StreamUrl = _streamUrlBox.Text.Trim();
            _settings.Save();
        };
        _autoConnect.CheckedChanged += async (_, _) =>
        {
            _settings.AutoConnect = _autoConnect.Checked;
            _settings.Save();
            Log(_autoConnect.Checked
                ? "Auto-connect on: the app will join the camera and keep the video running, reconnecting if it drops."
                : "Auto-connect off.");
            if (_autoConnect.Checked)
            {
                ResumeAuto();
                await AutoTickAsync();
            }
        };
        _autoTimer.Tick += async (_, _) => await AutoTickAsync();
        _snapshotButton.Click += (_, _) => SaveSnapshot();
        _folderButton.Click += (_, _) => OpenFolder(SnapshotDir);
        _recordingsButton.Click += (_, _) => OpenFolder(RecordingDir);
        _recordButton.Click += (_, _) => ToggleRecording();
        _fpsTimer.Tick += (_, _) =>
        {
            int fps = Interlocked.Exchange(ref _framesThisSecond, 0);
            _recordLabel.Text = _pipeline.IsRecording ? $"● REC {_pipeline.RecordingElapsed:mm\\:ss}" : "";
            if (!_pipeline.IsRecording && _recordButton.Text != "● Record")
                _recordButton.Text = "● Record"; // recording ended on its own (e.g. an encoder error)
            int shown = Interlocked.Exchange(ref _shownThisSecond, 0);
            int depthRate = _depthEstimator?.TakeCompletedCount() ?? 0;
            var stats = _pipeline.TakeStats();
            bool live = _videoCts != null;
            _perfCamera.Text = live ? $"Camera: {fps} fps" : "Camera: -";
            _perfShown.Text = live ? $"Shown: {shown} fps" : "Shown: -";
            _perfLatency.Text = live && stats.Output > 0
                ? $"Latency: {stats.AverageLatencyMs:0} ms (max {stats.MaxLatencyMs:0})" : "Latency: -";
            _perfDepth.Text = live && _sbsBox.Checked && _depthEstimator != null ? $"Depth: {depthRate}/s" : "Depth: -";
            _perfDropped.Text = live ? $"Skipped: {stats.Dropped}" : "Skipped: -";
            _perfDropped.ForeColor = stats.Dropped > 0 ? Color.DarkOrange : SystemColors.ControlText;
        };
        _pipeline.FrameReady += ShowProcessedFrame;
        _pipeline.Log += Log;
        LoadEnhanceSettings();
        foreach (var box in new[] { _denoiseBox, _sharpenBox, _stabilizeBox, _interpolateBox, _stretchBox, _halfSbsBox, _swapEyesBox })
            box.CheckedChanged += (_, _) => ApplyEnhanceOptions();
        foreach (var slider in new[] { _denoiseSlider, _sharpenSlider, _depthSlider })
            slider.ValueChanged += (_, _) => ApplyEnhanceOptions();
        _sbsBox.CheckedChanged += async (_, _) =>
        {
            ApplyEnhanceOptions();
            if (_sbsBox.Checked)
                await EnsureDepthModelAsync();
        };
        _fpsTimer.Start();
        Load += async (_, _) => await InitializeAsync();
    }

    WifiInterface? SelectedAdapter => _adapterBox.SelectedItem as WifiInterface;

    string SelectedSsid =>
        _networkBox.SelectedItem is WifiNetwork n && _networkBox.Text == n.ToString() ? n.Ssid : _networkBox.Text.Trim();

    void BuildLayout()
    {
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 4) };
        toolbar.Controls.AddRange(
        [
            Section("Camera Wi-Fi", Label("Adapter"), _adapterBox, Label("Network"), _networkBox, _scanButton, _showAll),
            Section("Connection", Label("Password"), _passwordBox, _connectButton, _disconnectButton, _autoConnect),
            Section("Camera", Label("Type"), _cameraTypeBox, Label("URL"), _streamUrlBox),
            Section("Diagnostics", _probeButton, _fullScan),
            Section("Video", _startButton, _stopButton, _rotateButton, _fullScreenButton, _stretchBox),
            Section("Capture", _snapshotButton, _recordButton, Label("Open:"), _folderButton, _recordingsButton),
            Section("Enhance", _denoiseBox, _denoiseSlider, _sharpenBox, _sharpenSlider, _stabilizeBox, _interpolateBox),
            Section("3D (side-by-side)", _sbsBox, Label("Intensity"), _depthSlider, _halfSbsBox, _swapEyesBox),
            Section("Performance", _perfCamera, _perfShown, _perfLatency, _perfDepth, _perfDropped),
        ]);
        _rows.Add(toolbar);
        // Sections hold different controls (buttons, sliders, labels), so their natural heights differ;
        // give them all the tallest one's height so the rows line up.
        Load += (_, _) =>
        {
            var sections = toolbar.Controls.OfType<GroupBox>().ToList();
            int height = sections.Max(b => b.PreferredSize.Height);
            foreach (var box in sections)
                box.MinimumSize = new Size(0, height);
        };

        _split.Panel1.Controls.Add(_picture);
        _split.Panel2.Controls.Add(_logBox);

        _statusStrip.Items.Add(_status);
        _statusStrip.Items.Add(_recordLabel);
        _statusStrip.Items.Add(_batteryLabel);

        Controls.Add(_split);
        Controls.Add(toolbar);
        Controls.Add(_statusStrip);
        Shown += (_, _) => _split.SplitterDistance = (int)(_split.Height * 0.68);
    }

    /// <summary>An outlined, titled group of controls laid out in one line.</summary>
    static GroupBox Section(string title, params Control[] controls)
    {
        var line = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty,
        };
        line.Controls.AddRange(controls);
        var box = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 2, 4, 2), Margin = new Padding(3, 2, 3, 2),
        };
        box.Controls.Add(line);
        // Sit just below the title, wherever the box says that is at the current display scaling.
        box.Layout += (_, _) => line.Location = box.DisplayRectangle.Location;
        return box;
    }

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 6, 0, 3) };

    // Grows to fit its text, but never narrower than a typical value, so the row doesn't jitter every second.
    static Label Metric(string text, int minimumWidth = 95) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(minimumWidth, 0), Margin = new Padding(6, 6, 0, 0),
    };

    static TrackBar Slider(int maximum = 10) => new()
    {
        Minimum = 1, Maximum = maximum, Value = 5, Width = 80, AutoSize = false, Height = 26,
        TickStyle = TickStyle.None, Margin = new Padding(0, 4, 6, 0),
    };

    static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 8, 0, 0) };

    async Task InitializeAsync()
    {
        Log("WiFi Camera Link started. Keep Ethernet plugged in so this PC stays online while the Wi-Fi adapter joins the camera.");
        try
        {
            var adapters = _wlan.GetInterfaces();
            _adapterBox.Items.AddRange(adapters.ToArray<object>());
            if (adapters.Count == 0)
            {
                Log("No Wi-Fi adapter found.");
                return;
            }
            _adapterBox.SelectedIndex = 0;

            bool onCameraNow = IsCameraNetwork(_wlan.GetCurrentProfile(adapters[0].Id));
            if (NetworkRestore.IsPending && !(_autoConnect.Checked && onCameraNow))
            {
                Log("The last session ended while still on the camera network; restoring your Wi-Fi.");
                if (NetworkRestore.Restore(_wlan) is { } restored)
                    Log(restored);
            }

            var current = _wlan.GetCurrentProfile(adapters[0].Id);
            Log($"Wi-Fi adapter: {adapters[0].Description}. Currently on: {current ?? "nothing"}");
            ReportOtherAdapters(adapters[0].Id);
        }
        catch (Exception ex)
        {
            Log($"Wi-Fi error: {ex.Message}");
            return;
        }
        await ScanAsync();
        _autoTimer.Start();
        if (_sbsBox.Checked)
            _ = EnsureDepthModelAsync();
        if (_autoConnect.Checked)
            await AutoTickAsync();
    }

    void ReportOtherAdapters(Guid wifiId)
    {
        var others = NetUtil.OtherOnlineAdapters(wifiId);
        if (others.Count == 0)
            Log("WARNING: No other adapter is online. Joining the camera will take this PC offline until you disconnect.");
        foreach (var (nic, info) in others)
            Log($"Other connection: {nic.Name} ({info.Address}/{info.PrefixLength})");
    }

    async Task ScanAsync()
    {
        if (SelectedAdapter is not { } adapter)
            return;

        _scanButton.Enabled = false;
        SetStatus("Scanning for networks...");
        try
        {
            try
            {
                _wlan.Scan(adapter.Id);
                await Task.Delay(4000); // scan results take a few seconds to arrive
            }
            catch (Exception ex)
            {
                Log($"Scan request failed ({ex.Message}); showing cached results.");
            }
            _networks = _wlan.GetNetworks(adapter.Id);
            ShowNetworks(log: true);
        }
        catch (Exception ex)
        {
            Log($"Could not list networks: {ex.Message}");
        }
        finally
        {
            _scanButton.Enabled = true;
            SetStatus("Ready");
        }
    }

    void ShowNetworks(bool log = true)
    {
        string typed = SelectedSsid;
        if (typed.Length == 0 && _settings.LastSsid is { } last && _networks.Any(n => n.Ssid == last))
            typed = last;
        var shown = _showAll.Checked
            ? _networks
            : _networks.Where(n => IsCameraNetwork(n.Ssid)).ToList();

        _networkBox.Items.Clear();
        _networkBox.Items.AddRange(shown.ToArray<object>());

        int match = shown.FindIndex(n => n.Ssid == typed);
        if (match >= 0)
            _networkBox.SelectedIndex = match;
        else if (shown.Count > 0 && typed.Length == 0)
            _networkBox.SelectedIndex = 0;
        else
            _networkBox.Text = typed;

        if (!log)
            return;
        Log(shown.Count > 0
            ? $"Found {shown.Count} {(_showAll.Checked ? "" : "camera ")}network(s): {string.Join(", ", shown.Select(n => $"{n.Ssid} ({n.Signal}%)"))}"
            : $"No camera networks found ({_networks.Count} other network(s) visible). Is the camera on? Tick Show all, or type the name.");
    }

    async Task ConnectAsync(bool interactive)
    {
        if (SelectedAdapter is not { } adapter)
            return;
        string ssid = SelectedSsid;
        if (ssid.Length == 0)
        {
            Log("Choose or type the camera's network name first.");
            return;
        }

        if (NetUtil.OtherOnlineAdapters(adapter.Id).Count == 0 && !interactive)
            Log("Note: no Ethernet connection, so this PC is offline while it's on the camera.");
        else if (NetUtil.OtherOnlineAdapters(adapter.Id).Count == 0 &&
            MessageBox.Show(this,
                "No other network connection (like Ethernet) is active, so joining the camera will take this PC offline until you disconnect.\n\nContinue?",
                "WiFi Camera Link", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _connectButton.Enabled = false;
        bool connected = false;
        try
        {
            string? current = _wlan.GetCurrentProfile(adapter.Id);
            NetworkRestore.Remember(adapter.Id, current == ssid ? null : current, ssid);

            Log($"Connecting {adapter.Description} to {ssid}...");
            SetStatus($"Connecting to {ssid}...");
            _wlan.Connect(adapter.Id, ssid, _passwordBox.Text, _networks.FirstOrDefault(n => n.Ssid == ssid));

            var info = await WaitForConnectionAsync(adapter.Id, ssid, TimeSpan.FromSeconds(25));
            if (info == null)
            {
                Log($"Could not connect to {ssid}. Check the password and that the camera is in range.");
                SetStatus("Connection failed");
                return;
            }
            if (NetUtil.IsLinkLocal(info.Address))
                Log($"Joined {ssid} but the camera didn't hand out an address (got {info.Address}). The probe will still try.");
            else
                Log($"Connected to {ssid}. This PC: {info}");

            foreach (var (nic, other) in NetUtil.OtherOnlineAdapters(adapter.Id))
            {
                if (NetUtil.Overlaps(info, other))
                    Log($"Note: the camera network ({info.Address}/{info.PrefixLength}) uses the same addresses as {nic.Name} ({other.Address}/{other.PrefixLength}). Camera traffic is pinned to the Wi-Fi adapter, so both keep working.");
            }
            SetStatus($"Connected to {ssid} ({info.Address})");
            connected = true;
            _settings.LastSsid = ssid;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log($"Connect failed: {ex.Message}");
            SetStatus("Connection failed");
        }
        finally
        {
            _connectButton.Enabled = true;
        }

        // The stream runs until stopped, so start it without waiting on it.
        if (connected)
            _ = StartVideoAsync(probeOnFailure: interactive);
    }

    async Task<Ipv4Info?> WaitForConnectionAsync(Guid adapterId, string ssid, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        Ipv4Info? last = null;
        while (watch.Elapsed < timeout)
        {
            await Task.Delay(1000);
            if (_wlan.GetCurrentProfile(adapterId) != ssid)
                continue;
            last = NetUtil.GetIpv4(adapterId);
            if (last != null && !NetUtil.IsLinkLocal(last.Address))
                return last;
        }
        return last;
    }

    void DisconnectFromCamera()
    {
        if (SelectedAdapter is not { } adapter)
            return;
        StopVideo();
        _probeCts?.Cancel();
        try
        {
            if (NetworkRestore.Restore(_wlan) is { } restored)
            {
                Log(restored);
            }
            else if (IsCameraNetwork(_wlan.GetCurrentProfile(adapter.Id)))
            {
                Log("Disconnecting Wi-Fi from the camera.");
                _wlan.Disconnect(adapter.Id);
            }
            else
            {
                Log("Not connected to a camera network.");
            }
            SetStatus("Disconnected from camera");
        }
        catch (Exception ex)
        {
            Log($"Disconnect failed: {ex.Message}");
        }
    }

    async Task ProbeAsync()
    {
        if (_probeCts != null)
        {
            _probeCts.Cancel();
            return;
        }
        if (SelectedAdapter is not { } adapter)
            return;
        var info = NetUtil.GetIpv4(adapter.Id);
        if (info == null)
        {
            Log("The Wi-Fi adapter has no IPv4 address. Connect to the camera first.");
            return;
        }

        var report = new StringBuilder();
        void ProbeLog(string message)
        {
            lock (report)
                report.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Log(message);
        }

        string network = _wlan.GetCurrentProfile(adapter.Id) ?? "unknown network";
        ProbeLog($"Probing via {adapter.Description}, connected to {network}");

        _probeCts = new CancellationTokenSource();
        _probeButton.Text = "Stop probe";
        SetStatus("Probing camera...");
        var probe = new CameraProbe(ProbeLog);
        try
        {
            await Task.Run(() => probe.RunAsync(info, _fullScan.Checked, _probeCts.Token));
        }
        catch (OperationCanceledException)
        {
            ProbeLog("Probe cancelled.");
        }
        catch (Exception ex)
        {
            ProbeLog($"Probe error: {ex}");
        }
        finally
        {
            _probeCts.Dispose();
            _probeCts = null;
            _probeButton.Text = "Probe camera";
        }

        try
        {
            Directory.CreateDirectory(AppPaths.ReportDir);
            string safeName = string.Concat(network.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(AppPaths.ReportDir, $"probe-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, report.ToString());
            Log($"Report saved to {file}");
        }
        catch (Exception ex)
        {
            Log($"Could not save report: {ex.Message}");
        }
        SetStatus(probe.StreamUrls.Count > 0 ? "Probe found standard stream URLs (see log)." : "Probe finished.");
    }

    async Task StartVideoAsync(bool probeOnFailure = true)
    {
        if (_videoCts != null || SelectedAdapter is not { } adapter)
            return;
        var info = NetUtil.GetIpv4(adapter.Id);
        if (info == null || NetUtil.IsLinkLocal(info.Address))
        {
            Log("The Wi-Fi adapter isn't on the camera network yet. Click Connect first.");
            return;
        }

        var cameraAddress = info.Gateway ?? info.DhcpServer ?? NetUtil.FromUInt(info.NetworkValue + 1);
        string? ssid = _wlan.GetCurrentProfile(adapter.Id);
        var type = SelectedCameraType;
        string manualUrl = _streamUrlBox.Text.Trim();
        if (type == CameraType.StreamUrl && manualUrl.Length == 0)
        {
            Log("Enter the camera's stream address (rtsp://... or http://...) first.");
            return;
        }

        _videoCts = new CancellationTokenSource();
        var ct = _videoCts.Token;
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
        _startButton.Enabled = false;
        SetStatus($"Starting video from {cameraAddress}...");
        bool handshake = false;
        try
        {
            if (type == CameraType.StreamUrl)
            {
                handshake = await RunSourceAsync(new StreamCameraSource(manualUrl), info, ssid, ct);
            }
            else
            {
                handshake = await RunSourceAsync(new I4SeasonCamera(info.Address, cameraAddress), info, ssid, ct);
                if (!handshake && type == CameraType.Auto)
                {
                    // Not an i4Season camera: try a stream address found here before, then look for one.
                    if (ssid != null && _settings.StreamUrls.TryGetValue(ssid, out var known))
                        handshake = await RunSourceAsync(new StreamCameraSource(known), info, ssid, ct);
                    if (!handshake)
                    {
                        Log("Looking for a standard video stream (RTSP or HTTP)...");
                        var urls = await new CameraProbe(Log).FindStreamsAsync(info, cameraAddress, ct);
                        foreach (string url in urls)
                        {
                            handshake = await RunSourceAsync(new StreamCameraSource(url), info, ssid, ct);
                            if (handshake)
                                break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("Video stopped.");
        }
        catch (Exception ex)
        {
            Log($"Video error: {ex.Message}");
        }
        finally
        {
            _videoCts.Dispose();
            _videoCts = null;
            _startButton.Enabled = true;
            if (_pipeline.IsRecording)
                StopRecording();
            _batteryLabel.Text = "";
        }

        if (!handshake && !probeOnFailure)
        {
            SetStatus("No supported video found; will retry");
        }
        else if (!handshake)
        {
            Log("No supported video found. Running the full probe; its report shows what the camera does offer.");
            SetStatus("No supported video found; probing...");
            await ProbeAsync();
        }
        else
        {
            SetStatus("Video stopped");
        }
    }

    void StopVideo() => _videoCts?.Cancel();

    /// <summary>
    /// Streams from one source until it ends or is cancelled. Returns false if the camera didn't speak its
    /// protocol. The first frame marks this network as a known camera (and remembers a working stream address).
    /// </summary>
    async Task<bool> RunSourceAsync(ICameraSource source, Ipv4Info info, string? ssid, CancellationToken ct)
    {
        Log($"Trying {source.Description}...");
        if (source is StreamCameraSource && NetUtil.OtherOnlineAdapters(Guid.Empty)
                .Any(a => !a.Info.Address.Equals(info.Address) && NetUtil.Overlaps(info, a.Info)))
        {
            Log("Note: Ethernet uses the same addresses as the camera network, and standard streams can't be pinned " +
                "to the Wi-Fi adapter, so this may reach the wrong device. If it fails, try without Ethernet.");
        }

        int frames = 0;
        void OnFrame(CameraFrame frame)
        {
            if (Interlocked.Increment(ref frames) == 1 && IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => RememberCamera(ssid, source)));
            ShowFrame(frame);
        }
        void OnBattery(int percent)
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => _batteryLabel.Text = $"Battery {percent}%"));
        }

        source.Log += Log;
        source.FrameReceived += OnFrame;
        source.BatteryChanged += OnBattery;
        try
        {
            return await Task.Run(() => source.StreamAsync(ct), ct);
        }
        finally
        {
            source.Log -= Log;
            source.FrameReceived -= OnFrame;
            source.BatteryChanged -= OnBattery;
        }
    }

    void RememberCamera(string? ssid, ICameraSource source)
    {
        if (ssid == null)
            return;
        if (!_settings.KnownCameras.Contains(ssid))
            _settings.KnownCameras.Add(ssid);
        if (source is StreamCameraSource stream && SelectedCameraType == CameraType.Auto)
            _settings.StreamUrls[ssid] = stream.Url;
        _settings.Save();
    }

    CameraType SelectedCameraType => (CameraType)Math.Max(0, _cameraTypeBox.SelectedIndex);

    bool IsKnownCamera(string? ssid) =>
        ssid != null && (ssid == _settings.LastSsid || _settings.KnownCameras.Contains(ssid));

    bool IsCameraNetwork(string? ssid) => ssid != null && (IsKnownCamera(ssid) || CameraNamePattern.IsMatch(ssid));

    /// <summary>Start of snapshot and recording file names: the camera's network name.</summary>
    string FilePrefix()
    {
        string name = _settings.LastSsid ?? "camera";
        return string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
    }

    void PauseAuto()
    {
        if (_autoConnect.Checked && !_autoPaused)
            Log("Auto-connect paused until you click Connect or Start video.");
        _autoPaused = true;
    }

    void ResumeAuto() => _autoPaused = false;

    /// <summary>
    /// Runs every few seconds while auto-connect is on: joins the camera when it's visible,
    /// keeps the video running, and restarts it if frames stop arriving.
    /// </summary>
    async Task AutoTickAsync()
    {
        if (!_autoConnect.Checked || _autoPaused || _autoBusy || _probeCts != null ||
            !_connectButton.Enabled || SelectedAdapter is not { } adapter)
            return;

        _autoBusy = true;
        try
        {
            if (IsCameraNetwork(_wlan.GetCurrentProfile(adapter.Id)))
            {
                _autoWaitLogged = false;
                if (_videoCts == null)
                {
                    Log("Auto-connect: starting video.");
                    _ = StartVideoAsync(probeOnFailure: false);
                }
                else if (Environment.TickCount64 - Interlocked.Read(ref _lastFrameTick) > StallTimeout.TotalMilliseconds)
                {
                    Log($"Auto-connect: no video for {StallTimeout.TotalSeconds:0} seconds; restarting the stream.");
                    StopVideo(); // the next tick starts it again
                }
                return;
            }

            if (_videoCts != null)
            {
                Log("Auto-connect: lost the camera's Wi-Fi.");
                StopVideo();
            }

            try
            {
                _wlan.Scan(adapter.Id);
                await Task.Delay(4000);
            }
            catch (InvalidOperationException) { } // use cached results
            _networks = _wlan.GetNetworks(adapter.Id);
            ShowNetworks(log: false);

            var cameras = _networks.Where(n => IsKnownCamera(n.Ssid)).ToList();
            var target = cameras.FirstOrDefault(n => n.Ssid == _settings.LastSsid) ?? cameras.FirstOrDefault();
            if (target == null)
            {
                if (!_autoWaitLogged)
                    Log("Auto-connect: waiting for the camera's Wi-Fi to appear...");
                _autoWaitLogged = true;
                SetStatus("Waiting for camera...");
                return;
            }

            _autoWaitLogged = false;
            if (_autoPaused || !_autoConnect.Checked)
                return; // the user changed their mind during the scan
            Log($"Auto-connect: found {target.Ssid}.");
            _networkBox.SelectedItem = _networkBox.Items.Cast<object>().FirstOrDefault(i => i is WifiNetwork n && n.Ssid == target.Ssid);
            if (_networkBox.SelectedItem == null)
                _networkBox.Text = target.Ssid;
            await ConnectAsync(interactive: false);
        }
        catch (Exception ex)
        {
            Log($"Auto-connect error: {ex.Message}");
        }
        finally
        {
            _autoBusy = false;
        }
    }

    /// <summary>Called on the network thread for every complete frame.</summary>
    void ShowFrame(CameraFrame frame)
    {
        _lastFrame = frame;
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
        Interlocked.Increment(ref _framesThisSecond);
        _pipeline.Submit(frame);
    }

    /// <summary>Called on the pipeline thread with each finished frame.</summary>
    void ShowProcessedFrame(Bitmap bitmap)
    {
        // Every frame is still processed (stabilizing and grain reduction need each one); this only
        // skips drawing when the UI hasn't finished with the previous frame.
        if (Interlocked.Exchange(ref _drawing, 1) == 1 || IsDisposed || !IsHandleCreated)
        {
            bitmap.Dispose();
            return;
        }
        BeginInvoke(new Action(() =>
        {
            var old = _picture.Image;
            _picture.Image = bitmap;
            old?.Dispose();
            _shownThisSecond++;
            if (_status.Text?.StartsWith("Starting video") == true && _lastFrame is { } frame)
                SetStatus($"Streaming {frame.Width}x{frame.Height}");
            Interlocked.Exchange(ref _drawing, 0);
        }));
    }

    void UpdateDisplayAspect()
    {
        if (_picture.ClientSize.Height > 0)
            _pipeline.DisplayAspect = (double)_picture.ClientSize.Width / _picture.ClientSize.Height;
    }

    void ApplyEnhanceOptions()
    {
        _pipeline.Options = new EnhanceOptions
        {
            Rotation = _rotation,
            Denoise = _denoiseBox.Checked,
            DenoiseStrength = _denoiseSlider.Value,
            Sharpen = _sharpenBox.Checked,
            SharpenAmount = _sharpenSlider.Value,
            Stabilize = _stabilizeBox.Checked,
            Interpolate = _interpolateBox.Checked,
            Stretch = _stretchBox.Checked,
            Sbs3D = _sbsBox.Checked,
            Depth3D = _depthSlider.Value,
            HalfSbs = _halfSbsBox.Checked,
            SwapEyes = _swapEyesBox.Checked,
        };
        _denoiseSlider.Enabled = _denoiseBox.Checked;
        _sharpenSlider.Enabled = _sharpenBox.Checked;
        _depthSlider.Enabled = _halfSbsBox.Enabled = _swapEyesBox.Enabled = _sbsBox.Checked;

        _settings.Enhance = _pipeline.Options;
        _settings.Save();
    }

    void LoadEnhanceSettings()
    {
        var o = _settings.Enhance ?? new EnhanceOptions();
        _rotation = o.Rotation;
        _rotateButton.Text = _rotation == 0 ? "Rotate" : $"Rotate ({_rotation}\u00b0)";
        _denoiseBox.Checked = o.Denoise;
        _denoiseSlider.Value = Math.Clamp(o.DenoiseStrength, 1, 10);
        _sharpenBox.Checked = o.Sharpen;
        _sharpenSlider.Value = Math.Clamp(o.SharpenAmount, 1, 10);
        _stabilizeBox.Checked = o.Stabilize;
        _interpolateBox.Checked = o.Interpolate;
        _stretchBox.Checked = o.Stretch;
        _sbsBox.Checked = o.Sbs3D;
        _depthSlider.Value = Math.Clamp(o.Depth3D, 1, MaxIntensity);
        _halfSbsBox.Checked = o.HalfSbs;
        _swapEyesBox.Checked = o.SwapEyes;
        ApplyEnhanceOptions();
    }

    /// <summary>Loads (downloading the first time) the depth model that 2D-to-3D needs.</summary>
    async Task EnsureDepthModelAsync()
    {
        if (_depthEstimator != null || _depthLoading)
            return;
        _depthLoading = true;
        Log("3D: loading the depth model...");
        try
        {
            _depthEstimator = await DepthEstimator.CreateAsync(Log);
            _pipeline.Depth = _depthEstimator;
            Log($"3D ready: depth runs on the {_depthEstimator.Device}. Until the first depth map arrives both eyes show the same picture.");
        }
        catch (Exception ex)
        {
            Log($"3D unavailable: {ex.Message}");
            _sbsBox.Checked = false;
        }
        finally
        {
            _depthLoading = false;
        }
    }

    void ToggleRecording()
    {
        if (_pipeline.IsRecording)
        {
            StopRecording();
            return;
        }
        if (_videoCts == null)
        {
            Log("Start the video before recording.");
            return;
        }
        Directory.CreateDirectory(RecordingDir);
        var o = _pipeline.Options;
        // VR players look for these tags to show side-by-side files in 3D automatically.
        string tag = !o.Sbs3D ? "" : o.HalfSbs ? "_HSBS" : "_SBS";
        string file = Path.Combine(RecordingDir, $"{FilePrefix()}-{DateTime.Now:yyyyMMdd-HHmmss}{tag}.mp4");
        _pipeline.StartRecording(file);
        _recordButton.Text = "■ Stop recording";
        Log($"Recording to {file}");
    }

    void StopRecording()
    {
        _recordButton.Text = "● Record";
        if (_pipeline.StopRecording() is { } file && File.Exists(file))
            Log($"Recording saved: {file} ({new FileInfo(file).Length / 1024 / 1024.0:0.0} MB)");
    }

    void SaveSnapshot()
    {
        if (_lastFrame is not { } frame)
        {
            Log("No video frame yet.");
            return;
        }
        Directory.CreateDirectory(SnapshotDir);
        string file = Path.Combine(SnapshotDir, $"{FilePrefix()}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
        if (!_pipeline.Options.IsPassthrough && _picture.Image is { } shown)
            shown.Save(file, ImageFormat.Jpeg); // what's on screen: rotated, enhanced or 3D
        else
            File.WriteAllBytes(file, frame.Jpeg); // untouched camera JPEG, no re-encoding
        Log($"Snapshot saved to {file}");
    }

    void Rotate()
    {
        _rotation = (_rotation + 90) % 360;
        ApplyEnhanceOptions();
        // Rotate the frame on screen right away, so it also changes while the video is stopped.
        if (_videoCts == null && !_sbsBox.Checked)
        {
            _picture.Image?.RotateFlip(RotateFlipType.Rotate90FlipNone);
            _picture.Invalidate();
        }
        _rotateButton.Text = _rotation == 0 ? "Rotate" : $"Rotate ({_rotation}°)";
    }

    /// <summary>Video only, borderless and maximized. F11, Esc or double-click returns.</summary>
    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        SuspendLayout();
        foreach (var row in _rows)
            row.Visible = !_fullScreen;
        _statusStrip.Visible = !_fullScreen;
        _split.Panel2Collapsed = _fullScreen;
        if (_fullScreen)
        {
            _windowStateBeforeFullScreen = WindowState;
            _boundsBeforeFullScreen = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal; // re-maximize so the borderless window covers the taskbar
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            Bounds = _boundsBeforeFullScreen;
            WindowState = _windowStateBeforeFullScreen;
        }
        ResumeLayout();
    }

    void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message.Replace("\n", Environment.NewLine).Replace("\r\r", "\r")}{Environment.NewLine}";
        lock (_pendingLog)
        {
            try { File.AppendAllText(AppPaths.LogFile, line); }
            catch (IOException) { }

            // Before the window exists, InvokeRequired is false on every thread, so a background
            // thread touching the text box would create it on the wrong thread and deadlock.
            if (!IsHandleCreated)
            {
                _pendingLog.Add(line);
                return;
            }
        }
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => _logBox.AppendText(line)));
            return;
        }
        _logBox.AppendText(line);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lock (_pendingLog)
        {
            _logBox.AppendText(string.Concat(_pendingLog));
            _pendingLog.Clear();
        }
    }

    void SetStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text)));
            return;
        }
        _status.Text = text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _autoTimer.Stop();
        _probeCts?.Cancel();
        if (_pipeline.IsRecording)
            StopRecording();
        StopVideo();
        // Put the Wi-Fi back on the network it was on before we borrowed it.
        try
        {
            if (NetworkRestore.Restore(_wlan) is { } restored)
                Log(restored);
        }
        catch (Exception ex)
        {
            Log($"Could not restore Wi-Fi on close: {ex.Message}");
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _fpsTimer.Dispose();
        _pipeline.Dispose();
        _depthEstimator?.Dispose();
        _autoTimer.Dispose();
        _wlan.Dispose();
        base.OnFormClosed(e);
    }
}
