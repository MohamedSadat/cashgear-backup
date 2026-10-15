using System.Security.Cryptography;

namespace KopiaSpike;

/// <summary>
/// Random per-start server credentials plus the repository password. Never printed: ToString is redacted,
/// and the leak scan searches every artifact for these values.
/// </summary>
public sealed record SpikeSecrets(
    string UiUser,
    string UiPassword,
    string ControlUser,
    string ControlPassword,
    string RepositoryPassword)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static SpikeSecrets Generate() => new(
        "ui-" + Random(12),
        Random(32),
        "control-" + Random(12),
        Random(32),
        Random(32));

    /// <summary>What the agent does on every start: fresh server credentials, same repository password.</summary>
    public SpikeSecrets WithNewServerCredentials() => Generate() with { RepositoryPassword = RepositoryPassword };

    public IEnumerable<string> Passwords => [UiPassword, ControlPassword, RepositoryPassword];

    public override string ToString() => "SpikeSecrets { redacted }";

    private static string Random(int length) => RandomNumberGenerator.GetString(Alphabet, length);
}
