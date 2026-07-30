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
    public string? LastInterfaceDescription { get; private set; }
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

            if (!_settings.Enabled) return;

            try
            {
                var address = IPAddress.Parse(_settings.MulticastAddress);
                _endpoint = new IPEndPoint(address, _settings.MulticastPort);
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.MulticastLoopback = true;
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

                var nic = SelectInterface(_settings.NetworkInterface);
                if (nic is not null)
                {
                    var ipv4 = nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ipv4 is not null)
                    {
                        udp.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface,
                            ipv4.Address.GetAddressBytes());
                        LastInterfaceDescription = nic.Name;
                    }
                }
                else
                {
                    LastInterfaceDescription = "Auto";
                }

                _udp = udp;
                LastErrorCode = null;
                _log.Info("Mesh", $"Mesh SA bound ({_settings.MulticastAddress}:{_settings.MulticastPort}).");
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

    public static IReadOnlyList<(string Id, string Name)> ListInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Select(n => (n.Id, n.Name))
            .ToList();
    }

    private static NetworkInterface? SelectInterface(string preference)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .ToList();

        if (string.IsNullOrWhiteSpace(preference) || preference.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return nics.FirstOrDefault();

        return nics.FirstOrDefault(n =>
                   n.Id.Equals(preference, StringComparison.OrdinalIgnoreCase) ||
                   n.Name.Equals(preference, StringComparison.OrdinalIgnoreCase))
               ?? nics.FirstOrDefault(n => n.Name.Contains(preference, StringComparison.OrdinalIgnoreCase));
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
