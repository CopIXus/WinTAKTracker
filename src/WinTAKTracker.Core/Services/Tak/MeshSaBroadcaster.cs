using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Tak;

/// <summary>ATAK-compatible Mesh SA UDP multicast sender (239.2.3.1:6969).</summary>
public sealed class MeshSaBroadcaster : IDisposable
{
    private readonly IRedactedLogger _log;
    private readonly object _gate = new();
    private UdpClient? _udp;
    private MeshSaSettings _settings = new();
    private IPEndPoint? _endpoint;

    public DateTimeOffset? LastSendUtc { get; private set; }
    /// <summary>Human-readable adapter used for send, e.g. "Wi-Fi (192.168.1.10)".</summary>
    public string? LastInterfaceDescription { get; private set; }
    /// <summary>Non-null when Auto fell back to a VPN/tunnel NIC or no usable LAN NIC exists.</summary>
    public string? LastInterfaceWarning { get; private set; }
    public string? LastErrorCode { get; private set; }
    public bool IsReady
    {
        get { lock (_gate) return _udp is not null; }
    }

    public MeshSaBroadcaster(IRedactedLogger log) => _log = log;

    public void ApplySettings(MeshSaSettings settings)
    {
        _settings = settings;
        Rebind();
    }

    public void Rebind()
    {
        lock (_gate)
        {
            try { _udp?.Dispose(); } catch { /* ignore */ }
            _udp = null;
            _endpoint = null;
            LastInterfaceDescription = null;
            LastInterfaceWarning = null;

            if (!_settings.Enabled) return;

            try
            {
                var address = IPAddress.Parse(_settings.MulticastAddress);
                _endpoint = new IPEndPoint(address, _settings.MulticastPort);
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.MulticastLoopback = true;

                var selection = SelectInterface(_settings.NetworkInterface);
                if (selection.Nic is null || selection.Ipv4 is null || selection.Index is null)
                {
                    LastErrorCode = "NoUsableInterface";
                    LastInterfaceDescription = "none";
                    LastInterfaceWarning = "No usable IPv4 adapter for Mesh SA multicast.";
                    _log.Warn("Mesh", "Mesh bind failed: no usable IPv4 interface.");
                    try { udp.Dispose(); } catch { /* ignore */ }
                    return;
                }

                var nic = selection.Nic;
                var ipv4 = selection.Ipv4;
                var ifIndex = selection.Index.Value;
                var isTunnel = IsTunnelOrVpn(nic);

                // ATAK Mesh SA uses TTL 1 on LAN so packets stay on-segment.
                // Raise TTL when the user explicitly picks a VPN/tunnel NIC.
                var ttl = isTunnel ? 64 : 1;
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);

                // Windows IP_MULTICAST_IF: IPv4 interface index in network byte order.
                var ifBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(ifIndex));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ifBytes);

                LastInterfaceDescription = $"{nic.Name} ({ipv4})";
                if (selection.AutoMode && isTunnel)
                {
                    LastInterfaceWarning =
                        "Only a VPN/tunnel adapter is available; ATAK on the LAN may not see Mesh SA. Pick Wi‑Fi/Ethernet explicitly if present.";
                }
                else if (!selection.AutoMode && isTunnel)
                {
                    LastInterfaceWarning =
                        "Sending on a VPN/tunnel adapter; LAN ATAK peers typically will not receive Mesh SA.";
                }

