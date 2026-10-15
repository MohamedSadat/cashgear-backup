using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.UI.Components.Model;

/// <summary>How the licence and update status read in the UI.</summary>
public static class LicenceText
{
    public static string Edition(LicenceStatus licence)
    {
        ArgumentNullException.ThrowIfNull(licence);
        return licence.Edition switch
        {
            Contracts.V1.Edition.Pro => "Pro",
            Contracts.V1.Edition.Business => "Business",
            _ => "Free edition",
        };
    }

    public static string State(LicenceStatus licence)
    {
        ArgumentNullException.ThrowIfNull(licence);
        var key = string.IsNullOrEmpty(licence.KeyHint) ? "" : $" (key ending {licence.KeyHint})";
        return licence.State switch
        {
            LicenceState.Active => "active" + key + (licence.ExpiresAt is { } ends ? $", until {ends.ToDateTimeOffset():d MMM yyyy}" : ""),
            LicenceState.OfflineGrace => "active, but the licence server has not been reached lately" + key,
            LicenceState.Lapsed => "ended; new backups are paused, restoring still works" + key,
            _ => "one folder to a disk or network share",
        };
    }
}

public static class UpdateText
{
    public static string For(UpdateStatus update, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(time);
        var checkedAt = update.LastCheckedAt is { } at ? $" Last checked {Format.When(at, time)}." : "";
        return update.State switch
        {
            UpdateState.NotConfigured => $"This is version {update.CurrentVersion}. Automatic updates are not set up on this installation.",
            UpdateState.UpToDate when update.LastCheckedAt is null => $"This is version {update.CurrentVersion}. It has not checked for updates yet.",
            UpdateState.UpToDate => $"Version {update.CurrentVersion} is the latest.{checkedAt}",
            UpdateState.Downloading => $"Version {update.AvailableVersion} is downloading.",
            UpdateState.Ready => $"Version {update.AvailableVersion} is ready. It installs as soon as no backup or restore is running.",
            UpdateState.Installing => $"Version {update.AvailableVersion} is installing. The backup service restarts by itself.",
            UpdateState.Failed => $"Version {update.CurrentVersion}. The last update check did not work: {update.Message}{checkedAt}",
            _ => $"Version {update.CurrentVersion}.",
        };
    }
}
