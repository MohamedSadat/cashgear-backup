using CashGear.Backup.Testing;

namespace CashGear.Backup.Installer.Tests;

/// <summary>
/// What the MSI does to a machine, read from its tables. The MSI is CASHGEAR_MSI or the newest one in
/// artifacts\installer (installer\build-installer.ps1); without one every test is skipped.
/// </summary>
public sealed class PackageTests : IDisposable
{
    private const string ServiceName = "CashGearBackupAgent";

    private readonly MsiDatabase _msi;

    public PackageTests()
    {
        var path = Environment.GetEnvironmentVariable("CASHGEAR_MSI");
        if (string.IsNullOrEmpty(path))
        {
            var folder = RepoPaths.Combine("artifacts", "installer");
            path = Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles("CashGearBackup-*.msi").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }

        Assert.SkipWhen(path is null || !File.Exists(path), "No MSI built: run installer\\build-installer.ps1 (or set CASHGEAR_MSI).");
        _msi = new MsiDatabase(path!);
    }

    [Fact]
    public void It_installs_per_machine_and_upgrades_in_place()
    {
        Assert.Equal("1", _msi.Property("ALLUSERS"));
        Assert.Equal("{F1AE5C97-47FD-45EF-A16B-BFDAF45F00A1}", _msi.Property("UpgradeCode"));
        Assert.Contains(_msi.Query("SELECT `UpgradeCode`, `Attributes` FROM `Upgrade`"), row => row[0] == "{F1AE5C97-47FD-45EF-A16B-BFDAF45F00A1}");
    }

    [Fact]
    public void The_agent_is_an_automatic_delayed_LocalSystem_service()
    {
        var service = Assert.Single(_msi.Query("SELECT `Name`, `ServiceType`, `StartType`, `StartName`, `Component_` FROM `ServiceInstall`"));
        Assert.Equal([ServiceName, "16", "2", "LocalSystem", "AgentService"], service);

        // Delayed start: sc.exe, deferred and not impersonated (3073), right after the service is installed.
        var delayed = Assert.Single(_msi.Query("SELECT `Type`, `Source`, `Target` FROM `CustomAction` WHERE `Action` = 'SetServiceDelayedStart'"));
        Assert.Equal(["3073", "Wix4UtilCA_X64", "WixQuietExec64"], delayed);
        var command = Assert.Single(_msi.Query("SELECT `Target` FROM `CustomAction` WHERE `Action` = 'SetSetServiceDelayedStart'"))[0];
        Assert.Equal($"\"[System64Folder]sc.exe\" config {ServiceName} start= delayed-auto", command);
        var sequence = _msi.Query("SELECT `Action`, `Sequence`, `Condition` FROM `InstallExecuteSequence`").ToDictionary(row => row[0]!, row => row);
        Assert.True(int.Parse(sequence["SetServiceDelayedStart"][1]!, System.Globalization.CultureInfo.InvariantCulture)
            > int.Parse(sequence["InstallServices"][1]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("NOT REMOVE~=\"ALL\"", sequence["SetServiceDelayedStart"][2]);

        // Restart after each failure, one minute later.
        var recovery = Assert.Single(_msi.Query(
            "SELECT `ServiceName`, `FirstFailureActionType`, `SecondFailureActionType`, `ThirdFailureActionType`, `RestartServiceDelayInSeconds` FROM `Wix4ServiceConfig`"));
        Assert.Equal([ServiceName, "restart", "restart", "restart", "60"], recovery);

        var agentExe = Assert.Single(_msi.Query("SELECT `FileName` FROM `File` WHERE `Component_` = 'AgentService'"));
        Assert.EndsWith("CashGear.Backup.Agent.exe", agentExe[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_is_stopped_before_files_change_and_removed_on_uninstall()
    {
        var control = Assert.Single(_msi.Query("SELECT `Name`, `Event`, `Wait` FROM `ServiceControl`"));
        // Start on install (1), stop on install (2) and uninstall (32), delete on uninstall (128).
        Assert.Equal([ServiceName, (1 + 2 + 32 + 128).ToString(System.Globalization.CultureInfo.InvariantCulture), "1"], control);
    }

    [Fact]
    public void The_data_folder_is_for_SYSTEM_and_Administrators_only()
    {
        var permission = Assert.Single(_msi.Query("SELECT `LockObject`, `Table`, `SDDLText` FROM `MsiLockPermissionsEx`"));
        Assert.Equal(["DATAFOLDER", "CreateFolder", "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"], permission);
        var directories = _msi.Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`");
        Assert.Contains(["DATAFOLDER", "CASHGEARDATAROOT", "Backup"], directories);
        Assert.Contains(["CASHGEARDATAROOT", "CommonAppDataFolder", "CashGear"], directories);
    }

    [Fact]
    public void Uninstall_never_deletes_the_data_folder()
    {
        Assert.Empty(_msi.Query("SELECT `FileKey` FROM `RemoveFile` WHERE `DirProperty` = 'DATAFOLDER'"));
        Assert.Empty(_msi.Query("SELECT `Wix4RemoveFolderEx` FROM `Wix4RemoveFolderEx`"));
    }

    [Fact]
    public void A_licence_key_from_a_silent_install_goes_to_licence_ini_and_not_to_the_log()
    {
        var ini = Assert.Single(_msi.Query("SELECT `FileName`, `DirProperty`, `Section`, `Key`, `Value`, `Action` FROM `IniFile`"));
        Assert.Equal(["licence.ini", "DATAFOLDER", "Licence", "Key", "[LICENCEKEY]", "0"], ini);
        Assert.Equal("LICENCEKEY", Assert.Single(_msi.Query("SELECT `Condition` FROM `Component` WHERE `Component` = 'LicenceKeyFile'"))[0]);
        Assert.Contains("LICENCEKEY", _msi.Property("MsiHiddenProperties")!.Split(';'));
        Assert.Contains("LICENCEKEY", _msi.Property("SecureCustomProperties")!.Split(';'));
    }

    [Fact]
    public void The_app_has_a_start_menu_shortcut_and_a_tray_icon_at_sign_in()
    {
        var shortcut = Assert.Single(_msi.Query("SELECT `Directory_`, `Target` FROM `Shortcut`"));
        Assert.Equal(["ProgramMenuFolder", "[APPFOLDER]CashGear.Backup.Desktop.exe"], shortcut);

        var run = Assert.Single(_msi.Query("SELECT `Root`, `Name`, `Value` FROM `Registry` WHERE `Key` = 'Software\\Microsoft\\Windows\\CurrentVersion\\Run'"));
        Assert.Equal(["2", "CashGear Backup", "\"[APPFOLDER]CashGear.Backup.Desktop.exe\" --tray"], run);
    }

    [Fact]
    public void The_engine_the_settings_and_the_notices_are_installed()
    {
        var files = _msi.Query("SELECT `FileName`, `Component_` FROM `File`").Select(row => row[0]!.Split('|').Last()).ToList();
        Assert.Contains("kopia.exe", files);
        Assert.Contains("appsettings.json", files);
        Assert.Contains("CashGear.Backup.Desktop.exe", files);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", files);
        Assert.DoesNotContain(files, f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Events_are_written_under_the_agent_source()
    {
        var source = Assert.Single(_msi.Query("SELECT `Name`, `Value` FROM `Registry` WHERE `Key` = 'SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\CashGear Backup'"));
        Assert.Equal("EventMessageFile", source[0]);
    }

    public void Dispose() => _msi?.Dispose();
}
