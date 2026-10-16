using System.Security.Cryptography;
using CashGear.Backup.Agent.Runs;

namespace CashGear.Backup.Agent.Updates;

internal enum UpdateStage
{
    NotConfigured,
    UpToDate,
    Downloading,

    /// <summary>Downloaded and verified; installs once no run is active.</summary>
    Ready,
    Installing,
    Failed,
}

internal sealed record UpdateInfo(string CurrentVersion, string? AvailableVersion, UpdateStage Stage, string Message, DateTimeOffset? LastCheckedAt);

/// <summary>
/// Signed updates. Once a day (or when asked) the signed manifest is fetched and verified against
/// <see cref="AgentOptions.UpdatePublicKeys"/>. A newer version's MSI is downloaded into the data folder, checked
/// against the manifest's size and SHA-256, and its Authenticode signature must come from a certificate in
/// <see cref="AgentOptions.UpdatePublisherThumbprints"/>. It is installed only while no backup, restore or check
/// runs, and new runs wait until the installer has restarted the service. Nothing is installed without all three
/// settings, and nothing that fails a check is kept.
/// </summary>
internal sealed partial class UpdateService(
    AgentOptions options, IUpdateFeed feed, IPackageVerifier verifier, IUpdateInstaller installer, RunCoordinator runs, TimeProvider time,
    ILogger<UpdateService> logger)
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>An installer that has not restarted the service by then failed; backups resume.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _status;
    private UpdateManifest? _ready;
    private string? _readyPath;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _installStartedAt;

    public bool IsConfigured =>
        options.AutomaticUpdates
        && !string.IsNullOrWhiteSpace(options.UpdateManifestUrl)
        && options.UpdatePublicKeys.Count > 0
        && options.UpdatePublisherThumbprints.Count > 0;

    public UpdateInfo Get()
    {
        if (!IsConfigured)
        {
            return new UpdateInfo(AgentInfo.Version, null, UpdateStage.NotConfigured,
                options.AutomaticUpdates ? "Automatic updates are not set up on this installation." : "Automatic updates are turned off on this PC.", null);
        }

        return _status ?? new UpdateInfo(AgentInfo.Version, null, UpdateStage.UpToDate, "Not checked yet.", null);
    }

    /// <summary>Checks now (the "Check for updates" button); a verified update then installs if nothing runs.</summary>
    public async Task<UpdateInfo> CheckNowAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return Get();
        }

        await _gate.WaitAsync(ct);
        try
        {
            await CheckAsync(ct);
            await TryInstallAsync(ct);
            return Get();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Housekeeping: installs a ready update when nothing runs, and checks once a day.</summary>
    public async Task RunIfDueAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_status?.Stage == UpdateStage.Installing && _installStartedAt is { } started && now - started > InstallTimeout)
            {
                runs.EndMaintenance();
                Set(UpdateStage.Failed, $"Version {_ready?.Version} did not install. See install-{_ready?.Version}.log in the updates folder.");
                _ready = null;
                _readyPath = null;
            }

            var due = _status?.Stage == UpdateStage.Failed
                ? _lastAttemptAt is not { } attempted || now - attempted >= RetryAfterFailure
                : _status?.LastCheckedAt is not { } checkedAt || now - checkedAt >= CheckEvery;
            if (due && _status?.Stage is not (UpdateStage.Ready or UpdateStage.Installing))
            {
                await CheckAsync(ct);
            }

            await TryInstallAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        _lastAttemptAt = now;
        try
        {
            if (UpdateManifest.Verify(await feed.GetManifestAsync(ct), options.UpdatePublicKeys) is not { } manifest)
            {
                Set(UpdateStage.Failed, "The update feed's answer is not signed by CashGear, so nothing was downloaded.");
                LogRejected(logger, "manifest signature");
                return;
            }

            if (!manifest.IsNewerThan(AgentInfo.Version))
            {
                _ready = null;
                _readyPath = null;
                CleanUpDownloads(keep: null);
                Set(UpdateStage.UpToDate, "", checkedAt: now);
                return;
            }

            Directory.CreateDirectory(options.UpdateDirectory);
            var path = Path.Combine(options.UpdateDirectory, $"CashGearBackup-{manifest.Version}.msi");
            CleanUpDownloads(keep: path);
            if (!File.Exists(path) || await ProblemAsync(path, manifest, ct) is not null)
            {
                Set(UpdateStage.Downloading, "", available: manifest.Version);
                var partial = path + ".part";
                await feed.DownloadAsync(manifest.MsiUrl, partial, manifest.MsiSize, ct);
                File.Move(partial, path, overwrite: true);
            }

            if (await ProblemAsync(path, manifest, ct) is { } problem)
            {
                File.Delete(path);
                Set(UpdateStage.Failed, problem, available: manifest.Version);
                LogRejected(logger, problem);
                return;
            }

            _ready = manifest;
            _readyPath = path;
            Set(UpdateStage.Ready, "", available: manifest.Version, checkedAt: now);
            LogReady(logger, manifest.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Set(UpdateStage.Failed, ex.Message);
            LogCheckFailed(logger, ex.Message);
        }
    }

    private async Task TryInstallAsync(CancellationToken ct)
    {
        if (_status?.Stage != UpdateStage.Ready || _ready is not { } manifest || _readyPath is not { } path || !runs.TryBeginMaintenance())
        {
            return;
        }

        // New runs stay stopped only once the installer is on its way; any other way out of here lets them start again.
        var started = false;
        try
        {
            // Checked again right before use: the file sits on disk between the download and now.
            if (await ProblemAsync(path, manifest, ct) is { } problem)
            {
                File.Delete(path);
                Set(UpdateStage.Failed, problem, available: manifest.Version);
                _ready = null;
                _readyPath = null;
                return;
            }

            Set(UpdateStage.Installing, "", available: manifest.Version);
            _installStartedAt = time.GetUtcNow();
            LogInstalling(logger, manifest.Version);
            await installer.StartAsync(path, Path.Combine(options.UpdateDirectory, $"install-{manifest.Version}.log"), ct);
            started = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Set(UpdateStage.Failed, "The installer could not be started: " + ex.Message, available: manifest.Version);
        }
        finally
        {
            if (!started)
            {
                runs.EndMaintenance();
            }
        }
    }

    /// <summary>Null when the file is the one the manifest describes and carries the CashGear publisher's signature.</summary>
    private async Task<string?> ProblemAsync(string path, UpdateManifest manifest, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length != manifest.MsiSize)
        {
            return "The download does not match the update feed (wrong size).";
        }

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(manifest.MsiSha256, StringComparison.OrdinalIgnoreCase))
            {
                return "The download does not match the update feed (damaged or changed).";
            }
        }

        return verifier.Problem(path, options.UpdatePublisherThumbprints);
    }

    /// <summary>Old downloads and logs; the current download is kept.</summary>
    private void CleanUpDownloads(string? keep)
    {
        if (!Directory.Exists(options.UpdateDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(options.UpdateDirectory, "CashGearBackup-*"))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private void Set(UpdateStage stage, string message, string? available = null, DateTimeOffset? checkedAt = null) =>
        _status = new UpdateInfo(AgentInfo.Version, available, stage, message, checkedAt ?? _status?.LastCheckedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} downloaded and verified")]
    private static partial void LogReady(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update installing: version {Version}; the installer restarts the service")]
    private static partial void LogInstalling(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);
}
