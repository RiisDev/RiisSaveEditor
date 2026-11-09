using System.IO.Compression;
using System.Text;
using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks;

// Crafted files must be rejected with a SaveException: a stack overflow or an unbounded allocation
// would take the whole server down, and a shared signing key would let one visitor's save be trusted
// by another visitor's game.
public static class SecurityChecks
{
    public static int Run(SaveFormatRegistry registry, string fixtures)
    {
        int failures = 0;

        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} security: {name}");
            failures += ok ? 0 : 1;
        }

        bool Rejects(string file, byte[] data)
        {
            try
            {
                registry.Load(file, data);
                return false;
            }
            catch (SaveException)
            {
                return true;
            }
        }

        byte[] deep = Encoding.ASCII.GetBytes(new string('[', 1_000_000));
        Check("deeply nested JSON is rejected, not a stack overflow", Rejects("x.json", deep));
        Check("deeply nested RPG Maker JSON is rejected", Rejects("x.rmmzsave", Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(Zlib(deep)))));

        byte[] bomb = new byte[140 << 20];
        Array.Fill(bomb, (byte)' ');
        bomb[0] = (byte)'{';
        bomb[^1] = (byte)'}';
        Check("gzip bomb is rejected", Rejects("x.es3", Gzip(bomb)));
        Check("zip bomb is rejected", Rejects("x.save", ZipOf("log", bomb)));

        Check("a pickle nested a million tuples deep is rejected", Rejects("x.save", ZipOf("log", [0x80, 0x02, (byte)')', .. Enumerable.Repeat((byte)0x85, 1_000_000), (byte)'.'])));
        Check("a long chain of memo aliases can be read without overflowing the stack", AliasChainReads(registry));
        CacheSecurity(registry, Check);

        string sample = Path.Combine(fixtures, "renpy.save");
        if (File.Exists(sample))
        {
            string[] keys = [Path.Combine(Path.GetTempPath(), "riis-key-a-" + Guid.NewGuid()), Path.Combine(Path.GetTempPath(), "riis-key-b-" + Guid.NewGuid())];
            string Signer(string? keyPath)
            {
                SaveDocument doc = registry.Load("renpy.save", File.ReadAllBytes(sample));
                doc.SigningKeyPath = keyPath;
                return SignatureOf(doc.Format.Save(doc));
            }

            Check("same key file signs with the same key", Signer(keys[0]) == Signer(keys[0]));
            Check("different key files sign with different keys", Signer(keys[0]) != Signer(keys[1]));
            Check("no key file means a throwaway key", Signer(null) != Signer(null));
            foreach (string key in keys.Where(File.Exists))
            {
                File.Delete(key);
            }
        }

        return failures;
    }

    static void CacheSecurity(SaveFormatRegistry registry, Action<string, bool> check)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "riis-sec-" + Guid.NewGuid());
        string root = Path.Combine(baseDir, "cache");
        string sentinel = Path.Combine(baseDir, "sentinel.txt");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(sentinel, "keep");
        SaveCache cache = new(root, TimeProvider.System, maxTotalBytes: 400);
        SaveDocument doc = registry.Load("x.json", Encoding.UTF8.GetBytes("{\"a\":1}"));
        string code = SaveCache.NewCode();

        string[] hostileNames = [@"..\..\evil.txt", "../../evil.txt", @"C:\Windows\win.ini", @"\\server\share\x", "a:stream", "bad\0name\n.json", new string('x', 500), "", ".."];
        bool namesSafe = true;
        foreach (string hostile in hostileNames)
        {
            CacheEntry entry = cache.StoreAsync(code, doc with { FileName = hostile }, Encoding.UTF8.GetBytes("{\"n\":" + Math.Abs(hostile.GetHashCode()) % 100 + "}"), edited: false).GetAwaiter().GetResult();
            namesSafe &= entry.FileName.Length is > 0 and <= 120 && entry.FileName.IndexOfAny(['/', '\\', ':', '\0', '\n']) < 0;
        }

        check("hostile file names are stored as short, plain names", namesSafe);

        string[] hostileCodes = ["../../x", @"..\x", @"C:\x", @"\\server\share", "a\0b", "%2e%2e%2f", "", "not-a-guid"];
        bool codesRefused = true;
        foreach (string bad in hostileCodes)
        {
            try
            {
                cache.ListAsync(bad).GetAwaiter().GetResult();
                codesRefused = false;
            }
            catch (ArgumentException)
            {
            }
        }

        check("a resume code that is not a GUID is refused", codesRefused);

        bool idsRefused = true;
        foreach (string bad in new[] { @"..\..\sentinel", "../sentinel", "..", sentinel })
        {
            idsRefused &= cache.ReadAsync(code, bad).GetAwaiter().GetResult() is null;
            cache.DeleteAsync(code, bad).GetAwaiter().GetResult();
        }

        check("an entry id that is not a GUID reads and deletes nothing", idsRefused && File.ReadAllText(sentinel) == "keep");

        bool inside = Directory.EnumerateFileSystemEntries(baseDir).Select(Path.GetFileName).Order().SequenceEqual(["cache", "sentinel.txt"]);
        check("nothing was written outside the cache folder", inside);

        bool full;
        try
        {
            cache.StoreAsync(code, doc, Encoding.UTF8.GetBytes("{\"big\":" + new string((char)57, 600) + "}"), edited: false).GetAwaiter().GetResult();
            full = false;
        }
        catch (CacheFullException)
        {
            full = true;
        }

        check("the overall cache cap is enforced on every write", full);
        Directory.Delete(baseDir, recursive: true);
    }

    static bool AliasChainReads(SaveFormatRegistry registry)
    {
        // BININT1 1, then repeatedly: fetch the previous memo slot and memoize the fetched alias again.
        List<byte> p = [0x80, 0x02, (byte)'K', 1, (byte)'r', 0, 0, 0, 0];
        for (int i = 1; i <= 200_000; i++)
        {
            p.Add((byte)'j');
            p.AddRange(BitConverter.GetBytes(i - 1));
            p.Add((byte)'r');
            p.AddRange(BitConverter.GetBytes(i));
        }

        p.Add((byte)'.');
        return registry.Load("x.save", ZipOf("log", [.. p])).Root.Value == "1";
    }

    // The public key half of the "signatures" entry identifies who signed.
    static string SignatureOf(byte[] zipBytes)
    {
        using ZipArchive zip = new(new MemoryStream(zipBytes));
        using StreamReader reader = new(zip.GetEntry("signatures")!.Open());
        return reader.ReadToEnd().Split(' ')[1];
    }

    static byte[] Zlib(byte[] raw)
    {
        using MemoryStream m = new();
        using (ZLibStream z = new(m, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        return m.ToArray();
    }

    static byte[] Gzip(byte[] raw)
    {
        using MemoryStream m = new();
        using (GZipStream z = new(m, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        return m.ToArray();
    }

    static byte[] ZipOf(string entry, byte[] raw)
    {
        using MemoryStream m = new();
        using (ZipArchive z = new(m, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream s = z.CreateEntry(entry).Open();
            s.Write(raw);
        }

        return m.ToArray();
    }
}
