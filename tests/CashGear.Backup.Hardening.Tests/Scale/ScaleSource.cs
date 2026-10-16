using System.Diagnostics;
using System.Text.Json;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Scale;

/// <summary>
/// A generated scale source under CASHGEAR_SCALE_DIR\sources, kept between runs (generating a million files or
/// 200 GB takes a while). A marker file beside the folder records what was generated; a folder without a matching
/// marker (another size, or an interrupted generation) is generated again.
/// </summary>
internal sealed record ScaleSource(string Folder, string Kind, long Files, long Bytes, string? BigFileSha256)
{
    public const string ManyFilesKind = "many-files";
    public const string BigFileKind = "big-file";
    public const string BigFileName = "big-file.bin";

    /// <summary>Files per folder in the many-files source; Explorer-sized folders, not one flat folder of a million.</summary>
    private const int FilesPerFolder = 1000;

    /// <summary>Small files are 0 to 8 KiB (4 KiB on average), random, so nothing compresses or deduplicates.</summary>
    private const int MaxSmallFileBytes = 8 * 1024;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public static ScaleSource ManyFiles(int count) => GetOrCreate($"{ManyFilesKind}-{count}", ManyFilesKind, folder =>
    {
        long bytes = 0;
        var folders = (count + FilesPerFolder - 1) / FilesPerFolder;
        var written = 0L;
        Parallel.For(0, folders, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, d =>
        {
            var dir = Path.Combine(folder, $"d{d:00000}");
            Directory.CreateDirectory(dir);
            var buffer = new byte[MaxSmallFileBytes];
            long local = 0;
            for (var f = d * FilesPerFolder; f < Math.Min(count, (d + 1) * FilesPerFolder); f++)
            {
                var size = Random.Shared.Next(0, MaxSmallFileBytes + 1);
                Random.Shared.NextBytes(buffer.AsSpan(0, size));
                using (var stream = new FileStream(Path.Combine(dir, $"f{f:0000000}.dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    stream.Write(buffer, 0, size);
                }

                local += size;
            }

            Interlocked.Add(ref bytes, local);
            if (Interlocked.Increment(ref written) % 100 == 0)
            {
                Progress($"  {written * FilesPerFolder:N0} of {count:N0} files written");
            }
        });
        return (count, bytes, null);
    });

    public static ScaleSource BigFile(long bytes) => GetOrCreate($"{BigFileKind}-{bytes}", BigFileKind, folder =>
    {
        var next = 10L * HardeningSettings.GiB;
        var sha = SourceData.WriteRandomFile(Path.Combine(folder, BigFileName), bytes, written =>
        {
            if (written >= next)
            {
                Progress($"  {written / HardeningSettings.GiB:N0} of {bytes / HardeningSettings.GiB:N0} GiB written");
                next += 10L * HardeningSettings.GiB;
            }
        });
        return (1, bytes, sha);
    });

    /// <summary>The big file is checked against the hash taken when it was written, not hashed again here.</summary>
    public bool BigFileUnchanged() =>
        new FileInfo(Path.Combine(Folder, BigFileName)) is { Exists: true } file && file.Length == Bytes;

    private static ScaleSource GetOrCreate(string name, string kind, Func<string, (long Files, long Bytes, string? Sha)> generate)
    {
        var sources = Path.Combine(HardeningSettings.ScaleDirectory, "sources");
        var folder = Path.Combine(sources, name);
        var marker = folder + ".json";
        if (File.Exists(marker) && Directory.Exists(folder)
            && JsonSerializer.Deserialize<ScaleSource>(File.ReadAllText(marker)) is { } existing && existing.Folder == folder)
        {
            Progress($"reusing {kind} source {folder} ({existing.Files:N0} files, {HardeningSettings.Describe(existing.Bytes)})");
            return existing;
        }

        Directory.CreateDirectory(sources);
        File.Delete(marker);
        Workspace.ForceDelete(folder);
        Directory.CreateDirectory(folder);
        Progress($"generating {kind} source in {folder}");
        var stopwatch = Stopwatch.StartNew();
        var (files, bytes, sha) = generate(folder);
        var source = new ScaleSource(folder, kind, files, bytes, sha);
        File.WriteAllText(marker, JsonSerializer.Serialize(source, s_json));
        Progress($"generated {files:N0} files, {HardeningSettings.Describe(bytes)} in {stopwatch.Elapsed.TotalSeconds:N0} s");
        return source;
    }

    private static void Progress(string line) => ScaleTests.Log(line);
}