                _udp = udp;
                LastErrorCode = null;
                _log.Info("Mesh",
                    $"Mesh SA bound ({_settings.MulticastAddress}:{_settings.MulticastPort}) via {LastInterfaceDescription}, ifIndex={ifIndex}, ttl={ttl}.");
                if (LastInterfaceWarning is not null)
                    _log.Warn("Mesh", LastInterfaceWarning);
            }
            catch (Exception ex)
            {
                LastErrorCode = ex.GetType().Name;
                _log.Warn("Mesh", $"Mesh bind failed: {ex.GetType().Name}");
            }
        }
    }

    public bool TrySend(string cotXml)
    {
        byte[] bytes;
        UdpClient? udp;
        IPEndPoint? ep;
        lock (_gate)
        {
            udp = _udp;
            ep = _endpoint;
        }

        if (udp is null || ep is null || !_settings.Enabled)
            return false;

        try
        {
            bytes = Encoding.UTF8.GetBytes(cotXml);
            udp.Send(bytes, bytes.Length, ep);
            LastSendUtc = DateTimeOffset.UtcNow;
            LastErrorCode = null;
            return true;
        }
        catch (Exception ex)
        {
            LastErrorCode = ex.GetType().Name;
            _log.Warn("Mesh", $"Mesh send failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Interfaces for the settings picker: LAN first, then others. Display uses Name (config key).</summary>
    public static IReadOnlyList<(string Id, string Name)> ListInterfaces()
    {
        return EnumerateCandidateInterfaces()
            .OrderBy(n => ScoreForAuto(n)) // lower = better LAN
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => (n.Id, n.Name))
            .ToList();
    }

    private readonly record struct InterfaceSelection(
        NetworkInterface? Nic,
        IPAddress? Ipv4,
        int? Index,
        bool AutoMode);

    private static InterfaceSelection SelectInterface(string preference)
    {
        var nics = EnumerateCandidateInterfaces().ToList();
        var auto = string.IsNullOrWhiteSpace(preference) ||
                   preference.Equals("Auto", StringComparison.OrdinalIgnoreCase);

        NetworkInterface? nic;
        if (auto)
        {
            // Prefer real LAN (Ethernet/Wi‑Fi); never auto-pick Tailscale/Wintun/VPN if a LAN NIC exists.
            nic = nics
                .OrderBy(ScoreForAuto)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        else
        {
            nic = nics.FirstOrDefault(n =>
                       n.Id.Equals(preference, StringComparison.OrdinalIgnoreCase) ||
                       n.Name.Equals(preference, StringComparison.OrdinalIgnoreCase))
                   ?? nics.FirstOrDefault(n => n.Name.Contains(preference, StringComparison.OrdinalIgnoreCase));
        }

        if (nic is null)
            return new InterfaceSelection(null, null, null, auto);

        var ipv4 = GetPrimaryIpv4(nic);
        var index = TryGetIpv4Index(nic);
        if (ipv4 is null || index is null)
            return new InterfaceSelection(null, null, null, auto);

        return new InterfaceSelection(nic, ipv4, index, auto);
    }

    private static IEnumerable<NetworkInterface> EnumerateCandidateInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
                        GetPrimaryIpv4(n) is not null &&
                        TryGetIpv4Index(n) is not null);

    private static IPAddress? GetPrimaryIpv4(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a) &&
                !IsLinkLocal(a));

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    private static int? TryGetIpv4Index(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lower score = preferred for Auto. LAN Ethernet/Wi‑Fi win; tunnels/VPNs lose.</summary>
    private static int ScoreForAuto(NetworkInterface nic)
    {
        if (IsTunnelOrVpn(nic))
            return 1000;

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.Ethernet3Megabit => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            NetworkInterfaceType.Wman or
            NetworkInterfaceType.Wwanpp or
            NetworkInterfaceType.Wwanpp2 => 50,
            _ => 100,
        };
    }

    internal static bool IsTunnelOrVpn(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true;

        var haystack = $"{nic.Name} {nic.Description}";
        ReadOnlySpan<string> markers =
        [
            "tailscale",
            "wintun",
            "wireguard",
            "tun",
            "tap",
            "vpn",
            "nordlynx",
            "openvpn",
            "zerotier",
            "hamachi",
            "mullvad",
            "cloudflare warp",
            "warp",
            "fortinet",
            "forticlient",
            "globalprotect",
            "cisco anyconnect",
            "pulse secure",
            "sonicwall",
            "hyper-v",
            "vethernet",
            "docker",
            "wsl",
            "virtualbox",
            "vmware",
            "pseudo-interface",
        ];

        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _udp?.Dispose();
            _udp = null;
        }
    }
}
