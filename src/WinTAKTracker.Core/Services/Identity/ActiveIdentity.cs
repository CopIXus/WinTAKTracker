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
    /// <summary>Computer | User</summary>
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
    /// Resolve active CoT identity. When <paramref name="activeUserSid"/> is null/empty
    /// (logged off / Session 0 with no interactive user), use computer identity.
    /// </summary>
    public static ActiveIdentity Resolve(AppConfig config, string? activeUserSid = null)
    {
        config.EnsureIdentityDefaults();

        var computer = config.ComputerIdentity;
        var computerCallsign = computer.GetEffectiveCallsign();

        if (!string.IsNullOrWhiteSpace(activeUserSid) &&
            config.UserIdentities.TryGetValue(activeUserSid, out var user) &&
            user.HasCallsign)
        {
            return new ActiveIdentity
            {
                Callsign = user.Callsign.Trim(),
                Team = string.IsNullOrWhiteSpace(user.Team) ? computer.Team : user.Team,
                Role = string.IsNullOrWhiteSpace(user.Role) ? computer.Role : user.Role,
                CotType = string.IsNullOrWhiteSpace(user.CotType) ? computer.CotType : user.CotType,
                Source = "User",
                UserSid = activeUserSid,
                UserName = user.UserName,
            };
        }

        return new ActiveIdentity
        {
            Callsign = computerCallsign,
            Team = computer.Team,
            Role = computer.Role,
            CotType = computer.CotType,
            Source = "Computer",
            UserSid = activeUserSid,
            UserName = null,
        };
    }

    public static bool CurrentUserNeedsSetup(AppConfig config, string? userSid = null)
    {
        userSid ??= CurrentUserSid();
        if (string.IsNullOrWhiteSpace(userSid)) return false;
        if (!config.UserIdentities.TryGetValue(userSid, out var user))
            return true;
        return !user.HasCallsign && !user.SetupPromptDismissed;
    }
}
