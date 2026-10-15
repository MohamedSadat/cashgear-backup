namespace KopiaSpike;

/// <summary>Deterministic source tree covering the shapes a file backup must round-trip.</summary>
public static class TreeFixture
{
    private const int MiB = 1024 * 1024;

    public const string BigFile = "big/random-12MiB.bin";
    public const string DeepFile = "nested/a/b/c/deep.txt";
    public const string DeletedFile = "many/file-007.txt";

    public static void Create(string root, int seed = 20261015)
    {
        var rng = new Random(seed);

        Write(root, "empty.txt", []);
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        Directory.CreateDirectory(Path.Combine(root, "nested", "empty-leaf", "x", "y"));
        Write(root, DeepFile, "deep\n"u8.ToArray());

        // Non-ASCII, CJK and an emoji (surrogate pair); then "cafe-acute" precomposed vs decomposed,
        // which NTFS keeps as two different names.
        Write(root, "unicode/Ünïcødé-文件-\U0001F642.txt", "unicode\n"u8.ToArray());
        Write(root, "unicode/café.txt", "precomposed\n"u8.ToArray());
        Write(root, "unicode/café.txt", "decomposed\n"u8.ToArray());

        var big = new byte[12 * MiB];
        rng.NextBytes(big);
        Write(root, BigFile, big);
        Write(root, "big/zeros-3MiB.bin", new byte[3 * MiB]);

        for (var i = 0; i < 200; i++)
        {
            var bytes = new byte[rng.Next(0, 4096)];
            rng.NextBytes(bytes);
            Write(root, $"many/file-{i:000}.txt", bytes);
        }

        Write(root, "attrs/readonly.txt", "read-only\n"u8.ToArray());
        File.SetAttributes(Path.Combine(root, "attrs", "readonly.txt"), FileAttributes.ReadOnly);
        Write(root, "attrs/hidden.txt", "hidden\n"u8.ToArray());
        File.SetAttributes(Path.Combine(root, "attrs", "hidden.txt"), FileAttributes.Hidden);
    }

    /// <summary>Same-size in-place change, append, delete, add file, add empty directory.</summary>
    public static void Mutate(string root)
    {
        using (var stream = new FileStream(Path.Combine(root, BigFile), FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 5 * MiB;
            var b = stream.ReadByte();
            stream.Position = 5 * MiB;
            stream.WriteByte((byte)(b ^ 0xFF));
        }

        File.AppendAllText(Path.Combine(root, DeepFile), "appended\n");
        File.Delete(Path.Combine(root, DeletedFile));
        Write(root, "added/new-file.txt", "new\n"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(root, "added", "new-empty-dir"));
    }

    private static void Write(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
