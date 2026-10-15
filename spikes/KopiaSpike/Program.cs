using CashGear.Backup.Testing;
using KopiaSpike;

// Usage: dotnet run --project spikes\KopiaSpike [-- --keep-workspace] [--out <dir>]
// Uses the pinned tools\kopia\kopia.exe (or CASHGEAR_KOPIA_EXE), verified against kopia.pin.json.
var keepWorkspace = args.Contains("--keep-workspace");
var outIndex = Array.IndexOf(args, "--out");
var artifactsRoot = outIndex >= 0 && outIndex + 1 < args.Length
    ? Path.GetFullPath(args[outIndex + 1])
    : RepoPaths.Combine("artifacts", "spike");

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var options = new SpikeOptions(KopiaTestBinary.ExePath, KopiaTestBinary.Pin.Version, keepWorkspace, artifactsRoot);
Console.WriteLine($"Kopia {options.KopiaVersion}: running the Phase 0 spike...");
var report = await SpikeScenario.RunAsync(options, cts.Token);

foreach (var check in report.Checks)
{
    Console.WriteLine($"  [{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
}

if (report.Fatal is not null)
{
    Console.WriteLine($"  [ABORTED] {report.Fatal}");
}

Console.WriteLine($"Artifacts: {report.ArtifactsDirectory}");
Console.WriteLine(report.Passed ? "Phase 0 gate: PASSED" : "Phase 0 gate: FAILED");
return report.Passed ? 0 : 1;
