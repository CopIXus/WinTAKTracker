namespace WinTAKTracker.Services.Config;

/// <summary>Outcome of loading config.json — distinguishes first-run, success, and corrupt parse.</summary>
public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }

    /// <summary>True when config.json already existed on disk.</summary>
    public bool FileExisted { get; init; }

    /// <summary>True when the existing file could not be parsed (corrupt backup written).</summary>
    public bool LoadHadError { get; init; }

    /// <summary>Path of the quarantined corrupt file, if any.</summary>
    public string? CorruptBackupPath { get; init; }

    /// <summary>True when no file existed and a fresh default was created (and may be saved).</summary>
    public bool CreatedFresh { get; init; }
}
