using Bunit;

namespace CashGear.Backup.UI.Components.Tests;

public class SmokeTests : BunitContext
{
    [Fact]
    public void BUnit_renders_markup()
    {
        // Placeholder until Phase 2 adds real components; proves the bUnit + xUnit v3 wiring.
        var cut = Render(builder => builder.AddMarkupContent(0, "<p>CashGear Backup</p>"));

        cut.MarkupMatches("<p>CashGear Backup</p>");
    }
}
