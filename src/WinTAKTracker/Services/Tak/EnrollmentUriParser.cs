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
        return new EnrollmentParseResult
        {
            Kind = EnrollmentKind.OpenTakTrackerEnroll,
            Host = Get(q, "host"),
            Username = Get(q, "username"),
            Token = Get(q, "token") ?? Get(q, "password"),
            Callsign = Get(q, "callsign"),
            Team = Get(q, "team"),
            Role = Get(q, "role"),
            Port = ParseInt(Get(q, "port")) ?? 8089,
            Protocol = NormalizeProtocol(Get(q, "protocol") ?? "ssl"),
        };
    }

    private static EnrollmentParseResult ParseTak(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        var q = ParseQuery(uri);

        if (path.EndsWith("enroll", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("enroll", StringComparison.OrdinalIgnoreCase))
        {
            return new EnrollmentParseResult
            {
                Kind = EnrollmentKind.TakEnroll,
                Host = Get(q, "host"),
                Username = Get(q, "username"),
                Token = Get(q, "token") ?? Get(q, "password"),
                Callsign = Get(q, "callsign"),
                Team = Get(q, "team"),
                Role = Get(q, "role"),
                Port = ParseInt(Get(q, "port")) ?? 8089,
                Protocol = NormalizeProtocol(Get(q, "protocol") ?? "ssl"),
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
