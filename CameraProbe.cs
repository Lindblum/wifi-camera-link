using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WiFiCameraLink;

/// <summary>
/// Figures out how the camera talks: finds it on the hotspot's subnet, scans ports,
/// and checks for HTTP/MJPEG, RTSP and the common UDP discovery protocols used by cheap cameras.
/// </summary>
public sealed class CameraProbe
{
    static readonly int[] CommonPorts =
    [
        21, 23, 80, 81, 443, 554, 1935, 3333, 5000, 5007, 5544, 6000, 6123, 6789, 7070, 7777,
        8000, 8001, 8080, 8081, 8088, 8090, 8554, 8888, 8899, 9000, 9527, 10080, 10554, 34567, 49152,
    ];

    static readonly string[] HttpPaths =
    [
        "/?action=stream", "/stream", "/video", "/video.mjpg", "/video.cgi", "/mjpeg", "/mjpg/video.mjpg",
        "/videostream.cgi", "/live", "/livestream", "/cgi-bin/mjpg/video.cgi", "/snapshot.jpg",
        "/snapshot.cgi", "/image.jpg", "/tmpfs/auto.jpg", "/webcapture.jpg?command=snap&channel=1",
    ];

    static readonly string[] RtspPaths =
    [
        "/", "/live", "/live/ch00_0", "/live/ch00_1", "/live/main", "/11", "/12", "/h264", "/h264_stream",
        "/onvif1", "/stream1", "/stream0", "/av0_0", "/ch0", "/ch0_0.h264", "/main", "/video1", "/live.sdp",
        "/cam/realmonitor?channel=1&subtype=0", "/0", "/1",
    ];

    // Windows' SIO_UDP_CONNRESET: stops "port unreachable" replies from breaking a UDP socket's receive loop.
    const int SioUdpConnReset = -1744830452;

    readonly Action<string> _log;
    readonly List<string> _streamUrls = [];
    IPAddress _localAddress = IPAddress.Any;
    int _refused, _timedOut;

    public CameraProbe(Action<string> log) => _log = log;

    public IReadOnlyList<string> StreamUrls => _streamUrls;

