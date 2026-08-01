namespace WinTAKTracker.Services.Config;

/// <summary>
/// Detects whether a config replace requires bouncing TAK / GPS / mesh connections.
/// Timestamp / log-level-only edits must not trigger ReloadConnections.
/// </summary>
public static class ConfigReconnectComparer
{
    public static bool RequiresConnectionReload(AppConfig? before, AppConfig after)
    {
        if (before is null) return true;

        if (!GpsEqual(before.Gps, after.Gps)) return true;
        if (!MeshEqual(before.MeshSa, after.MeshSa)) return true;
        if (!ServersEqual(before.Servers, after.Servers)) return true;

        // Soft-accept affects the next TLS handshake; bounce so live clients pick up the flag.
        if (before.Diagnostics.AllowInsecureTlsSoftAccept != after.Diagnostics.AllowInsecureTlsSoftAccept)
            return true;

        // Reporting intervals alone do not bounce sockets; ReportingEngine.ApplyConfig handles that.
        // Identity / other diagnostics / updates / startup do not require reconnect.
        return false;
    }

    private static bool GpsEqual(GpsSettings a, GpsSettings b) =>
        string.Equals(a.SourcePriority, b.SourcePriority, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ComPort ?? "", b.ComPort ?? "", StringComparison.OrdinalIgnoreCase)
        && a.BaudRate == b.BaudRate
        && a.LastFixHoldSeconds == b.LastFixHoldSeconds
        && a.EnableNetworkFallback == b.EnableNetworkFallback;

    private static bool MeshEqual(MeshSaSettings a, MeshSaSettings b) =>
        a.Enabled == b.Enabled
        && string.Equals(a.Mode, b.Mode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.MulticastAddress, b.MulticastAddress, StringComparison.OrdinalIgnoreCase)
        && a.MulticastPort == b.MulticastPort
        && string.Equals(a.NetworkInterface, b.NetworkInterface, StringComparison.OrdinalIgnoreCase);

    private static bool ServersEqual(List<ServerProfile> a, List<ServerProfile> b)
    {
        if (a.Count != b.Count) return false;
        var byId = b.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var left in a)
        {
            if (!byId.TryGetValue(left.Id, out var right)) return false;
            if (!ServerEqual(left, right)) return false;
        }

        return true;
    }

    private static bool ServerEqual(ServerProfile a, ServerProfile b) =>
        a.Enabled == b.Enabled
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Protocol, b.Protocol, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Username ?? "", b.Username ?? "", StringComparison.Ordinal)
        && string.Equals(a.SecretBlobName ?? "", b.SecretBlobName ?? "", StringComparison.Ordinal)
        && string.Equals(a.CertPasswordBlobName ?? "", b.CertPasswordBlobName ?? "", StringComparison.Ordinal)
        && string.Equals(a.TrustPasswordBlobName ?? "", b.TrustPasswordBlobName ?? "", StringComparison.Ordinal)
        && string.Equals(a.ClientCertFileName ?? "", b.ClientCertFileName ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.TrustStoreFileName ?? "", b.TrustStoreFileName ?? "", StringComparison.OrdinalIgnoreCase)
        && a.AllowInsecureTlsSoftAccept == b.AllowInsecureTlsSoftAccept
        && string.Equals(a.CallsignOverride ?? "", b.CallsignOverride ?? "", StringComparison.Ordinal)
        && string.Equals(a.TeamOverride ?? "", b.TeamOverride ?? "", StringComparison.Ordinal)
        && string.Equals(a.RoleOverride ?? "", b.RoleOverride ?? "", StringComparison.Ordinal);
}
