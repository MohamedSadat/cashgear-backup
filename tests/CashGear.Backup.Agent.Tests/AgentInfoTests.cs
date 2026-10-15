namespace CashGear.Backup.Agent.Tests;

public class AgentInfoTests
{
    [Fact]
    public void Service_name_matches_the_installer_contract()
    {
        // The installer registers the service under this name; changing it breaks upgrades.
        Assert.Equal("CashGearBackupAgent", AgentInfo.ServiceName);
    }
}
