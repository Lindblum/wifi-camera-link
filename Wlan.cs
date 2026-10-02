using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace WiFiCameraLink;

public sealed record WifiInterface(Guid Id, string Description, bool Connected)
{
    public override string ToString() => Description;
}

public sealed record WifiNetwork(string Ssid, int Signal, bool Secured, int Auth, int Cipher, bool Connected)
{
    public override string ToString() =>
        $"{Ssid}  ({Signal}%, {(Secured ? "secured" : "open")}{(Connected ? ", connected" : "")})";
}

/// <summary>Thin wrapper over the Windows Native Wifi API (wlanapi.dll).</summary>
public sealed class WlanClient : IDisposable
{
    const int InterfaceInfoSize = 532;      // WLAN_INTERFACE_INFO
    const int AvailableNetworkSize = 628;   // WLAN_AVAILABLE_NETWORK
    const int ListHeaderSize = 8;           // dwNumberOfItems + dwIndex
    const int FlagConnected = 1;            // WLAN_AVAILABLE_NETWORK_CONNECTED
    const int OpcodeCurrentConnection = 7;  // wlan_intf_opcode_current_connection
    const int StateConnected = 1;           // wlan_interface_state_connected

    IntPtr _handle;

    public WlanClient()
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out _handle), nameof(WlanOpenHandle));
    }

    public List<WifiInterface> GetInterfaces()
    {
        Check(WlanEnumInterfaces(_handle, IntPtr.Zero, out var list), nameof(WlanEnumInterfaces));
        try
        {
            int count = Marshal.ReadInt32(list);
            var result = new List<WifiInterface>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + ListHeaderSize + i * InterfaceInfoSize;
                var id = Marshal.PtrToStructure<Guid>(item);
                string description = ReadWString(item + 16, 256);
                int state = Marshal.ReadInt32(item + 528);
                result.Add(new WifiInterface(id, description, state == StateConnected));
            }
            return result;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Asks the adapter to rescan. Results arrive a few seconds later.</summary>
    public void Scan(Guid iface) =>
        Check(WlanScan(_handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), nameof(WlanScan));

    public List<WifiNetwork> GetNetworks(Guid iface)
    {
        Check(WlanGetAvailableNetworkList(_handle, ref iface, 0, IntPtr.Zero, out var list), nameof(WlanGetAvailableNetworkList));
        try
        {
            int count = Marshal.ReadInt32(list);
            var bySsid = new Dictionary<string, WifiNetwork>();
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + ListHeaderSize + i * AvailableNetworkSize;
                int ssidLength = Math.Min(Marshal.ReadInt32(item + 512), 32);
                if (ssidLength == 0)
                    continue; // hidden network

                var ssidBytes = new byte[ssidLength];
                Marshal.Copy(item + 516, ssidBytes, 0, ssidLength);
                var network = new WifiNetwork(
                    Ssid: Encoding.UTF8.GetString(ssidBytes),
                    Signal: Marshal.ReadInt32(item + 604),
                    Secured: Marshal.ReadInt32(item + 608) != 0,
                    Auth: Marshal.ReadInt32(item + 612),
                    Cipher: Marshal.ReadInt32(item + 616),
                    Connected: (Marshal.ReadInt32(item + 620) & FlagConnected) != 0);

                // The same SSID shows up once per saved profile, so merge duplicates.
                bySsid[network.Ssid] = bySsid.TryGetValue(network.Ssid, out var existing)
                    ? existing with
                    {
                        Signal = Math.Max(existing.Signal, network.Signal),
                        Connected = existing.Connected || network.Connected,
                    }
                    : network;
            }
            return bySsid.Values.OrderByDescending(n => n.Signal).ToList();
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>Name of the profile the adapter is connected with, or null if not connected.</summary>
    public string? GetCurrentProfile(Guid iface)
    {
        int error = WlanQueryInterface(_handle, ref iface, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _);
        if (error != 0)
            return null;
        try
        {
            return Marshal.ReadInt32(data) == StateConnected ? ReadWString(data + 8, 256) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>
    /// Saves a manual-connect profile for the network (so Windows never auto-joins the camera)
    /// and connects to it.
    /// </summary>
    public void Connect(Guid iface, string ssid, string password, WifiNetwork? scanned)
    {
        string xml = BuildProfileXml(ssid, password, scanned);
        int error = WlanSetProfile(_handle, ref iface, 0, xml, null, true, IntPtr.Zero, out uint reason);
        if (error != 0)
            throw new InvalidOperationException($"Could not save the Wi-Fi profile (error {error}, reason code {reason}). Check the password.");
        ConnectProfile(iface, ssid);
    }

    public void ConnectProfile(Guid iface, string profile)
    {
        var parameters = new WLAN_CONNECTION_PARAMETERS
        {
            wlanConnectionMode = 0, // wlan_connection_mode_profile
            strProfile = profile,
            dot11BssType = 1,       // dot11_BSS_type_infrastructure
        };
        Check(WlanConnect(_handle, ref iface, ref parameters, IntPtr.Zero), nameof(WlanConnect));
    }

    public void Disconnect(Guid iface) =>
        Check(WlanDisconnect(_handle, ref iface, IntPtr.Zero), nameof(WlanDisconnect));

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
    }

    static string BuildProfileXml(string ssid, string password, WifiNetwork? scanned)
    {
        bool open = password.Length == 0 && scanned is not { Secured: true };
        string auth = scanned?.Auth switch
        {
            4 => "WPAPSK",   // DOT11_AUTH_ALGO_WPA_PSK
            9 => "WPA3SAE",  // DOT11_AUTH_ALGO_WPA3_SAE
            _ => "WPA2PSK",
        };
        string encryption = scanned?.Cipher == 2 ? "TKIP" : "AES";
        string name = SecurityElement.Escape(ssid);
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));

        string security = open
            ? "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>"
            : $"<authEncryption><authentication>{auth}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX></authEncryption>" +
              $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey>";

        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM><security>{security}</security></MSM>
            </WLANProfile>
            """;
    }

    static string ReadWString(IntPtr ptr, int maxChars)
    {
        string s = Marshal.PtrToStringUni(ptr, maxChars);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    static void Check(int error, string call)
    {
        if (error == 0)
            return;
        string hint = error == 5
            ? " Windows blocked Wi-Fi access: turn on Location services (Settings > Privacy & security > Location) and allow desktop apps to use it."
            : "";
        throw new InvalidOperationException($"{call} failed with error {error}.{hint}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;
        public uint dwFlags;
    }

    [DllImport("wlanapi.dll")]
    static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ieData, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr networkList);

    [DllImport("wlanapi.dll")]
    static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out uint dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    static extern int WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string profileXml, string? security, bool overwrite, IntPtr reserved, out uint reasonCode);

    [DllImport("wlanapi.dll")]
    static extern int WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS parameters, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    static extern void WlanFreeMemory(IntPtr memory);
}
