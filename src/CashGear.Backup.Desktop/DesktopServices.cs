using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CashGear.Backup.UI.Components.Services;
using Microsoft.Win32;

namespace CashGear.Backup.Desktop;

/// <summary>The Windows folder dialog for the pages' folder fields.</summary>
internal sealed partial class WpfFolderPicker : IFolderPicker
{
    private const int ErrorSuccess = 0;

    public Task<string?> PickFolderAsync(string title, string? initialFolder, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            if (initialFolder is not null && Directory.Exists(initialFolder))
            {
                dialog.InitialDirectory = initialFolder;
            }

            var owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
            var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            return picked == true ? ToServicePath(dialog.FolderName) : null;
        }, System.Windows.Threading.DispatcherPriority.Normal, ct).Task;

    /// <summary>
    /// The backup service runs as LocalSystem and does not see this user's mapped drive letters, so a folder on a
    /// mapped drive becomes its UNC path (Z:\Backups to \\nas\share\Backups).
    /// </summary>
    internal static string ToServicePath(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return path;
        }

        var drive = path[..2];
        Span<char> buffer = stackalloc char[1024];
        var length = buffer.Length;
        return WNetGetConnectionW(drive, ref MemoryMarshal.GetReference(buffer), ref length) == ErrorSuccess
            ? buffer[..buffer.IndexOf('\0')].ToString() + path[2..]
            : path;
    }

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetGetConnectionW(string localName, ref char remoteName, ref int length);
}

internal sealed class DesktopShell : IDesktopShell
{
    public string DefaultRestoreParent { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Restored");

    public void OpenFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            return;
        }

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}
