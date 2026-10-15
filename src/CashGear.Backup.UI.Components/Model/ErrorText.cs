using CashGear.Backup.Client;

namespace CashGear.Backup.UI.Components.Model;

/// <summary>What to tell the user when a call fails, by error kind. Unknown kinds show the agent's message.</summary>
public static class ErrorText
{
    public static string For(AgentCallException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Kind switch
        {
            AgentErrorKinds.AgentNotRunning =>
                "The CashGear Backup service is not running, so nothing was done. Start the service (or restart the PC) and try again.",
            AgentErrorKinds.AgentNotTrusted =>
                ex.Message + " Restart the PC; if this keeps happening, reinstall CashGear Backup.",
            AgentErrorKinds.ElevationCanceled =>
                "Windows did not get administrator approval, so nothing was changed.",
            AgentErrorKinds.ElevationFailed or AgentErrorKinds.PermissionDenied =>
                "This step needs administrator approval and it did not work: " + ex.Message,
            AgentErrorKinds.RestoreTargetNotEmpty =>
                "That folder is not empty. Choose a new or empty folder: CashGear Backup never overwrites existing files.",
            AgentErrorKinds.InvalidPassword =>
                "That password does not open the backup at that location.",
            AgentErrorKinds.RepositoryNotFound =>
                "There is no CashGear backup at that location. To start a new one there, choose \"Create a new backup destination\".",
            AgentErrorKinds.RepositoryAlreadyExists =>
                "There is already a backup at that location. To use it, choose \"Use a backup made earlier\" and enter its password.",
            AgentErrorKinds.DestinationUnreachable =>
                "The destination cannot be reached. Check that the drive is plugged in or the network share is available. " + ex.Message,
            _ => ex.Message,
        };
    }
}
