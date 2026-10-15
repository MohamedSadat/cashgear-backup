using System.Text;
using System.Text.RegularExpressions;
using CashGear.Backup.UI.Components.Model;

namespace CashGear.Backup.UI.Components.Tests;

public partial class RecoveryKitTests
{
    private static readonly RecoveryKitInfo Info = new(
        DestinationName: "Backup disk",
        Location: @"E:\Backups\CashGear",
        MachineName: "PAT-LAPTOP",
        MachineIdentity: "cg-0123456789abcdef",
        Created: new DateTimeOffset(2026, 10, 15, 14, 32, 0, TimeSpan.Zero));

    [Fact]
    public void Is_a_well_formed_one_page_pdf_with_the_destination_and_the_steps()
    {
        var pdf = RecoveryKit.Create(Info);
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Count 1", text, StringComparison.Ordinal);
        Assert.Contains(@"E:\\Backups\\CashGear", text, StringComparison.Ordinal);
        Assert.Contains("Backup disk", text, StringComparison.Ordinal);
        Assert.Contains("Use a backup made earlier", text, StringComparison.Ordinal);
        Assert.Contains("Write the password here", text, StringComparison.Ordinal);

        // startxref points at the xref table, and every object offset in it points at "n 0 obj".
        var startXref = int.Parse(StartXref().Match(text).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startXref..], StringComparison.Ordinal);
        var offsets = XrefEntry().Matches(text[startXref..]).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(7, offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            Assert.StartsWith($"{i + 1} 0 obj", text[offsets[i]..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Contains_the_password_only_when_asked()
    {
        const string Password = "correct (horse) battery\\staple";

        var without = Encoding.Latin1.GetString(RecoveryKit.Create(Info));
        var with = Encoding.Latin1.GetString(RecoveryKit.Create(Info with { Password = Password }));

        Assert.DoesNotContain("correct", without, StringComparison.Ordinal);
        Assert.Contains(@"correct \(horse\) battery\\staple", with, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the password here", with, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_the_page_cannot_print_is_left_out_with_a_note()
    {
        Assert.False(RecoveryKit.CanPrint("pässwört-€"));
        Assert.True(RecoveryKit.CanPrint("pässwört ok"));

        var text = Encoding.Latin1.GetString(RecoveryKit.Create(Info with { Password = "日本語のパスワード" }));

        Assert.Contains("Write the password here", text, StringComparison.Ordinal);
        Assert.Contains("cannot be printed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_paths_wrap_instead_of_running_off_the_page()
    {
        var location = @"\\nas\backups\" + new string('x', 200);

        var text = Encoding.Latin1.GetString(RecoveryKit.Create(Info with { Location = location }));

        Assert.DoesNotContain(new string('x', 120), text, StringComparison.Ordinal);
        Assert.Equal(200, text.Count(c => c == 'x') - Encoding.Latin1.GetString(RecoveryKit.Create(Info)).Count(c => c == 'x'));
    }

    [GeneratedRegex(@"startxref\n(\d+)\n")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"(\d{10}) 00000 n")]
    private static partial Regex XrefEntry();
}
