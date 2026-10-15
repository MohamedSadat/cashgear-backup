using System.Text.Encodings.Web;
using System.Text.Json;

namespace KopiaSpike;

public sealed record SpikeCheck(string Name, bool Passed, string Detail);

/// <summary>Pass/fail checks (asserted by KopiaSpike.Tests) and observations (recorded for FINDINGS.md).</summary>
public sealed class SpikeReport
{
    private static readonly JsonSerializerOptions s_write = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public required string RunId { get; init; }
    public required string KopiaVersion { get; init; }
    public required string ArtifactsDirectory { get; init; }
    public List<SpikeCheck> Checks { get; } = [];
    public SortedDictionary<string, object?> Observations { get; } = new(StringComparer.Ordinal);
    public string? Fatal { get; set; }
    public bool Passed => Fatal is null && Checks.Count > 0 && Checks.All(c => c.Passed);

    public void Check(string name, bool passed, string detail) => Checks.Add(new SpikeCheck(name, passed, detail));

    public void Observe(string key, object? value) => Observations[key] = value;

    public SpikeCheck? Find(string name) => Checks.FirstOrDefault(c => c.Name == name);

    public string ToJson() => JsonSerializer.Serialize(this, s_write);
}
