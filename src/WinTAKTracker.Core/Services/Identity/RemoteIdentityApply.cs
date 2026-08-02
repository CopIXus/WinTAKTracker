using WinTAKTracker.Services.Config;

namespace WinTAKTracker.Services.Identity;

/// <summary>
/// Applies Portal / enrollment remote identity (callsign + team color name).
/// Callsigns for WinTAKTracker get a <c>.wtt</c> suffix so they are distinct on the TAK network.
/// </summary>
public static class RemoteIdentityApply
{
    public const string CallsignSuffix = ".wtt";

    public static readonly string[] KnownTeams =
    [
        "Cyan", "Blue", "Green", "Yellow", "Orange", "Red",
        "Purple", "Magenta", "Maroon", "Teal", "White",
    ];

    public sealed class Result
    {
        public bool Applied { get; init; }
        public string? Callsign { get; init; }
        public string? Team { get; init; }
        public string? Role { get; init; }
        public string Target { get; init; } = "";
        public string Message { get; init; } = "";
    }

    /// <summary>Append <c>.wtt</c> when missing (idempotent; strips trailing dots).</summary>
    public static string EnsureWttSuffix(string callsign)
    {
        var trimmed = callsign.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.EndsWith(CallsignSuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed[..^CallsignSuffix.Length].TrimEnd() + CallsignSuffix;
        return trimmed + CallsignSuffix;
    }

    /// <summary>
    /// Normalize ATAK team color name (title-case known colors; otherwise trim as-is).
    /// </summary>
    public static string? NormalizeTeam(string? team)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;
        var t = team.Trim();
        foreach (var known in KnownTeams)
        {
            if (known.Equals(t, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return t;
    }

    /// <summary>
    /// Write callsign/team/role into the active user identity when a session SID is provided
    /// and that user already has (or is receiving) a callsign; otherwise computer identity.
    /// </summary>
    public static Result Apply(
        AppConfig config,
        string? callsign,
        string? team,
        string? role,
        string? activeUserSid = null,
        string? activeUserName = null)
    {
        var hasCallsign = !string.IsNullOrWhiteSpace(callsign);
        var hasTeam = !string.IsNullOrWhiteSpace(team);
        var hasRole = !string.IsNullOrWhiteSpace(role);
        if (!hasCallsign && !hasTeam && !hasRole)
        {
            return new Result
            {
                Applied = false,
                Message = "No callsign, team, or role in remote configuration.",
            };
        }

        var normalizedCallsign = hasCallsign ? EnsureWttSuffix(callsign!) : null;
        var normalizedTeam = NormalizeTeam(team);
        var normalizedRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim();

        var useUser = !string.IsNullOrWhiteSpace(activeUserSid) &&
                      (hasCallsign || UserAlreadyHasCallsign(config, activeUserSid!));

        if (useUser)
        {
            if (!config.UserIdentities.TryGetValue(activeUserSid!, out var user))
            {
                user = new UserIdentitySettings();
                config.UserIdentities[activeUserSid!] = user;
            }

            var unchanged =
                (normalizedCallsign is null ||
                 string.Equals(user.Callsign, normalizedCallsign, StringComparison.OrdinalIgnoreCase)) &&
                (normalizedTeam is null ||
                 string.Equals(user.Team, normalizedTeam, StringComparison.OrdinalIgnoreCase)) &&
                (normalizedRole is null ||
                 string.Equals(user.Role, normalizedRole, StringComparison.OrdinalIgnoreCase));

            if (unchanged && user.HasCallsign)
            {
                return new Result
                {
                    Applied = false,
                    Callsign = user.Callsign,
                    Team = user.Team,
                    Role = user.Role,
                    Target = "user",
                    Message = "Remote identity unchanged; skip apply.",
                };
            }

            if (!string.IsNullOrWhiteSpace(activeUserName))
                user.UserName = activeUserName!;
            if (normalizedCallsign is not null)
                user.Callsign = normalizedCallsign;
            if (normalizedTeam is not null)
                user.Team = normalizedTeam;
            if (normalizedRole is not null)
                user.Role = normalizedRole;
            user.SetupPromptDismissed = false;
            if (user.HasCallsign)
                config.LastInteractiveUserSid = activeUserSid;

            config.EnsureIdentityDefaults();
            return new Result
            {
                Applied = true,
                Callsign = user.Callsign,
                Team = user.Team,
                Role = user.Role,
                Target = "user",
                Message = "Remote identity applied to per-user callsign.",
            };
        }

        var computerUnchanged =
            (normalizedCallsign is null ||
             string.Equals(config.ComputerIdentity.Callsign, normalizedCallsign, StringComparison.OrdinalIgnoreCase)) &&
            (normalizedTeam is null ||
             string.Equals(config.ComputerIdentity.Team, normalizedTeam, StringComparison.OrdinalIgnoreCase)) &&
            (normalizedRole is null ||
             string.Equals(config.ComputerIdentity.Role, normalizedRole, StringComparison.OrdinalIgnoreCase));

        if (computerUnchanged)
        {
            return new Result
            {
                Applied = false,
                Callsign = config.ComputerIdentity.Callsign,
                Team = config.ComputerIdentity.Team,
                Role = config.ComputerIdentity.Role,
                Target = "computer",
                Message = "Remote identity unchanged; skip apply.",
            };
        }

        if (normalizedCallsign is not null)
            config.ComputerIdentity.Callsign = normalizedCallsign;
        if (normalizedTeam is not null)
            config.ComputerIdentity.Team = normalizedTeam;
        if (normalizedRole is not null)
            config.ComputerIdentity.Role = normalizedRole;
        config.EnsureIdentityDefaults();

        return new Result
        {
            Applied = true,
            Callsign = config.ComputerIdentity.Callsign,
            Team = config.ComputerIdentity.Team,
            Role = config.ComputerIdentity.Role,
            Target = "computer",
            Message = "Remote identity applied to computer callsign.",
        };
    }

    private static bool UserAlreadyHasCallsign(AppConfig config, string sid) =>
        config.UserIdentities.TryGetValue(sid, out var u) && u.HasCallsign;
}
