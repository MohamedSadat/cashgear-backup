using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;
using Microsoft.Win32;

namespace CashGear.Backup.Hardening.Tests.Scale;

/// <summary>
/// One measured phase of a scale run: one line of CASHGEAR_SCALE_RESULTS (JSON lines, appended), so duration and
/// memory can be compared release by release. Sizes are bytes, durations seconds.
/// </summary>
internal sealed record ScaleResult
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Label { get; init; }
    public required string Commit { get; init; }
    public required string AgentVersion { get; init; }
    public required string KopiaVersion { get; init; }
    public required MachineInfo Machine { get; init; }
    public required string Scenario { get; init; }
    public required long SourceFiles { get; init; }
    public required long SourceBytes { get; init; }

    /// <summary>first-backup, unchanged-backup, check (the agent's check at the default 5 % read-back) or restore.</summary>
    public required string Phase { get; init; }

    public required string State { get; init; }
    public required double Seconds { get; init; }
    public required long Files { get; init; }
    public required long Bytes { get; init; }
    public double MiBPerSecond => Seconds > 0 ? Bytes / (double)HardeningSettings.MiB / Seconds : 0;
    public double FilesPerSecond => Seconds > 0 ? Files / Seconds : 0;

    /// <summary>Sampled every 250 ms over the phase.</summary>
    public required MemoryPeaks Peaks { get; init; }

    /// <summary>Peak committed memory of the agent's whole process tree since the agent started (Job Object accounting).</summary>
    public required long JobPeakCommitSinceStart { get; init; }

    /// <summary>Size of the destination folder after the phase.</summary>
    public required long RepositoryBytes { get; init; }

    public string? Note { get; init; }

    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Append()
    {
        var file = HardeningSettings.ScaleResultsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, JsonSerializer.Serialize(this, s_json) + Environment.NewLine);
    }

    /// <summary>A Markdown table row: scenario, phase, state, time, throughput, peak memory of the agent and kopia.</summary>
    public string Row() => string.Create(CultureInfo.InvariantCulture,
        $"| {Scenario} | {Phase} | {State} | {Files:N0} | {Bytes / (double)HardeningSettings.GiB:N2} GiB | {Seconds:N1} s | {MiBPerSecond:N1} MiB/s | {FilesPerSecond:N0} | " +
        $"{Peaks.AgentWorkingSet / HardeningSettings.MiB:N0} / {Peaks.AgentPrivate / HardeningSettings.MiB:N0} MiB | " +
        $"{Peaks.KopiaWorkingSet / HardeningSettings.MiB:N0} / {Peaks.KopiaPrivate / HardeningSettings.MiB:N0} MiB | {RepositoryBytes / (double)HardeningSettings.GiB:N2} GiB |");

    public const string Header =
        "| Scenario | Phase | State | Files | Data | Time | MiB/s | Files/s | Agent peak WS / private | Kopia peak WS / private | Repository |" + "\n" +
        "|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|";
}

internal sealed record MachineInfo(string Os, string Cpu, int LogicalCpus, double RamGiB, string ScaleDirectory, string FileSystem)
{
    public static MachineInfo Current { get; } = Read();

    private static MachineInfo Read()
    {
        var cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? "unknown";
        var root = Path.GetPathRoot(Path.GetFullPath(HardeningSettings.ScaleDirectory)) ?? "";
        string fileSystem;
        try
        {
            fileSystem = new DriveInfo(root).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            fileSystem = "unknown";
        }

        return new MachineInfo(RuntimeInformation.OSDescription, cpu.Trim(), Environment.ProcessorCount,
            Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)HardeningSettings.GiB, 1), HardeningSettings.ScaleDirectory, fileSystem);
    }

    /// <summary>The commit the tests were built from, as git reports it ("unknown" without git).</summary>
    public static string Commit { get; } = GitCommit();

    private static string GitCommit()
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = RepoPaths.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "describe", "--always", "--dirty", "--abbrev=10" })
            {
                start.ArgumentList.Add(argument);
            }

            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
