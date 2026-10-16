using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Runs;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Unit;

/// <summary>
/// When the engine ends a backup whose destination has gone (docs/hardening.md, finding 3), without Kopia. The log
/// lines are the ones the pinned kopia wrote in its task log while the network to SFTP and S3 was cut and while an
/// SFTP server's quota was full (FINDINGS.md, Phase 5).
/// </summary>
public class DestinationWatchTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    private const string SftpRetry =
        "got error error opening connection: error establishing connecting: unable to dial [127.0.0.1:58355]: &ssh.ClientConfig{User:\"cashgear\", Timeout:0}: "
        + "ssh: handshake failed: read tcp 127.0.0.1:58379->127.0.0.1:58355: wsarecv: An existing connection was forcibly closed by the remote host. "
        + "when PutBlob(pbbb126cc549d6c14923384e944ef67b6-s85655f89371840d5146) (#6), sleeping for 1.1390625s before retrying";

    private const string SftpGiveUp =
        "async write error: unable to write content chunk 0 of FILE:f001.bin: unable to write pack: error writing pack: can't save pack data blob "
        + "pbbb126cc549d6c14923384e944ef67b6-s85655f89371840d5146: error writing pack file: unable to complete PutBlob(pbbb126cc549d6c14923384e944ef67b6-s85655f89371840d5146) "
        + "despite 10 retries: error opening connection: error establishing connecting: unable to dial [127.0.0.1:58355]: ssh: handshake failed: read tcp "
        + "127.0.0.1:58400->127.0.0.1:58355: wsarecv: An existing connection was forcibly closed by the remote host.";

    private const string S3Retry =
        "got error Put \"http://127.0.0.1:58547/backups/pc1/p5e52bd0aebcf7b6509a714485c9b0052-sf4abf5c54c401b88146\": read tcp 127.0.0.1:58673->127.0.0.1:58547: "
        + "wsarecv: An existing connection was forcibly closed by the remote host. when PutBlob(p5e52bd0aebcf7b6509a714485c9b0052-sf4abf5c54c401b88146) (#0), "
        + "sleeping for 100ms before retrying";

    private const string FullRetry =
        "got error can't write temporary file: sftp: \"failure: failure: denying write due to space limit\" (SSH_FX_FAILURE) when "
        + "PutBlob(p2c1a05c2480b683a9c386a3c83bc2c00-se99d8e0b35413308146) (#2), sleeping for 225ms before retrying";

    private const string FullGiveUp =
        "async write error: unable to write content chunk 0 of FILE:f025.bin: unable to write pack: error writing pack: can't save pack data blob "
        + "p213054f142354ea256a57252994c796e-se99d8e0b35413308146: error writing pack file: unable to complete PutBlob(p213054f142354ea256a57252994c796e-se99d8e0b35413308146) "
        + "despite 10 retries: cannot create temporary file: unrecognized error when creating temp file on SFTP: "
        + "/repo/p/213/054f142354ea256a57252994c796e-se99d8e0b35413308146.f.tmp.436f6f6692ca8073: permission denied";

    // A folder destination (here one that stopped taking writes): two quick retries, no retry lines, then this.
    private const string FolderGiveUp =
        "async write error: unable to write content chunk 1 of FILE:random-05.bin: unable to write pack: error writing pack: can't save pack data blob "
        + "pbf7ae3b56599a7d026221751af0bc661-sf5eee631e8b2869a146: error writing pack file: unable to complete "
        + @"PutBlobInPath:D:\Backups\CashGear\repo/p/bf7/ae3b56599a7d026221751af0bc661-sf5eee631e8b2869a146.f despite 2 retries: cannot create temporary file: "
        + @"open D:\Backups\CashGear\repo/p/bf7/ae3b56599a7d026221751af0bc661-sf5eee631e8b2869a146.f.tmp.8cf8db28650c59df: Access is denied.";

    // Not observed: the line above with Windows' wording for a full disk (ERROR_DISK_FULL). The VHDX chaos test, which
    // needs an elevated run, is the real check.
    private const string FolderFullGiveUp =
        "async write error: unable to write content chunk 0 of FILE:a.bin: unable to write pack: error writing pack: can't save pack data blob p442c4e7aa2f9ba003: "
        + @"error writing pack file: unable to complete PutBlobInPath:E:\CashGear Backup/p/442/c4e7aa2f9ba003.f despite 2 retries: cannot write temporary file: "
        + @"write E:\CashGear Backup/p/442/c4e7aa2f9ba003.f.tmp.1f: There is not enough space on the disk.";

    private const double T0 = 1_792_160_871.75;

    private static TaskLogEntry Entry(double seconds, string message, int level = 0, string module = "retry") =>
        new(level, T0 + seconds, module, message);

    private static TaskLogEntry GiveUp(double seconds, string message = SftpGiveUp) => Entry(seconds, message, level: 3, module: "object");

    /// <summary>Kopia's log as the engine reads it: the whole log so far, every time.</summary>
    private static DestinationLoss? Feed(DestinationWatch watch, IEnumerable<TaskLogEntry> entries, double pollEverySeconds = 2)
    {
        var log = new List<TaskLogEntry>();
        DestinationLoss? loss = null;
        foreach (var batch in entries.OrderBy(e => e.Ts).GroupBy(e => Math.Floor((e.Ts - T0) / pollEverySeconds)))
        {
            log.AddRange(batch);
            loss ??= watch.Observe(log);
        }

        return loss;
    }

    /// <summary>Retries every <paramref name="every"/> seconds over [from, to), with a give-up each time Kopia's 10 retries run out (about 11 s).</summary>
    private static IEnumerable<TaskLogEntry> Outage(double from, double to, double every = 1.0, string retry = SftpRetry, string giveUp = SftpGiveUp)
    {
        for (var t = from; t < to; t += every)
        {
            yield return Entry(t, retry);
            if ((t - from) % 11 < every && t - from >= 11)
            {
                yield return GiveUp(t, giveUp);
            }
        }
    }

    [Fact]
    public void Storage_errors_for_the_whole_window_after_a_give_up_mean_the_destination_is_lost()
    {
        var loss = Feed(new DestinationWatch(Window, Quiet), Outage(0, 120));

        Assert.NotNull(loss);
        Assert.Equal(DestinationProblem.Unreachable, loss.Problem);
        Assert.InRange(loss.FailingFor.TotalSeconds, 90, 92);
        Assert.StartsWith("Lost the destination: it could not be reached for 90 s", loss.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_watch_decides_once_and_keeps_its_answer()
    {
        var watch = new DestinationWatch(Window, Quiet);
        var log = Outage(0, 100).ToList();
        var first = watch.Observe(log);
        Assert.NotNull(first);

        log.AddRange(Outage(100, 200));
        Assert.Same(first, watch.Observe(log));
    }

    [Fact]
    public void An_outage_shorter_than_the_window_is_ridden_out()
    {
        // 60 s down (longer than Kopia's retries, so writes were given up), then the network is back. Kopia goes on and
        // the run ends with warnings, as before.
        Assert.Null(Feed(new DestinationWatch(Window, Quiet), Outage(0, 60).Append(Entry(400, "snapshotted directory", module: "uploader"))));
    }

    [Fact]
    public void Retries_that_Kopia_never_gives_up_on_do_not_end_the_run()
    {
        // A slow or flaky destination: writes need retries but every one gets through in the end, so the run can still succeed.
        var retries = Enumerable.Range(0, 300).Select(i => Entry(i, SftpRetry));
        Assert.Null(Feed(new DestinationWatch(Window, Quiet), retries));
    }

    [Fact]
    public void A_pause_in_the_storage_errors_longer_than_the_quiet_period_starts_the_count_again()
    {
        var watch = new DestinationWatch(Window, Quiet);

        // 60 s of outage, 31 s of quiet (the destination was back), then 60 s more: never 90 s without a pause.
        Assert.Null(Feed(watch, Outage(0, 60).Concat(Outage(91, 151))));

        // The second outage goes on to reach the window on its own.
        var loss = Feed(watch, Outage(0, 60).Concat(Outage(91, 190)));
        Assert.NotNull(loss);
        Assert.InRange(loss.FailingFor.TotalSeconds, 90, 92);
    }

    [Fact]
    public void Gaps_shorter_than_the_quiet_period_do_not_reset_it()
    {
        // S3 retries came up to 8.5 s apart in the observed outage; a destination behind a firewall that drops packets
        // can take 21 s per connection attempt.
        var loss = Feed(new DestinationWatch(Window, Quiet), Outage(0, 130, every: 21, retry: S3Retry).Append(GiveUp(22)));

        Assert.NotNull(loss);
        Assert.Equal(DestinationProblem.Unreachable, loss.Problem);
    }

    [Fact]
    public void A_full_destination_says_so_even_when_kopia_ends_with_permission_denied()
    {
        var loss = Feed(new DestinationWatch(Window, Quiet), Outage(0, 100, retry: FullRetry, giveUp: FullGiveUp));

        Assert.NotNull(loss);
        Assert.Equal(DestinationProblem.Full, loss.Problem);
        Assert.StartsWith("Lost the destination: it is full", loss.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_refuses_writes_is_access_denied_from_give_ups_alone()
    {
        // Observed: a give-up every 2.5 to 3 s and nothing else.
        var giveUps = Enumerable.Range(0, 40).Select(i => GiveUp(i * 2.7, FolderGiveUp));
        var loss = Feed(new DestinationWatch(Window, Quiet), giveUps);

        Assert.NotNull(loss);
        Assert.Equal(DestinationProblem.AccessDenied, loss.Problem);
        Assert.Contains("access denied", loss.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_disk_is_full()
    {
        var loss = Feed(new DestinationWatch(Window, Quiet), Enumerable.Range(0, 40).Select(i => GiveUp(i * 3, FolderFullGiveUp)));

        Assert.NotNull(loss);
        Assert.Equal(DestinationProblem.Full, loss.Problem);
    }

    [Fact]
    public void Errors_reading_the_source_are_not_destination_errors()
    {
        // Unreadable files fail one by one (CompletedWithWarnings, FINDINGS Phase 1a #5); they say nothing about the destination.
        var source = Enumerable.Range(0, 200).Select(i => Entry(i,
            $"unable to open file C:\\Data\\locked-{i}.db: The process cannot access the file because it is being used by another process.", level: 3, module: "uploader"));
        Assert.Null(Feed(new DestinationWatch(Window, Quiet), source));
    }

    [Fact]
    public void Entries_already_read_are_not_counted_again()
    {
        var watch = new DestinationWatch(Window, Quiet);
        List<TaskLogEntry> log = [Entry(0, SftpRetry), GiveUp(11)];

        // The same lines read again much later (the run is still going, the log did not grow) do not stretch the outage.
        for (var i = 0; i < 100; i++)
        {
            Assert.Null(watch.Observe(log));
        }

        log.Add(Entry(12, SftpRetry));
        Assert.Null(watch.Observe(log));
    }

    [Fact]
    public void The_message_names_the_time_and_never_quotes_kopias_error()
    {
        // Kopia's errors name blobs and internal structures; the run's message is for the user, the details go to the log.
        var loss = Feed(new DestinationWatch(Window, Quiet), Outage(0, 100, retry: S3Retry));

        Assert.NotNull(loss);
        Assert.DoesNotContain("PutBlob", loss.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", loss.Message, StringComparison.Ordinal);
        Assert.Contains("No restore point was recorded", loss.Message, StringComparison.Ordinal);
        Assert.Contains("PutBlob", loss.LastError, StringComparison.Ordinal);
    }
}
