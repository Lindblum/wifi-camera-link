using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WiFiCameraLink;

public sealed record Ipv4Info(IPAddress Address, IPAddress Mask, IPAddress? Gateway, IPAddress? DhcpServer)
{
    public uint NetworkValue => NetUtil.ToUInt(Address) & NetUtil.ToUInt(Mask);
    public IPAddress Broadcast => NetUtil.FromUInt(NetworkValue | ~NetUtil.ToUInt(Mask));
    public int PrefixLength => System.Numerics.BitOperations.PopCount(NetUtil.ToUInt(Mask));

    public override string ToString() =>
        $"{Address}/{PrefixLength}, gateway {Gateway?.ToString() ?? "none"}, DHCP server {DhcpServer?.ToString() ?? "none"}";
}

public static class NetUtil
{
    public static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, _, _];

    public static NetworkInterface? FindNic(Guid id) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == id);

    public static Ipv4Info? GetIpv4(Guid id) => FindNic(id) is { } nic ? GetIpv4(nic) : null;

    public static Ipv4Info? GetIpv4(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
            return null;
        var props = nic.GetIPProperties();
        var unicast = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
        if (unicast == null)
            return null;

        var gateway = props.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        var dhcp = props.DhcpServerAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new Ipv4Info(unicast.Address, unicast.IPv4Mask, gateway, dhcp);
    }

    /// <summary>Other adapters that currently have a routable connection (e.g. Ethernet to the home router).</summary>
    public static List<(NetworkInterface Nic, Ipv4Info Info)> OtherOnlineAdapters(Guid exclude)
    {
        var result = new List<(NetworkInterface, Ipv4Info)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (Guid.TryParse(nic.Id, out var id) && id == exclude)
                continue;
            if (GetIpv4(nic) is { Gateway: not null } info && !IsLinkLocal(info.Address))
                result.Add((nic, info));
        }
        return result;
    }

    /// <summary>True when the two subnets overlap, which breaks routing to the camera.</summary>
    public static bool Overlaps(Ipv4Info a, Ipv4Info b)
    {
        uint widerMask = ToUInt(a.Mask) & ToUInt(b.Mask);
        return (ToUInt(a.Address) & widerMask) == (ToUInt(b.Address) & widerMask);
    }
}
