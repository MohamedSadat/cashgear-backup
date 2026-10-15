using System.Text.Json;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

internal enum J
{
    String,
    Number,
    Bool,
    Object,
    Array,
}

/// <summary>Asserts the parts of Kopia's JSON the engine depends on: field names and JSON types.</summary>
internal static class JsonShape
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static void Has(JsonElement element, params (string Name, J Kind)[] fields)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        foreach (var (name, kind) in fields)
        {
            Assert.True(element.TryGetProperty(name, out var value), $"missing field '{name}' in {Truncate(element)}");
            Assert.True(Matches(value, kind), $"field '{name}' is {value.ValueKind}, expected {kind} in {Truncate(element)}");
        }
    }

    public static JsonElement Get(JsonElement element, string dottedPath)
    {
        foreach (var part in dottedPath.Split('.'))
        {
            Assert.True(element.TryGetProperty(part, out element), $"missing '{dottedPath}'");
        }

        return element;
    }

    private static bool Matches(JsonElement value, J kind) => kind switch
    {
        J.String => value.ValueKind == JsonValueKind.String,
        J.Number => value.ValueKind == JsonValueKind.Number,
        J.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        J.Object => value.ValueKind == JsonValueKind.Object,
        J.Array => value.ValueKind == JsonValueKind.Array,
        _ => false,
    };

    private static string Truncate(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
