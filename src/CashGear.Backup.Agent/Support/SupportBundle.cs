using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Licensing;
using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Updates;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Support;

/// <summary>
/// A zip for support: summary.json (versions, licence state, destinations, jobs, recent runs, settings), the agent's
/// logs from the last week and the update installer's logs. Built only from what is safe to hand over: no
/// passwords, DPAPI blobs, licence keys or tokens, no email addresses, and log lines that look like they carry a
/// secret are cut anyway (the agent never logs one, this is a second line of defence).
/// </summary>
internal sealed partial class SupportBundleService(
    AgentOptions options, AgentState state, IDbContextFactory<StateDbContext> dbs, SettingsStore settings, LicenceService licences,
    UpdateService updates, TimeProvider time)
{
    public static readonly TimeSpan LogAge = TimeSpan.FromDays(7);
    public const long MaxLogBytes = 50L * 1024 * 1024;
    private const int RecentRuns = 100;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public async Task<(string FileName, byte[] Content)> CreateAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddTextAsync(zip, "README.txt", Readme(now), ct);
            await AddTextAsync(zip, "summary.json", JsonSerializer.Serialize(await SummaryAsync(now, ct), s_json), ct);

            long budget = MaxLogBytes;
            foreach (var file in RecentFiles(options.LogDirectory, "*.log", now))
            {
                budget = await AddLogAsync(zip, "logs/" + Path.GetFileName(file), file, budget, ct);
            }

            foreach (var file in RecentFiles(options.UpdateDirectory, "install-*.log", now).Take(3))
            {
                budget = await AddLogAsync(zip, "updates/" + Path.GetFileName(file), file, budget, ct);
            }
        }

        return ($"cashgear-support-{now:yyyyMMdd-HHmmss}.zip", buffer.ToArray());
    }

    /// <summary>Cuts what follows words like password, secret, token or authorization on a line.</summary>
    internal static string Scrub(string text) => SecretPattern().Replace(text, m => m.Groups["name"].Value + m.Groups["sep"].Value + "[removed]");

    private async Task<object> SummaryAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var destinations = await db.Destinations.AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(RecentRuns).ToListAsync(ct);
        var current = await settings.LoadAsync(ct);
        var licence = await licences.GetAsync(ct);
        var update = updates.Get();
        return new
        {
            createdAt = now,
            agentVersion = AgentInfo.Version,
            windows = Environment.OSVersion.VersionString,
            is64Bit = Environment.Is64BitOperatingSystem,
            machineIdentity = state.MachineIdentity,
            ready = state.Ready,
            licence = new { edition = licence.Edition.ToString(), state = licence.State.ToString(), licence.ExpiresAt, licence.GraceUntil, licence.LastCheckedAt, licence.Message },
            updates = new { stage = update.Stage.ToString(), update.AvailableVersion, update.Message, update.LastCheckedAt },
            settings = new
            {
                current.AlertAfterDays,
                current.VerifyEveryDays,
                current.VerifySamplePercent,
                email = new
                {
                    current.Email.Enabled,
                    current.Email.Host,
                    current.Email.Port,
                    security = current.Email.Security.ToString(),
                    usernameSet = current.Email.Username.Length > 0,
                    passwordSaved = current.Email.ProtectedPassword is not null,
                    recipients = current.Email.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Length,
                },
            },
            destinations = destinations.Select(d => new
            {
                d.Id,
                d.Name,
                kind = d.Kind.ToString(),
                d.Location,
                settings = d.Settings is null ? null : WithoutKeyId(d.Settings),
                credentialSaved = d.ProtectedCredential is not null,
                d.CreatedAt,
            }),
            jobs = jobs.Select(j => new
            {
                j.Id,
                j.Name,
                j.DestinationId,
                j.SourcePath,
                j.Enabled,
                schedule = new { kind = j.ScheduleKind.ToString(), j.ScheduleMinuteOfDay, j.ScheduleDays, j.ScheduleIntervalHours },
                retention = new { j.KeepLatest, j.KeepHourly, j.KeepDaily, j.KeepWeekly, j.KeepMonthly, j.KeepAnnual },
                compression = j.Compression.ToString(),
                excludes = j.Excludes,
                j.CreatedAt,
                j.UpdatedAt,
            }),
            recentRuns = runs.Select(r => new
            {
                r.Id,
                r.JobId,
                kind = r.Kind.ToString(),
                state = r.State.ToString(),
                trigger = r.Trigger.ToString(),
                r.StartedAt,
                r.FinishedAt,
                r.SnapshotId,
                r.Files,
                r.Bytes,
                r.FailedItemCount,
                message = r.Message is null ? null : Scrub(r.Message),
                r.RestoreTarget,
            }),
        };
    }

    /// <summary>The destination's settings without the S3 access key id (not a secret, but not needed either).</summary>
    private static Dictionary<string, JsonElement>? WithoutKeyId(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        values?.Remove("AccessKeyId");
        return values;
    }

    private static IEnumerable<string> RecentFiles(string directory, string pattern, DateTimeOffset now) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern)
                .Select(f => new FileInfo(f))
                .Where(f => now - f.LastWriteTimeUtc < LogAge)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
            : [];

    /// <summary>Adds the scrubbed log while the budget lasts (newest files first); returns what is left.</summary>
    private static async Task<long> AddLogAsync(ZipArchive zip, string name, string path, long budget, CancellationToken ct)
    {
        if (budget <= 0)
        {
            return budget;
        }

        string text;
        try
        {
            // The agent keeps its current log open.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync(ct);
        }
        catch (IOException)
        {
            return budget;
        }

        if (text.Length > budget)
        {
            text = text[^(int)budget..];
        }

        await AddTextAsync(zip, name, Scrub(text), ct);
        return budget - text.Length;
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    private static string Readme(DateTimeOffset now) =>
        $"""
        CashGear Backup support file, made {now:yyyy-MM-dd HH:mm} UTC on {Environment.MachineName}.

        summary.json  versions, licence state, backup destinations and jobs, the last {RecentRuns} runs, settings
        logs/         the backup service's logs from the last {LogAge.Days} days
        updates/      the update installer's logs, if an update was installed

        No passwords, keys or saved credentials are in this file. It does name the folders that are backed up and
        where the backups are kept. Send it only to CashGear support.
        """;

    // An Authorization header loses the rest of its line; other secret-looking names lose their value.
    [GeneratedRegex(@"(?<name>\bauthorization\b)(?<sep>""?\s*[:=]\s*)[^\r\n]*|(?<name>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key(?:[_-]?id)?|secret[_-]?access[_-]?key)\b)(?<sep>""?\s*[:=]\s*)(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
}
