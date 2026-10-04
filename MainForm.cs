using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
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

    // Line icons from Segoe Fluent Icons (Windows 11), falling back to Segoe MDL2 Assets (Windows 10).
    const string IconScan = "\uE72C", IconConnect = "\uE701", IconDisconnect = "\uEB5E", IconPlay = "\uE768",
        IconStop = "\uE71A", IconSnapshot = "\uE722", IconRecord = "\uE7C8", IconRotate = "\uE7AD",
        IconFullScreen = "\uE740", IconProbe = "\uE9D9", IconPictures = "\uE8B9", IconVideos = "\uE8B2",
        IconDenoise = "\uF4A5", IconSharpen = "\uE793", IconStabilize = "\uE7B7", IconInterpolate = "\uE916",
        Icon3D = "\uF158", IconHalfWidth = "\uF57C", IconSwapEyes = "\uE8AB", IconShowAll = "\uE8FD",
        IconAutoConnect = "\uE8EE", IconDeepScan = "\uE773", IconStretch = "\uE9A6", IconLight = "\uEA80",
        IconTimestamp = "\uEC92";
    static readonly Font IconFont = new(
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 12f);

    readonly WlanClient _wlan = new();
    readonly ToolTip _tips = new();

    readonly ComboBox _adapterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, DropDownWidth = 340 };
    readonly ComboBox _networkBox = new() { Width = 200, DropDownWidth = 340 };
    readonly CheckBox _showAll = IconToggle(IconShowAll);
    readonly Button _scanButton = IconButton(IconScan);
    readonly TextBox _passwordBox = new() { Width = 140, UseSystemPasswordChar = true, PlaceholderText = "blank if open" };
    readonly Button _connectButton = IconButton(IconConnect);
    readonly Button _disconnectButton = IconButton(IconDisconnect);
    readonly CheckBox _autoConnect = IconToggle(IconAutoConnect);
    readonly ComboBox _cameraTypeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly TextBox _streamUrlBox = new() { Width = 250, PlaceholderText = "rtsp://... or http://..." };
    readonly Button _startButton = IconButton(IconPlay);
    readonly Button _stopButton = IconButton(IconStop);
    readonly Button _snapshotButton = IconButton(IconSnapshot);
    readonly Button _probeButton = IconButton(IconProbe);
    readonly CheckBox _fullScan = IconToggle(IconDeepScan);
    readonly Button _folderButton = IconButton(IconPictures);
    readonly Button _recordButton = IconButton(IconRecord);
    readonly Button _recordingsButton = IconButton(IconVideos);
    readonly ToolStripStatusLabel _recordLabel = new() { ForeColor = Color.Red };
    readonly Button _rotateButton = IconButton(IconRotate);
    readonly Button _fullScreenButton = IconButton(IconFullScreen);
    readonly ComboBox _resolutionBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Margin = new Padding(3, 5, 6, 3) };
    static readonly (string Name, int Lines)[] Resolutions = [("Full", 0), ("1080p", 1080), ("720p", 720), ("480p", 480)];
    readonly CheckBox _denoiseBox = IconToggle(IconDenoise);
    readonly TrackBar _denoiseSlider = Slider();
    readonly CheckBox _sharpenBox = IconToggle(IconSharpen);
    readonly TrackBar _sharpenSlider = Slider();
    readonly CheckBox _stabilizeBox = IconToggle(IconStabilize);
    readonly CheckBox _interpolateBox = IconToggle(IconInterpolate);
    readonly CheckBox _stretchBox = IconToggle(IconStretch);
    readonly CheckBox _lightBox = IconToggle(IconLight);
    readonly CheckBox _timestampBox = IconToggle(IconTimestamp);
    readonly TrackBar _lightSlider = Slider();
    readonly Label _perfCamera = Metric("Camera: -");
    readonly Label _perfShown = Metric("Shown: -");
    readonly Label _perfLatency = Metric("Latency: -", 175);
    readonly Label _perfDepth = Metric("Depth: -");
    readonly Label _perfDropped = Metric("Skipped: -");
    readonly Label _perfSize = Metric("Resolution: -", 150);
    readonly CheckBox _sbsBox = IconToggle(Icon3D);
    readonly TrackBar _depthSlider = Slider(MaxIntensity);
    readonly CheckBox _halfSbsBox = IconToggle(IconHalfWidth);
    readonly CheckBox _swapEyesBox = IconToggle(IconSwapEyes);
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
    string? _autoWaitMessage; // last "waiting" message, so it's logged once, not every tick
    int _autoFailures;      // auto-connect video attempts in a row that got no video
    long _autoRetryTick;    // when auto-connect may try the video again (backs off after failures)
    long _framesReceived;   // camera frames since the app started
    I4SeasonCamera? _lightCamera; // the streaming camera whose light the Light controls switch, if any
    bool _lightBusy;        // a light command is waiting for the camera's answer
    bool _lightDirty;       // the controls changed meanwhile; send again once it answers
    bool _lightUpdating;    // showing the camera's reported state, not a user change
    VStarcamCamera? _timestampCamera; // the streaming camera whose on-picture date/time the Timestamp box switches
    bool _timestampUpdating; // showing the camera's state, not a user change

    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    public MainForm()
    {
        Text = "WiFi Camera Link";
        ClientSize = new Size(1600, 1000);
        MinimumSize = new Size(800, 500);

        Directory.CreateDirectory(AppPaths.AppDir);
        BuildLayout();
        foreach (var (control, name, description) in new (Control, string, string)[]
        {
            (_scanButton, "Scan", "look for Wi-Fi networks"),
            (_showAll, "Show all", "list every Wi-Fi network, not just ones that look like cameras"),
            (_connectButton, "Connect", "join the selected camera's Wi-Fi"),
            (_disconnectButton, "Disconnect", "leave the camera's Wi-Fi and go back to the previous network"),
            (_autoConnect, "Auto-connect", "join the camera's Wi-Fi and keep the video running, reconnecting if it drops"),
            (_timestampBox, "Timestamp", "the date and time the camera draws on the picture (VStarcam cameras; the camera remembers it)"),
            (_fullScan, "Deep scan", "make the probe check every TCP and UDP port (takes a few minutes)"),
            (_lightBox, "Light", "the camera's built-in light (i4Season cameras; not every model can be switched remotely)"),
            (_lightSlider, "Brightness", "how bright the camera's light is"),
            (_startButton, "Start video", "start streaming from the camera"),
            (_stopButton, "Stop video", "stop streaming (auto-connect waits until you start it again)"),
            (_fullScreenButton, "Full screen", "show only the video (F11; Esc or double-click to return)"),
            (_stretchBox, "Stretch to fill", "reshape the picture to fill the video area (also applies to snapshots and recordings)"),
            (_snapshotButton, "Snapshot", "save the current picture as a JPEG"),
            (_folderButton, "Snapshots folder", "open the folder snapshots are saved in"),
            (_recordingsButton, "Recordings folder", "open the folder recordings are saved in"),
            (_resolutionBox, "Resampling", "scale the camera's picture down before enhancement and 3D, so they keep up " +
                "(recordings use it too); the camera still sends its full resolution"),
            (_denoiseBox, "Reduce grain", "smooth out sensor noise"),
            (_denoiseSlider, "Grain reduction strength", "how strongly grain is smoothed"),
            (_sharpenBox, "Sharpen", "bring out fine detail"),
            (_sharpenSlider, "Sharpening amount", "how strongly detail is sharpened"),
            (_stabilizeBox, "Stabilize", "steady a shaky picture"),
            (_interpolateBox, "Double frame rate", "insert a motion-estimated frame between camera frames; 20 fps becomes 40, about 50 ms later"),
            (_sbsBox, "Convert to 3D", "side-by-side 3D from estimated depth, for VR headsets and 3D TVs"),
            (_depthSlider, "3D intensity", "how strong the depth effect is"),
            (_halfSbsBox, "Half width", "squeeze both eyes into one normal-width frame"),
            (_swapEyesBox, "Swap eyes", "put the left eye's view on the right, for cross-eyed viewing"),
            (_perfLatency, "Latency", "time from a camera frame arriving to the finished frame reaching the display"),
            (_perfDropped, "Skipped", "camera frames skipped because processing was still busy with the previous one"),
            (_perfSize, "Resolution", "the picture as the camera sends it, and after resampling"),
        })
            Describe(control, name, description);
        ShowRecordButton(recording: false);
        ShowProbeButton(probing: false);
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
        _cameraTypeBox.Items.AddRange(["Auto-detect", "i4Season (YPC)", "Stream URL", "VStarcam (O-KAM)"]);
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
            if (!_pipeline.IsRecording && _recordButton.Text != IconRecord)
                ShowRecordButton(recording: false); // recording ended on its own (e.g. an encoder error)
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
            _perfSize.Text = live && _pipeline.Sizes is { } sizes
                ? sizes.Native == sizes.Resampled
                    ? $"Resolution: {sizes.Native.Width}\u00d7{sizes.Native.Height}"
                    : $"Resolution: {sizes.Native.Width}\u00d7{sizes.Native.Height} \u2192 {sizes.Resampled.Width}\u00d7{sizes.Resampled.Height}"
                : "Resolution: -";
            _perfDropped.ForeColor = stats.Dropped > 0 ? Color.DarkOrange : SystemColors.ControlText;
        };
        _pipeline.FrameReady += ShowProcessedFrame;
        _pipeline.Log += Log;
        _resolutionBox.Items.AddRange([.. Resolutions.Select(r => r.Name)]);
        LoadEnhanceSettings();
        _resolutionBox.SelectedIndexChanged += (_, _) => ApplyEnhanceOptions();
        foreach (var box in new[] { _denoiseBox, _sharpenBox, _stabilizeBox, _interpolateBox, _stretchBox, _halfSbsBox, _swapEyesBox })
            box.CheckedChanged += (_, _) => ApplyEnhanceOptions();
        foreach (var slider in new[] { _denoiseSlider, _sharpenSlider, _depthSlider })
            slider.ValueChanged += (_, _) => ApplyEnhanceOptions();
        _lightSlider.Value = _lightSlider.Maximum;
        _lightBox.CheckedChanged += async (_, _) => await ApplyLightAsync();
        _lightSlider.ValueChanged += async (_, _) => await ApplyLightAsync();
        UpdateLightControls();
        _timestampBox.Enabled = false;
        _timestampBox.CheckedChanged += async (_, _) => await ApplyTimestampAsync();
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
            Section("Camera", Label("Type"), _cameraTypeBox, Label("URL"), _streamUrlBox, _timestampBox),
            Section("Diagnostics", _probeButton, _fullScan),
            Section("Light", _lightBox, _lightSlider),
            Section("Video", _startButton, _stopButton, _rotateButton, _fullScreenButton, _stretchBox, _resolutionBox),
            Section("Capture", _snapshotButton, _recordButton, _folderButton, _recordingsButton),
            Section("Enhance", _denoiseBox, _denoiseSlider, _sharpenBox, _sharpenSlider, _stabilizeBox, _interpolateBox),
            Section("3D", _sbsBox, _depthSlider, _halfSbsBox, _swapEyesBox),
            Section("Performance", _perfCamera, _perfShown, _perfLatency, _perfDepth, _perfDropped, _perfSize),
        ]);
        _rows.Add(toolbar);
        foreach (var box in toolbar.Controls.OfType<GroupBox>())
            ShowTooltipsWhenDisabled(box.Controls[0]);
        // Sections hold different controls (buttons, sliders, labels), so their natural heights differ;
        // give them all the tallest one's height so the rows line up.
        Load += (_, _) =>
        {
            // Glyphs differ in width, so size every icon button and toggle like the largest one.
            var icons = toolbar.Controls.OfType<GroupBox>().SelectMany(b => b.Controls[0].Controls.Cast<Control>())
                .Where(c => c.Font.Name == IconFont.Name).ToList();
            var iconSize = new Size(icons.Max(c => c.PreferredSize.Width), icons.Max(c => c.PreferredSize.Height));
            foreach (var icon in icons)
                icon.MinimumSize = iconSize;
            MatchHeight(toolbar.Controls.OfType<GroupBox>().SelectMany(b => b.Controls[0].Controls.Cast<Control>())
                .Where(c => c.Font.Name != IconFont.Name), iconSize.Height);

            var sections = toolbar.Controls.OfType<GroupBox>().ToList();
            int height = sections.Max(b => b.PreferredSize.Height);
            foreach (var box in sections)
                box.MinimumSize = new Size(box.MinimumSize.Width, height);
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
        // A section with only a few icons would otherwise be narrower than its title and cut it off.
        box.MinimumSize = new Size(TextRenderer.MeasureText(title, box.Font).Width + 16, 0);
        // Sit just below the title, wherever the box says that is at the current display scaling.
        box.Layout += (_, _) => line.Location = box.DisplayRectangle.Location;
        return box;
    }

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    static Button IconButton(string glyph) => new()
    {
        Text = glyph, Font = IconFont, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(6, 3, 6, 1),
    };

    /// <summary>
    /// Gives labels, text boxes and dropdowns the icon buttons' height, so each toolbar row lines up:
    /// labels centre their text, and dropdowns are owner-drawn because that's the only way to set their height.
    /// Sliders can't change height, so they're centred instead.
    /// </summary>
    static void MatchHeight(IEnumerable<Control> controls, int height)
    {
        const int top = 3; // the buttons' default margin
        foreach (var control in controls)
        {
            switch (control)
            {
                case Label label:
                    label.MinimumSize = new Size(label.MinimumSize.Width, height);
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    label.Margin = new Padding(label.Margin.Left, top, label.Margin.Right, top);
                    break;
                case TextBox box:
                    box.AutoSize = false;
                    box.Height = height;
                    box.Margin = new Padding(box.Margin.Left, top, box.Margin.Right, top);
                    break;
                case ComboBox combo:
                    // Widths are set in pixels and don't grow with display scaling, so let a box widen to fit
                    // its longest choice (up to half again its width; long adapter names still get "...").
                    FitWidth(combo);
                    combo.DrawMode = DrawMode.OwnerDrawVariable;
                    combo.MeasureItem += (_, e) => e.ItemHeight = combo.Font.Height + 4; // list rows stay normal
                    combo.DrawItem += DrawComboItem;
                    combo.ItemHeight = height - 6; // the selection field; the border adds the rest
                    combo.Margin = new Padding(combo.Margin.Left, top, combo.Margin.Right, top);
                    break;
                default:
                    int spare = Math.Max(0, height - control.Height);
                    control.Margin = new Padding(control.Margin.Left, top + spare / 2, control.Margin.Right, top + spare - spare / 2);
                    break;
            }
        }
    }

    /// <summary>
    /// Widths are set in pixels and don't grow with display scaling, so a dropdown widens to fit its longest
    /// choice, up to half again its designed width (long adapter names still end in "...").
    /// </summary>
    static void FitWidth(ComboBox combo)
    {
        if (combo.DropDownStyle != ComboBoxStyle.DropDownList || combo.Items.Count == 0)
            return;
        int designed = combo.Tag as int? ?? combo.Width;
        combo.Tag = designed;
        int longest = combo.Items.Cast<object>().Max(i => TextRenderer.MeasureText(combo.GetItemText(i), combo.Font).Width);
        int needed = longest + SystemInformation.VerticalScrollBarWidth * combo.DeviceDpi / 96 + 12;
        combo.Width = Math.Clamp(needed, designed, designed * 3 / 2);
    }

    static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        var combo = (ComboBox)sender!;
        e.DrawBackground();
        if (e.Index >= 0)
        {
            var bounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds, e.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        e.DrawFocusRectangle();
    }

    /// <summary>A checkbox drawn as an icon button that stays pressed while it's on.</summary>
    static CheckBox IconToggle(string glyph) => new()
    {
        Appearance = Appearance.Button, Text = glyph, Font = IconFont, AutoSize = true,
        Padding = new Padding(6, 3, 6, 1), TextAlign = ContentAlignment.MiddleCenter,
        Margin = new Padding(3, 3, 0, 3),
    };

    /// <summary>
    /// WinForms shows no tooltip over a disabled control, because it gets no mouse messages; its panel does,
    /// so the panel shows the disabled control's tooltip instead.
    /// </summary>
    void ShowTooltipsWhenDisabled(Control panel)
    {
        Control? shown = null;
        panel.MouseMove += (_, e) =>
        {
            var child = panel.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible);
            var target = child is { Enabled: false } && _tips.GetToolTip(child).Length > 0 ? child : null;
            if (target == shown)
                return;
            shown = target;
            if (target == null)
                _tips.Hide(panel);
            else
                _tips.Show(_tips.GetToolTip(target), panel, target.Left, target.Bottom + 4, _tips.AutoPopDelay);
        };
        panel.MouseLeave += (_, _) =>
        {
            if (shown != null)
                _tips.Hide(panel);
            shown = null;
        };
    }

    /// <summary>
    /// Icon controls have no visible text, so the tooltip ("Name: description") and screen readers say what they do.
    /// </summary>
    void Describe(Control control, string name, string description)
    {
        _tips.SetToolTip(control, $"{name}: {description}");
        control.AccessibleName = name;
        control.AccessibleDescription = description;
    }

    void ShowRecordButton(bool recording)
    {
        _recordButton.Text = recording ? IconStop : IconRecord;
        _recordButton.ForeColor = recording ? SystemColors.ControlText : Color.Red;
        if (recording)
            Describe(_recordButton, "Stop recording", "finish the recording and save it");
        else
            Describe(_recordButton, "Record", "record the video as shown (rotated, enhanced or 3D) to an MP4 file");
    }

    void ShowProbeButton(bool probing)
    {
        _probeButton.Text = probing ? IconStop : IconProbe;
        if (probing)
            Describe(_probeButton, "Stop probe", "cancel the probe");
        else
            Describe(_probeButton, "Probe camera", "list every port and service the camera offers, and save a report");
    }

    void ShowRotation() =>
        Describe(_rotateButton, "Rotate", $"turn the picture 90\u00b0 clockwise (now {_rotation}\u00b0)");


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
        Log($"WiFi Camera Link started {DateTime.Now:yyyy-MM-dd}. Keep Ethernet plugged in so this PC stays online while the Wi-Fi adapter joins the camera.");
        try
        {
            var adapters = _wlan.GetInterfaces();
            _adapterBox.Items.AddRange(adapters.ToArray<object>());
            FitWidth(_adapterBox); // its choices arrive after the toolbar was sized
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
        ShowProbeButton(probing: true);
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
            ShowProbeButton(probing: false);
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
            ICameraSource I4Season() => new I4SeasonCamera(info.Address, cameraAddress);
            ICameraSource VStarcam() => new VStarcamCamera(info.Address, cameraAddress, _settings.VStarcamPassword);

            if (type == CameraType.StreamUrl)
            {
                handshake = await RunSourceAsync(new StreamCameraSource(manualUrl), info, ssid, ct);
            }
            else if (type == CameraType.I4Season)
            {
                handshake = await RunSourceAsync(I4Season(), info, ssid, ct);
            }
            else if (type == CameraType.VStarcam)
            {
                handshake = await RunSourceAsync(VStarcam(), info, ssid, ct);
            }
            else
            {
                // Each kind of camera in turn, starting with the one that worked on this network before.
                string? knownUrl = ssid != null && _settings.StreamUrls.TryGetValue(ssid, out var url) ? url : null;
                var kinds = new List<(CameraType Type, Func<ICameraSource?> Create)>
                {
                    (CameraType.I4Season, I4Season),
                    (CameraType.VStarcam, VStarcam),
                    (CameraType.StreamUrl, () => knownUrl == null ? null : new StreamCameraSource(knownUrl)),
                };
                if (ssid != null && _settings.CameraTypes.TryGetValue(ssid, out var remembered))
                    kinds = [.. kinds.OrderBy(k => k.Type != remembered)];
                foreach (var kind in kinds)
                {
                    if (kind.Create() is { } source && (handshake = await RunSourceAsync(source, info, ssid, ct)))
                        break;
                }
                if (!handshake)
                {
                    Log("Looking for a standard video stream (RTSP or HTTP)...");
                    var urls = await new CameraProbe(Log).FindStreamsAsync(info, cameraAddress, ct);
                    foreach (string found in urls)
                    {
                        handshake = await RunSourceAsync(new StreamCameraSource(found), info, ssid, ct);
                        if (handshake)
                            break;
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
                BeginInvoke(new Action(() =>
                {
                    RememberCamera(ssid, source);
                    if (source is I4SeasonCamera camera && !ct.IsCancellationRequested)
                        EnableLight(camera);
                    if (source is VStarcamCamera vstarcam && !ct.IsCancellationRequested)
                        EnableTimestamp(vstarcam);
                }));
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
            if (_lightCamera == source)
                EnableLight(null);
            if (_timestampCamera == source)
                EnableTimestamp(null);
        }
    }

    /// <summary>Points the Timestamp box at a streaming camera (or none), showing the camera's current setting.</summary>
    void EnableTimestamp(VStarcamCamera? camera)
    {
        _timestampCamera = camera;
        _timestampBox.Enabled = camera?.TimestampShown != null;
        if (camera?.TimestampShown is { } shown)
            ShowTimestampState(shown);
    }

    void ShowTimestampState(bool shown)
    {
        _timestampUpdating = true;
        _timestampBox.Checked = shown;
        _timestampUpdating = false;
    }

    async Task ApplyTimestampAsync()
    {
        if (_timestampUpdating || _timestampCamera is not { } camera)
            return;
        bool show = _timestampBox.Checked;
        _timestampBox.Enabled = false; // one change at a time; the camera's CGI server is easily overwhelmed
        try
        {
            if (await camera.SetTimestampAsync(show, CancellationToken.None))
            {
                Log(show ? "Camera timestamp on." : "Camera timestamp off.");
            }
            else
            {
                Log("The camera didn't accept the timestamp change.");
                ShowTimestampState(!show);
            }
        }
        finally
        {
            _timestampBox.Enabled = _timestampCamera == camera;
        }
    }

    /// <summary>Points the Light controls at a streaming camera (or none), re-applying the light if it's switched on.</summary>
    void EnableLight(I4SeasonCamera? camera)
    {
        _lightCamera = camera;
        UpdateLightControls();
        if (camera != null && _lightBox.Checked)
            _ = ApplyLightAsync();
    }

    void UpdateLightControls()
    {
        _lightBox.Enabled = _lightCamera != null;
        _lightSlider.Enabled = _lightCamera != null && _lightBox.Checked;
    }

    /// <summary>
    /// Sends the Light controls' state to the camera. Changes made while a command is in flight (e.g. dragging
    /// the slider) are merged into one follow-up command instead of queueing one per step.
    /// </summary>
    async Task ApplyLightAsync()
    {
        UpdateLightControls();
        if (_lightUpdating || _lightCamera is not { } camera)
            return;
        if (_lightBusy)
        {
            _lightDirty = true;
            return;
        }
        _lightBusy = true;
        try
        {
            do
            {
                _lightDirty = false;
                bool on = _lightBox.Checked;
                int brightness = _lightSlider.Value * 100 / _lightSlider.Maximum;
                bool? state = await camera.SetLightAsync(on, brightness, CancellationToken.None);
                if (state == null)
                {
                    Log("The camera didn't confirm the light command; this model may not support remote light control.");
                    break;
                }
                Log(state.Value ? $"Light on, brightness {brightness}%." : "Light off.");
                if (state.Value != on)
                {
                    _lightUpdating = true;
                    _lightBox.Checked = state.Value;
                    _lightUpdating = false;
                    UpdateLightControls();
                }
            } while (_lightDirty && _lightCamera == camera);
        }
        catch (SocketException ex)
        {
            Log($"Light command failed: {ex.Message}");
        }
        finally
        {
            _lightBusy = false;
        }
    }

    void RememberCamera(string? ssid, ICameraSource source)
    {
        if (ssid == null)
            return;
        if (!_settings.KnownCameras.Contains(ssid))
            _settings.KnownCameras.Add(ssid);
        if (SelectedCameraType == CameraType.Auto)
        {
            _settings.CameraTypes[ssid] = source switch
            {
                I4SeasonCamera => CameraType.I4Season,
                VStarcamCamera => CameraType.VStarcam,
                _ => CameraType.StreamUrl,
            };
            if (source is StreamCameraSource stream)
                _settings.StreamUrls[ssid] = stream.Url;
        }
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

    void ResumeAuto()
    {
        _autoPaused = false;
        ResetAutoRetry();
    }

    void ResetAutoRetry()
    {
        _autoFailures = 0;
        _autoRetryTick = 0;
    }

    /// <summary>
    /// Starts the video for auto-connect. When it gets no video, the next try waits longer each time
    /// (10 s, 20 s, 40 s ... up to 5 minutes), so a camera the app can't read doesn't flood the log.
    /// </summary>
    async Task AutoStartVideoAsync()
    {
        long before = Interlocked.Read(ref _framesReceived);
        await StartVideoAsync(probeOnFailure: false);
        if (Interlocked.Read(ref _framesReceived) != before)
        {
            ResetAutoRetry(); // it streamed, so a drop is worth retrying right away
            return;
        }
        _autoFailures++;
        int delaySeconds = Math.Min(300, 10 << Math.Min(_autoFailures - 1, 5));
        _autoRetryTick = Environment.TickCount64 + delaySeconds * 1000L;
        Log($"Auto-connect: no video ({_autoFailures} {(_autoFailures == 1 ? "try" : "tries in a row")}); " +
            $"trying again in {(delaySeconds < 60 ? $"{delaySeconds} seconds" : $"{delaySeconds / 60} minute(s)")}.");
        SetStatus($"No supported video found; retrying in {delaySeconds} s");
    }

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
                _autoWaitMessage = null;
                if (_videoCts == null)
                {
                    if (Environment.TickCount64 < _autoRetryTick)
                        return;
                    Log(_autoFailures == 0 ? "Auto-connect: starting video." : $"Auto-connect: trying the video again (try {_autoFailures + 1}).");
                    _ = AutoStartVideoAsync();
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
            ResetAutoRetry(); // off the camera network: the next camera gets a fresh start

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
                // Auto-connect only joins networks that have streamed before; say so when a likely camera is in range.
                var untried = _networks.Where(n => IsCameraNetwork(n.Ssid)).Select(n => n.Ssid).ToList();
                string message = untried.Count > 0
                    ? $"Auto-connect: {string.Join(", ", untried)} looks like a camera but hasn't streamed here before, " +
                      "so it isn't joined automatically. Pick it under Network and click Connect to try it."
                    : "Auto-connect: waiting for a camera's Wi-Fi to appear...";
                if (message != _autoWaitMessage)
                    Log(message);
                _autoWaitMessage = message;
                SetStatus("Waiting for camera...");
                return;
            }

            _autoWaitMessage = null;
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
        Interlocked.Increment(ref _framesReceived);
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
            MaxLines = Resolutions[Math.Max(0, _resolutionBox.SelectedIndex)].Lines,
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
        ShowRotation();
        _denoiseBox.Checked = o.Denoise;
        _denoiseSlider.Value = Math.Clamp(o.DenoiseStrength, 1, 10);
        _sharpenBox.Checked = o.Sharpen;
        _sharpenSlider.Value = Math.Clamp(o.SharpenAmount, 1, 10);
        _stabilizeBox.Checked = o.Stabilize;
        _interpolateBox.Checked = o.Interpolate;
        _stretchBox.Checked = o.Stretch;
        _resolutionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Resolutions, r => r.Lines == o.MaxLines));
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
        ShowRecordButton(recording: true);
        Log($"Recording to {file}");
    }

    void StopRecording()
    {
        ShowRecordButton(recording: false);
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
        ShowRotation();
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