    public async Task RunAsync(Ipv4Info local, bool fullScan, CancellationToken ct)
    {
        _log($"Probe started. This PC on the camera network: {local}");

        // Every socket is bound to our address on the camera network. Windows then sends only through
        // the Wi-Fi adapter, even when Ethernet is on a home network with the same addresses.
        _localAddress = local.Address;
        var overlapping = NetUtil.OtherOnlineAdapters(Guid.Empty)
            .Where(a => !a.Info.Address.Equals(local.Address) && NetUtil.Overlaps(local, a.Info))
            .ToList();
        foreach (var (nic, info) in overlapping)
            _log($"{nic.Name} ({info.Address}/{info.PrefixLength}) uses the same address range as the camera; skipping the ping sweep, which can't be pinned to Wi-Fi.");

        var hosts = await FindHostsAsync(local, pingSweep: overlapping.Count == 0, ct);
        _log($"Candidate camera address(es): {string.Join(", ", hosts)}");

        foreach (var host in hosts)
        {
            _refused = _timedOut = 0;
            _log($"[{host}] Scanning {CommonPorts.Length} common TCP ports...");
            var open = await ScanPortsAsync(host, CommonPorts, 32, ct);
            if (fullScan)
            {
                var rest = Enumerable.Range(1, 65535).Except(CommonPorts).ToArray();
                _log($"[{host}] Scanning the other {rest.Length} TCP ports (a minute or two)...");
                open.AddRange(await ScanPortsAsync(host, rest, 200, ct));
                open.Sort();
            }
            _log(open.Count == 0
                ? $"[{host}] No open TCP ports. Closed ports: {_refused} refused, {_timedOut} silent."
                : $"[{host}] Open TCP ports: {string.Join(", ", open)} (others: {_refused} refused, {_timedOut} silent)");

            foreach (int port in open)
                await InspectPortAsync(host, port, ct);
        }

        await UdpDiscoveryAsync(local, hosts, ct);

        if (fullScan)
        {
            foreach (var host in hosts)
                await UdpSweepAsync(host, ct);
        }

        _log(_streamUrls.Count > 0
            ? $"Probe finished. Stream candidates:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", _streamUrls)}"
            : "Probe finished. No standard video stream found. Save this report so the protocol can be worked out.");
    }

    /// <summary>
    /// Quick search for a standard stream on one camera: common TCP ports, then HTTP/RTSP checks on the
    /// open ones. A few seconds, versus the full probe's sweeps and UDP tests.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindStreamsAsync(Ipv4Info local, IPAddress camera, CancellationToken ct)
    {
        _localAddress = local.Address;
        foreach (int port in await ScanPortsAsync(camera, CommonPorts, 32, ct))
            await InspectPortAsync(camera, port, ct);
        return StreamUrls;
    }

    async Task<List<IPAddress>> FindHostsAsync(Ipv4Info local, bool pingSweep, CancellationToken ct)
    {
        var hosts = new List<IPAddress>();
        void Add(IPAddress? address)
        {
            if (address != null && !address.Equals(local.Address) && !hosts.Contains(address))
                hosts.Add(address);
        }

        Add(local.Gateway);
        Add(local.DhcpServer);

        // Ping sweep, capped at the /24 around our address.
        uint net = local.PrefixLength >= 24 ? local.NetworkValue : NetUtil.ToUInt(local.Address) & 0xFFFFFF00;
        uint broadcast = local.PrefixLength >= 24 ? NetUtil.ToUInt(local.Broadcast) : net | 0xFF;
        if (pingSweep)
        {
            var candidates = Enumerable.Range(1, (int)(broadcast - net - 1)).Select(i => NetUtil.FromUInt(net + (uint)i));

            _log("Ping sweep of the camera subnet...");
            var alive = new ConcurrentBag<IPAddress>();
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct },
                async (ip, _) =>
                {
                    using var ping = new Ping();
                    try
                    {
                        if ((await ping.SendPingAsync(ip, 700)).Status == IPStatus.Success)
                            alive.Add(ip);
                    }
                    catch (PingException) { }
                });
            foreach (var ip in alive.OrderBy(NetUtil.ToUInt))
                Add(ip);
        }

        // The ARP table also catches devices that ignore ping, and the MAC prefix identifies the chip vendor.
        foreach (var (ip, mac) in ReadArpTable(local))
        {
            _log($"ARP: {ip} has MAC {mac}");
            Add(ip);
        }

        if (hosts.Count == 0)
            Add(NetUtil.FromUInt(net + 1));
        return hosts;
    }

    List<(IPAddress Ip, string Mac)> ReadArpTable(Ipv4Info local)
    {
        var result = new List<(IPAddress, string)>();
        try
        {
            using var arp = Process.Start(new ProcessStartInfo("arp", $"-a -N {local.Address}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            string output = arp.StandardOutput.ReadToEnd();
            arp.WaitForExit(3000);

            foreach (Match m in Regex.Matches(output, @"^\s*(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})", RegexOptions.Multiline))
            {
                var ip = IPAddress.Parse(m.Groups[1].Value);
                string mac = m.Groups[2].Value.ToLowerInvariant();
                bool sameSubnet = (NetUtil.ToUInt(ip) & NetUtil.ToUInt(local.Mask)) == local.NetworkValue;
                if (sameSubnet && !ip.Equals(local.Broadcast) && mac != "ff-ff-ff-ff-ff-ff" && !mac.StartsWith("01-00-5e"))
                    result.Add((ip, mac));
            }
        }
        catch (Exception ex)
        {
            _log($"Could not read the ARP table: {ex.Message}");
        }
        return result;
    }

    async Task<List<int>> ScanPortsAsync(IPAddress host, int[] ports, int parallelism, CancellationToken ct)
    {
        var open = new ConcurrentBag<int>();
        int done = 0;
        await Parallel.ForEachAsync(ports, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (port, token) =>
            {
                if (await IsOpenAsync(host, port, 500, token))
                {
                    open.Add(port);
                    _log($"[{host}] Port {port} is open");
                }
                int count = Interlocked.Increment(ref done);
                if (ports.Length > 1000 && count % 5000 == 0)
                    _log($"[{host}] ...{count}/{ports.Length} ports checked");
            });
        return open.Order().ToList();
    }

    async Task<bool> IsOpenAsync(IPAddress host, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = NewTcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _timedOut);
            return false;
        }
        catch (SocketException ex)
        {
            if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                Interlocked.Increment(ref _refused);
            else
                Interlocked.Increment(ref _timedOut);
            return false;
        }
    }

    TcpClient NewTcpClient() => new(new IPEndPoint(_localAddress, 0));

    UdpClient NewUdpClient()
    {
        var udp = new UdpClient(new IPEndPoint(_localAddress, 0)) { EnableBroadcast = true };
        udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        return udp;
    }

    async Task InspectPortAsync(IPAddress host, int port, CancellationToken ct)
    {
        string tag = $"[{host}:{port}]";

        // Some services (telnet, ftp, proprietary) talk first.
        var banner = await ExchangeAsync(host, port, null, 1000, 512, ct);
        if (banner.Length > 0)
            _log($"{tag} Sends on connect: {Describe(banner)}");

        var http = await ExchangeAsync(host, port, HttpRequest(host, port, "/"), 1500, 2048, ct);
        if (StartsWith(http, "HTTP/"))
        {
            _log($"{tag} HTTP server. Response to GET /:{Environment.NewLine}{Indent(Describe(http))}");
            await ProbeHttpPathsAsync(host, port, ct);
            return;
        }
        if (http.Length > 0)
            _log($"{tag} Non-HTTP reply to GET: {Describe(http)}");

        var rtsp = await ExchangeAsync(host, port, RtspRequest("OPTIONS", $"rtsp://{host}:{port}/", 1), 1500, 2048, ct);
        if (StartsWith(rtsp, "RTSP/"))
        {
            _log($"{tag} RTSP server:{Environment.NewLine}{Indent(Describe(rtsp))}");
            await ProbeRtspPathsAsync(host, port, ct);
            return;
        }
        if (rtsp.Length > 0)
            _log($"{tag} Non-RTSP reply to OPTIONS: {Describe(rtsp)}");
        else if (banner.Length == 0 && http.Length == 0)
            _log($"{tag} Open but silent: probably a proprietary protocol.");
    }

    async Task ProbeHttpPathsAsync(IPAddress host, int port, CancellationToken ct)
    {
        foreach (string path in HttpPaths)
        {
            var response = await ExchangeAsync(host, port, HttpRequest(host, port, path), 1500, 1024, ct);
            if (!StartsWith(response, "HTTP/"))
                continue;

            string head = Encoding.ASCII.GetString(response);
            string status = head.Split("\r\n", 2)[0];
            string contentType = Regex.Match(head, @"^Content-Type:\s*(.+?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
            string url = port == 80 ? $"http://{host}{path}" : $"http://{host}:{port}{path}";

            if (status.Contains(" 404"))
                continue;
            _log($"[{host}:{port}] GET {path} -> {status} {contentType}");

            if (status.Contains(" 200") && (contentType.Contains("multipart", StringComparison.OrdinalIgnoreCase) ||
                                            contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
                AddUrl(url);
        }
    }

    async Task ProbeRtspPathsAsync(IPAddress host, int port, CancellationToken ct)
    {
        int cseq = 2;
        foreach (string path in RtspPaths)
        {
            string url = $"rtsp://{host}:{port}{path}";
            var response = await ExchangeAsync(host, port, RtspRequest("DESCRIBE", url, cseq++), 1500, 4096, ct);
            if (!StartsWith(response, "RTSP/"))
                continue;

            string text = Encoding.ASCII.GetString(response);
            string status = text.Split("\r\n", 2)[0];
            if (status.Contains(" 200"))
            {
                var media = Regex.Matches(text, @"^(m=.+|a=rtpmap:.+)\r?$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim());
                _log($"[{host}:{port}] DESCRIBE {path} -> {status}  {string.Join(" | ", media)}");
                AddUrl(url);
            }
            else if (status.Contains(" 401"))
            {
                _log($"[{host}:{port}] DESCRIBE {path} -> {status} (needs a username/password, e.g. rtsp://admin:PASSWORD@{host}:{port}{path})");
                AddUrl(url);
            }
        }
    }

    async Task UdpDiscoveryAsync(Ipv4Info local, List<IPAddress> hosts, CancellationToken ct)
    {
        _log("Sending UDP discovery packets (PPPP/CS2, XMeye, ONVIF, SSDP)...");
        var multicast = IPAddress.Parse("239.255.255.250");
        byte[] pppp = [0xF1, 0x30, 0x00, 0x00];
        byte[] xmeye = Convert.FromHexString("FF00000000000000000000000000FA0500000000");
        byte[] onvif = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\"><e:Header><w:MessageID>uuid:" + Guid.NewGuid() + "</w:MessageID>" +
            "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
            "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>" +
            "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body></e:Envelope>");
        byte[] ssdp = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");

        var targets = new List<IPAddress> { local.Broadcast };
        targets.AddRange(hosts);

        using var udp = NewUdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());

        async Task Send(byte[] data, IPAddress to, int port)
        {
            try { await udp.SendAsync(data, new IPEndPoint(to, port), ct); }
            catch (SocketException ex) { _log($"UDP send to {to}:{port} failed: {ex.Message}"); }
        }

        foreach (var target in targets)
        {
            await Send(pppp, target, 32108);
            await Send(pppp, target, 32100);
            await Send(xmeye, target, 34569);
        }
        await Send(onvif, multicast, 3702);
        await Send(ssdp, multicast, 1900);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);
        int replies = 0;
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                replies++;
                _log($"UDP reply from {result.RemoteEndPoint}: {Describe(result.Buffer)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }

        if (replies == 0)
            _log("No UDP discovery replies.");
    }

    /// <summary>
    /// Sends small packets to every UDP port and listens for anything that comes back. Cameras on this
    /// kind of chip usually stream JPEG frames over UDP once an app says hello on the right port.
    /// </summary>
    async Task UdpSweepAsync(IPAddress host, CancellationToken ct)
    {
        byte[][] payloads =
        [
            Encoding.ASCII.GetBytes("App send hello"),
            [0xF1, 0x30, 0x00, 0x00],
            [0x00],
        ];
        const int packetsPerSecond = 3000; // gentle enough for a tiny Wi-Fi chip

        _log($"[{host}] UDP sweep: sending {payloads.Length} probe packets to each of 65535 ports (about a minute)...");
        using var udp = NewUdpClient();
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var replyPorts = new ConcurrentDictionary<int, int>();
        var listener = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(listenCts.Token);
                    int count = replyPorts.AddOrUpdate(result.RemoteEndPoint.Port, 1, (_, n) => n + 1);
                    if (count <= 3) // a streaming camera can flood us; show the first few per port
                        _log($"UDP reply from {result.RemoteEndPoint}: {Describe(result.Buffer)}");
                    else if (count == 4)
                        _log($"(more packets from {result.RemoteEndPoint}; it may be streaming)");
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var watch = Stopwatch.StartNew();
        long sent = 0;
        for (int port = 1; port <= 65535; port++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var payload in payloads)
            {
                try
                {
                    udp.Send(payload, payload.Length, new IPEndPoint(host, port));
                }
                catch (SocketException)
                {
                    await Task.Delay(20, ct); // send buffer full; back off
                }
                sent++;
            }
            long aheadMs = sent * 1000 / packetsPerSecond - watch.ElapsedMilliseconds;
            if (aheadMs > 0)
                await Task.Delay((int)aheadMs, ct);
            if (port % 16384 == 0)
                _log($"[{host}] ...UDP port {port}/65535");
        }

        await Task.Delay(3000, ct); // let late replies arrive
        listenCts.Cancel();
        await listener;

        _log(replyPorts.IsEmpty
            ? $"[{host}] No UDP replies on any port."
            : $"[{host}] UDP replies came from port(s): {string.Join(", ", replyPorts.Select(p => $"{p.Key} ({p.Value} packets)"))}");
    }

    void AddUrl(string url)
    {
        lock (_streamUrls)
        {
            if (!_streamUrls.Contains(url))
                _streamUrls.Add(url);
        }
    }

    async Task<byte[]> ExchangeAsync(IPAddress host, int port, string? request, int timeoutMs, int maxBytes, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var client = NewTcpClient();
        var received = new MemoryStream();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();
            if (request != null)
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

            var chunk = new byte[4096];
            while (received.Length < maxBytes)
            {
                int n = await stream.ReadAsync(chunk, cts.Token);
                if (n == 0)
                    break;
                received.Write(chunk, 0, n);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        return received.ToArray();
    }

    static string HttpRequest(IPAddress host, int port, string path) =>
        $"GET {path} HTTP/1.0\r\nHost: {host}:{port}\r\nUser-Agent: WiFiCameraLink\r\nConnection: close\r\n\r\n";

    static string RtspRequest(string method, string url, int cseq) =>
        $"{method} {url} RTSP/1.0\r\nCSeq: {cseq}\r\nUser-Agent: WiFiCameraLink\r\nAccept: application/sdp\r\n\r\n";

    static bool StartsWith(byte[] data, string prefix) =>
        data.Length >= prefix.Length && Encoding.ASCII.GetString(data, 0, prefix.Length) == prefix;

    /// <summary>Shows text as text (truncated) and binary as a hex dump.</summary>
    static string Describe(byte[] data)
    {
        int printable = data.Count(b => b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t');
        if (data.Length > 0 && printable >= data.Length * 0.9)
        {
            string text = Encoding.ASCII.GetString(data).TrimEnd();
            return text.Length > 600 ? text[..600] + " ..." : text;
        }
        int shown = Math.Min(data.Length, 96);
        return $"{data.Length} bytes binary: {BitConverter.ToString(data, 0, shown).Replace('-', ' ')}{(data.Length > shown ? " ..." : "")}";
    }

    static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd('\r')));
}
