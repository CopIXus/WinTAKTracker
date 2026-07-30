namespace WinTAKTracker.Services.Tak;

public enum EnrollmentKind
{
    Unknown,
    OpenTakTrackerEnroll,
    TakEnroll,
    TakPreference,
    TakImportUrl,
    ItakCsv,
}

public sealed class EnrollmentParseResult
{
    public EnrollmentKind Kind { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    /// <summary>Marti certificate enrollment HTTPS port (default 8446).</summary>
    public int EnrollmentPort { get; init; } = 8446;
    public string Protocol { get; init; } = "ssl";
    public string? Username { get; init; }
    public string? Token { get; init; }
    public string? Callsign { get; init; }
    public string? Team { get; init; }
    public string? Role { get; init; }
    public string? DisplayName { get; init; }
    public string? ImportUrl { get; init; }
    public Dictionary<string, string> Preferences { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; init; }
    public bool Success => Error is null && Kind != EnrollmentKind.Unknown;
}

/// <summary>
/// Parses opentaktracker://, tak:// enroll/preference/import, and iTAK CSV.
/// Never logs raw URLs with tokens.
/// </summary>
public static class EnrollmentUriParser
{
    public static EnrollmentParseResult Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Empty input.");

        var text = input.Trim();

        if (text.Contains(',') && !text.Contains("://", StringComparison.Ordinal))
            return ParseItakCsv(text);

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return Fail("Not a valid URI or iTAK CSV.");

        if (uri.Scheme.Equals("opentaktracker", StringComparison.OrdinalIgnoreCase))
            return ParseOpenTak(uri);

        if (uri.Scheme.Equals("tak", StringComparison.OrdinalIgnoreCase))
            return ParseTak(uri);

        return Fail($"Unsupported scheme: {uri.Scheme}");
    }

    private static EnrollmentParseResult ParseOpenTak(Uri uri)
    {
        var q = ParseQuery(uri);
        var hostField = Get(q, "host");
        var (host, streamPort, protocol, enrollPort) = SplitHostField(
            hostField,
            ParseInt(Get(q, "port")),
            Get(q, "protocol"),
            ParseInt(Get(q, "enrollmentPort") ?? Get(q, "enrollPort")));

        return new EnrollmentParseResult
        {
            Kind = EnrollmentKind.OpenTakTrackerEnroll,
            Host = host,
            Username = Get(q, "username"),
            Token = Get(q, "token") ?? Get(q, "password"),
            Callsign = Get(q, "callsign"),
            Team = Get(q, "team"),
            Role = Get(q, "role"),
            Port = streamPort ?? 8089,
            EnrollmentPort = enrollPort ?? 8446,
            Protocol = NormalizeProtocol(protocol ?? "ssl"),
        };
    }

