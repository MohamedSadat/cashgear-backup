using System.Globalization;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// Sizes and places for the suites, from environment variables. The defaults are the small variants that run in a
/// few minutes; docs/hardening.md lists the values of the full runs.
/// </summary>
internal static class HardeningSettings
{
    public const long MiB = 1024 * 1024;
    public const long GiB = 1024 * MiB;

    /// <summary>Data each chaos test backs up when the fault hits. Raise it on a fast machine if a backup ends before its fault.</summary>
    public static long ChaosSourceBytes => (long)(Number("CASHGEAR_CHAOS_SOURCE_MB", 1024) * MiB);

    /// <summary>
    /// How long after the fault a run that lost its destination may take to end by itself. Kopia alone keeps going file
    /// by file for a long time; the engine ends the run once Kopia's writes have failed for 90 s (docs/hardening.md,
    /// finding 3). A chaos test cancels and fails a run that is still going after this.
    /// </summary>
    public static TimeSpan ChaosFaultWait => TimeSpan.FromMinutes(Number("CASHGEAR_CHAOS_FAULT_WAIT_MIN", 2.5));

    /// <summary>Files in the many-files scale scenario (the plan's full run: 1,000,000).</summary>
    public static int ScaleFileCount => (int)Number("CASHGEAR_SCALE_FILES", 20_000);

    /// <summary>Size of the single file in the big-file scale scenario (the plan's full run: 200 GB, about 186.3 GiB).</summary>
    public static long ScaleBigFileBytes => (long)(Number("CASHGEAR_SCALE_BIG_FILE_GB", 1) * 1_000_000_000);

    /// <summary>Where the scale scenarios put their sources (kept between runs), repositories and restores.</summary>
    public static string ScaleDirectory => Text("CASHGEAR_SCALE_DIR") ?? Path.Combine(Path.GetTempPath(), "cashgear-scale");

    /// <summary>Restore every scale backup into a new folder and byte-compare it (needs the source's size again in free space).</summary>
    public static bool ScaleRestore => Text("CASHGEAR_SCALE_RESTORE") is not ("0" or "false");

    /// <summary>One JSON line per measured phase is appended here.</summary>
    public static string ScaleResultsFile => Text("CASHGEAR_SCALE_RESULTS") ?? RepoPaths.Combine("artifacts", "scale", "results.jsonl");

    /// <summary>Free text stored with each result, for example "1.0.0-rc1 full run".</summary>
    public static string ScaleLabel => Text("CASHGEAR_SCALE_LABEL") ?? "";

    public static string Describe(long bytes) => (bytes / (double)MiB).ToString("N0", CultureInfo.InvariantCulture) + " MiB";

    public static bool KeepWorkspace => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_WORKSPACE"));

    private static string? Text(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    private static double Number(string name, double fallback)
    {
        if (Text(name) is not { } text)
        {
            return fallback;
        }

        return double.TryParse(text.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"{name}='{text}' is not a positive number.");
    }
}
