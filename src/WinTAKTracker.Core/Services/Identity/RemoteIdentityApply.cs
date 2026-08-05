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
        "Cyan", "Blue", "Dark Blue", "Brown", "Green", "Dark Green",
        "Yellow", "Orange", "Red", "Purple", "Magenta", "Maroon", "Teal", "White",
    ];

    /// <summary>ATAK role allow-list — unknown roles are dropped, not written to CoT.</summary>
    public static readonly string[] KnownRoles =
    [
        "Team Member", "Team Lead", "HQ", "Sniper", "Medic", "Forward Observer", "RTO", "K9",
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

    /// <summary>Normalize ATAK role against the allow-list (case-insensitive); unknown roles → null.</summary>
    public static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var r = role.Trim();
        foreach (var known in KnownRoles)
        {
            if (known.Equals(r, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return null;
    }

    /// <summary>
    /// Write callsign/team/role into the identity currently on the wire: the active user session
    /// when one is bound, otherwise the sticky last interactive user (default after logoff),
    /// otherwise the computer identity. Without the sticky fallback a headless Portal push would
    /// update <c>ComputerIdentity</c> while CoT kept broadcasting the last user's callsign — the
    /// push looked like a no-op on maps and Portal.
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
        var normalizedRole = NormalizeRole(role);
        if (normalizedCallsign is null && normalizedTeam is null && normalizedRole is null)
        {
            return new Result
            {
                Applied = false,
                Message = "No valid callsign, team, or role in remote configuration.",
            };
        }

        // Match IdentityResolver.Resolve order: active user → sticky last user → computer.
        var targetSid = activeUserSid;
        if (string.IsNullOrWhiteSpace(targetSid) &&
            !config.RevertToComputerCallsignOnLogoff &&
            !string.IsNullOrWhiteSpace(config.LastInteractiveUserSid) &&
            UserAlreadyHasCallsign(config, config.LastInteractiveUserSid!))
        {
            targetSid = config.LastInteractiveUserSid;
        }

        var useUser = !string.IsNullOrWhiteSpace(targetSid) &&
                      (hasCallsign || UserAlreadyHasCallsign(config, targetSid!));

        if (useUser)
        {
            if (!config.UserIdentities.TryGetValue(targetSid!, out var user))
            {
                user = new UserIdentitySettings();
                config.UserIdentities[targetSid!] = user;
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

            // Only re-open the callsign setup prompt when a callsign actually arrived — a
            // team/role-only push must not nag a user who previously skipped setup.
            if (normalizedCallsign is not null)
                user.SetupPromptDismissed = false;
            if (user.HasCallsign)
                config.LastInteractiveUserSid = targetSid;

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