    private static EnrollmentParseResult ParseTak(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        var q = ParseQuery(uri);

        if (path.EndsWith("enroll", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("enroll", StringComparison.OrdinalIgnoreCase))
        {
            var hostField = Get(q, "host");
            var (host, streamPort, protocol, enrollPort) = SplitHostField(
                hostField,
                ParseInt(Get(q, "port")),
                Get(q, "protocol"),
                ParseInt(Get(q, "enrollmentPort") ?? Get(q, "enrollPort")));

            return new EnrollmentParseResult
            {
                Kind = EnrollmentKind.TakEnroll,
                Host = host,
                Username = Get(q, "username"),
                Token = Get(q, "token") ?? Get(q, "password"),
                Callsign = Get(q, "callsign"),
                Team = Get(q, "team"),
                Role = Get(q, "role"),
                Port = streamPort ?? 8089,
                EnrollmentPort = enrollPort ?? 8446,
                Protocol = NormalizeProtocol(protocol ?? "ssl"),
            };
        }

        if (path.EndsWith("preference", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("preference", StringComparison.OrdinalIgnoreCase))
        {
            return new EnrollmentParseResult
            {
                Kind = EnrollmentKind.TakPreference,
                Callsign = Get(q, "locationCallsign") ?? Get(q, "callsign"),
                Team = Get(q, "locationTeam") ?? Get(q, "team"),
                Role = Get(q, "locationRole") ?? Get(q, "role"),
                Preferences = q,
            };
        }

        if (path.EndsWith("import", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("import", StringComparison.OrdinalIgnoreCase))
        {
            return new EnrollmentParseResult
            {
                Kind = EnrollmentKind.TakImportUrl,
                ImportUrl = Get(q, "url"),
            };
        }

        return Fail("Unrecognized tak:// path.");
    }

    private static EnrollmentParseResult ParseItakCsv(string text)
    {
        var parts = text.Split(',');
        if (parts.Length < 4)
            return Fail("iTAK CSV requires Name,host,port,protocol.");

        return new EnrollmentParseResult
        {
            Kind = EnrollmentKind.ItakCsv,
            DisplayName = parts[0].Trim(),
            Host = parts[1].Trim(),
            Port = ParseInt(parts[2].Trim()) ?? 8089,
            Protocol = NormalizeProtocol(parts[3].Trim()),
        };
    }

    /// <summary>
    /// Accepts host, host:port, or host:port:ssl|tcp (ATAK connect-string style).
    /// Port 8446 in the host field is treated as the enrollment port, not CoT streaming.
    /// </summary>
    internal static (string? Host, int? StreamPort, string? Protocol, int? EnrollmentPort) SplitHostField(
        string? hostField,
        int? explicitPort,
        string? explicitProtocol,
        int? explicitEnrollmentPort)
    {
        if (string.IsNullOrWhiteSpace(hostField))
            return (null, explicitPort, explicitProtocol, explicitEnrollmentPort);

        var raw = hostField.Trim();
        int? streamPort = explicitPort;
        int? enrollPort = explicitEnrollmentPort;
        string? protocol = explicitProtocol;

        // host:port:protocol
        var lastColon = raw.LastIndexOf(':');
        if (lastColon > 0)
        {
            var maybeProto = raw[(lastColon + 1)..];
            if (maybeProto.Equals("ssl", StringComparison.OrdinalIgnoreCase) ||
                maybeProto.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                maybeProto.Equals("tls", StringComparison.OrdinalIgnoreCase) ||
                maybeProto.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                maybeProto.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                protocol ??= maybeProto;
                raw = raw[..lastColon];
                lastColon = raw.LastIndexOf(':');
                if (lastColon > 0 && int.TryParse(raw[(lastColon + 1)..], out var connectPort))
                {
                    ApplyPort(connectPort, ref streamPort, ref enrollPort);
                    raw = raw[..lastColon];
                }

                return (raw, streamPort ?? 8089, protocol, enrollPort);
            }
        }

        // host:port
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var portOnly))
        {
            ApplyPort(portOnly, ref streamPort, ref enrollPort);
            raw = raw[..colon];
        }

        return (raw, streamPort, protocol, enrollPort);

        static void ApplyPort(int port, ref int? stream, ref int? enroll)
        {
            if (port == 8446)
                enroll ??= port;
            else if (stream is null)
                stream = port;
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = uri.Query;
        if (string.IsNullOrEmpty(q)) return dict;
        if (q.StartsWith('?')) q = q[1..];

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                dict[Uri.UnescapeDataString(pair)] = "";
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
            dict[key] = value;
        }

        return dict;
    }

    private static string NormalizeProtocol(string proto)
    {
        proto = proto.Trim().ToLowerInvariant();
        return proto switch
        {
            "https" or "ssl" or "tls" => "ssl",
            "http" or "tcp" => "tcp",
            _ => "ssl",
        };
    }

    private static string? Get(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, out var n) ? n : null;

    private static EnrollmentParseResult Fail(string error) =>
        new() { Kind = EnrollmentKind.Unknown, Error = error };
}
