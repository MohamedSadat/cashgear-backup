using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// A small NTFS volume in a VHDX, mounted into an empty folder (no drive letter): a backup disk that can really run
/// out of space. Attaching a virtual disk needs an elevated administrator, so tests that use it skip otherwise.
/// </summary>
internal sealed partial class SmallVolume : IAsyncDisposable
{
    private readonly string _vhdx;
    private readonly string _scripts;

    private SmallVolume(string root)
    {
        _vhdx = Path.Combine(root, "small-volume.vhdx");
        _scripts = Path.Combine(root, "diskpart");
        MountPoint = Path.Combine(root, "small-volume");
        Directory.CreateDirectory(_scripts);
        Directory.CreateDirectory(MountPoint);
    }

    /// <summary>The volume's root.</summary>
    public string MountPoint { get; }

    public static bool CanCreate => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Free space of the mounted volume (DriveInfo would report the drive the mount folder sits on).</summary>
    public long FreeBytes => GetDiskFreeSpaceExW(MountPoint + Path.DirectorySeparatorChar, out var free, out _, out _)
        ? (long)free
        : throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDiskFreeSpaceEx failed.");

    public static async Task<SmallVolume> CreateAsync(string root, long bytes, CancellationToken ct)
    {
        var volume = new SmallVolume(root);
        var megabytes = Math.Max(64, bytes / HardeningSettings.MiB).ToString(CultureInfo.InvariantCulture);
        await volume.DiskPartAsync("create",
        [
            $"create vdisk file=\"{volume._vhdx}\" maximum={megabytes} type=expandable",
            $"select vdisk file=\"{volume._vhdx}\"",
            "attach vdisk",
            "create partition primary",
            "format fs=ntfs quick label=CGFULL",
            $"assign mount=\"{volume.MountPoint}\"",
        ], ct);
        return volume;
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(_vhdx))
        {
            await DiskPartAsync("detach", [$"select vdisk file=\"{_vhdx}\"", "detach vdisk"], CancellationToken.None);
            File.Delete(_vhdx);
        }
    }

    private async Task DiskPartAsync(string name, IEnumerable<string> commands, CancellationToken ct)
    {
        var script = Path.Combine(_scripts, name + ".txt");
        await File.WriteAllLinesAsync(script, commands, ct);
        var start = new ProcessStartInfo("diskpart.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"diskpart ({name}) exited with code {process.ExitCode}:{Environment.NewLine}{await output}{await errors}");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);
}
