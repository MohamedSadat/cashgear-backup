namespace CashGear.Backup.UI.Components.Services;

/// <summary>The native folder picker (the desktop app shows the Windows dialog).</summary>
public interface IFolderPicker
{
    /// <summary>The chosen folder, or null when the user canceled.</summary>
    Task<string?> PickFolderAsync(string title, string? initialFolder, CancellationToken ct);
}

/// <summary>What the UI needs from the desktop around it.</summary>
public interface IDesktopShell
{
    /// <summary>Where restores go by default, one new folder per restore (for example %USERPROFILE%\Restored).</summary>
    string DefaultRestoreParent { get; }

    /// <summary>Opens a folder in Explorer.</summary>
    void OpenFolder(string path);

    /// <summary>Asks where to save a file (the Windows save dialog) and writes it; null when the user canceled.</summary>
    Task<string?> SaveFileAsync(string suggestedName, byte[] content, CancellationToken ct);

    /// <summary>This app's version.</summary>
    string AppVersion { get; }

    /// <summary>The open-source notices installed with the app (THIRD-PARTY-NOTICES.txt); empty when missing.</summary>
    Task<string> ReadThirdPartyNoticesAsync(CancellationToken ct);
}

/// <summary>Timing knobs; tests turn the background refresh off.</summary>
public sealed class UiOptions
{
    /// <summary>How often the dashboard asks for jobs (to notice scheduled runs). Infinite turns it off.</summary>
    public TimeSpan DashboardRefresh { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Runs listed on the history page.</summary>
    public int HistoryLimit { get; init; } = 100;

    /// <summary>Runs fetched per job on the dashboard (to find the last successful one).</summary>
    public int DashboardRunLimit { get; init; } = 50;
}
