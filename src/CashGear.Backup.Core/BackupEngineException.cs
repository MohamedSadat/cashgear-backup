namespace CashGear.Backup.Core;

/// <summary>What went wrong, in terms the agent and UI can act on without knowing the engine.</summary>
public enum BackupErrorKind
{
    /// <summary>The request itself is invalid (bad path, bad retention, unknown run).</summary>
    InvalidRequest,

    /// <summary>The repository exists but the password does not open it.</summary>
    InvalidPassword,

    /// <summary>There is no repository at the destination.</summary>
    RepositoryNotFound,

    /// <summary>Create was asked for a destination (or id) that already has a repository.</summary>
    RepositoryAlreadyExists,

    /// <summary>The operation needs a repository this engine has not created or connected.</summary>
    RepositoryNotConnected,

    /// <summary>The destination (disk, share, bucket) cannot be reached.</summary>
    DestinationUnreachable,

    /// <summary>The folder to back up does not exist or cannot be read.</summary>
    SourceUnavailable,

    /// <summary>The restore target exists and is not empty. Restores never overwrite.</summary>
    RestoreTargetNotEmpty,

    /// <summary>A backup of the same source is already running.</summary>
    RunAlreadyActive,

    /// <summary>The engine process is not running or did not answer.</summary>
    EngineUnavailable,

    /// <summary>The engine binary failed its integrity check; nothing was started.</summary>
    EngineBinaryRejected,

    /// <summary>The engine answered with something we do not understand (version drift).</summary>
    EngineProtocolError,
}

/// <summary>Every engine failure the caller is expected to handle surfaces as this exception.</summary>
public sealed class BackupEngineException : Exception
{
    public BackupEngineException(BackupErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BackupErrorKind Kind { get; }

    /// <summary>Throws <see cref="BackupErrorKind.InvalidRequest"/> when <paramref name="errors"/> is not empty.</summary>
    public static void ThrowIfInvalid(IReadOnlyList<string> errors, string what)
    {
        if (errors.Count > 0)
        {
            throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"{what} is invalid: {string.Join(" ", errors)}");
        }
    }
}
