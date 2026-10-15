using System.Diagnostics;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests;

[Trait("Category", "Integration")]
public class PinnedBinaryTests
{
    [Fact]
    public void Kopia_exe_matches_the_pin()
    {
        Assert.True(File.Exists(KopiaTestBinary.ExePath));
    }

    [Fact]
    public async Task Kopia_reports_the_pinned_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var psi = new ProcessStartInfo(KopiaTestBinary.ExePath, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = (await stdout + await stderr).Trim();
        Assert.Equal(0, process.ExitCode);
        Assert.StartsWith(KopiaTestBinary.Pin.Version + " ", output, StringComparison.Ordinal);
    }
}
