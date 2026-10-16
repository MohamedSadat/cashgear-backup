using System.Security.Cryptography;

namespace CashGear.Backup.Engine.Kopia.Hosting;

/// <summary>
/// Random Basic-auth users for one server start: the UI user drives the REST API, the control user can
/// only reach <c>/api/v1/control/*</c>. Passed to Kopia through its environment only; never printed.
/// </summary>
internal sealed class KopiaServerCredentials
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private KopiaServerCredentials(string uiUser, string uiPassword, string controlUser, string controlPassword)
    {
        UiUser = uiUser;
        UiPassword = uiPassword;
        ControlUser = controlUser;
        ControlPassword = controlPassword;
    }

    public string UiUser { get; }
    public string UiPassword { get; }
    public string ControlUser { get; }
    public string ControlPassword { get; }

    public IEnumerable<string> Passwords => [UiPassword, ControlPassword];

    public static KopiaServerCredentials Generate() =>
        new("ui-" + Random(12), Random(32), "control-" + Random(12), Random(32));

    public override string ToString() => "KopiaServerCredentials { redacted }";

    private static string Random(int length) => RandomNumberGenerator.GetString(Alphabet, length);
}
