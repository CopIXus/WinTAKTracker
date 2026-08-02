using System.Security.Principal;
using WinTAKTracker.Services.Config;

namespace WinTAKTracker.Services.Identity;

/// <summary>Resolved CoT identity for the current session (computer vs logged-on user).</summary>
public sealed class ActiveIdentity
{
    public required string Callsign { get; init; }
    public required string Team { get; init; }
    public required string Role { get; init; }
    public required string CotType { get; init; }
    /// <summary>Optional phone for ATAK Call; empty means omit from CoT contact.</summary>
    public string Phone { get; init; } = "";
    /// <summary>Computer | User | LastUser</summary>
    public required string Source { get; init; }
    public string? UserSid { get; init; }
    public string? UserName { get; init; }
}

public static class IdentityResolver
{
    public static string? CurrentUserSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    public static string? CurrentUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    /// <summary>
    /// Resolve active CoT identity.
    /// Preference: active user with callsign → sticky last user (default after logoff) → computer.
    /// </summary>
    public static ActiveIdentity Resolve(AppConfig config, string? activeUserSid = null)
    {
        config.EnsureIdentityDefaults();

        if (TryResolveUser(config, activeUserSid, source: "User") is { } active)
            return active;

        // Default after logoff / service headless: keep last logged-in user callsign.
        if (!config.RevertToComputerCallsignOnLogoff &&
            TryResolveUser(config, config.LastInteractiveUserSid, source: "LastUser") is { } sticky)
            return sticky;

        var computer = config.ComputerIdentity;
        return new ActiveIdentity
        {
            Callsign = computer.GetEffectiveCallsign(),
            Team = computer.Team,
            Role = computer.Role,
            CotType = computer.CotType,
            Phone = string.IsNullOrWhiteSpace(computer.Phone) ? "" : computer.Phone.Trim(),
            Source = "Computer",
            UserSid = activeUserSid,
            UserName = null,
        };
    }

    private static ActiveIdentity? TryResolveUser(AppConfig config, string? userSid, string source)
    {
        if (string.IsNullOrWhiteSpace(userSid) ||
            !config.UserIdentities.TryGetValue(userSid, out var user) ||
            !user.HasCallsign)
            return null;

        var computer = config.ComputerIdentity;
        return new ActiveIdentity
        {
            Callsign = user.Callsign.Trim(),
            Team = string.IsNullOrWhiteSpace(user.Team) ? computer.Team : user.Team,
            Role = string.IsNullOrWhiteSpace(user.Role) ? computer.Role : user.Role,
            CotType = string.IsNullOrWhiteSpace(user.CotType) ? computer.CotType : user.CotType,
            Phone = string.IsNullOrWhiteSpace(user.Phone) ? "" : user.Phone.Trim(),
            Source = source,
            UserSid = userSid,
            UserName = user.UserName,
        };
    }

    /// <summary>
    /// True when this Windows user has no per-user callsign yet and has not dismissed the setup prompt.
    /// Call on each tray start / interactive login so new users get prompted once.
    /// </summary>
    public static bool CurrentUserNeedsSetup(AppConfig config, string? userSid = null)
    {
        userSid ??= CurrentUserSid();
        if (string.IsNullOrWhiteSpace(userSid)) return false;
        if (!config.UserIdentities.TryGetValue(userSid, out var user))
            return true; // brand-new Windows user — prompt callsign (+ optional phone)
        if (user.HasCallsign) return false;
        return !user.SetupPromptDismissed;
    }
}
