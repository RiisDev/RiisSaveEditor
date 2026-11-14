using RiisSaveEditor.Checks;
using RiisSaveEditor.Saves;

// Usage: dotnet run --project Checks -- <SAVE_EXAMPLES dir> [--only text] [--sig]
// Loads every example, saves it untouched and requires the bytes to match the original.
string root = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SAVE_EXAMPLES");
int passwordIndex = Array.IndexOf(args, "--password");
string? password = passwordIndex >= 0 ? args[passwordIndex + 1] : null;
int onlyIndex = Array.IndexOf(args, "--only");
string? only = onlyIndex >= 0 ? args[onlyIndex + 1] : null;

SaveFormatRegistry registry = new([new GvasFormat(), new RenpyFormat(), new RenpyPersistentFormat(), new RpgMakerFormat(), new RpgMakerMvFormat(), new PlainJsonFormat(), new XorTextFormat(), new SaveGameFreeFormat(), new UnityEs3Format()]);
int failures = 0;

if (args.Contains("--write-modified"))
{
    string source = Path.Combine(root, "renpy.save");
    byte[] data = File.ReadAllBytes(source);
    SaveDocument doc = registry.Load("renpy.save", data);
    SaveNode target = SaveTree.Collect(doc.Root, n => n.IsEditable && n.Kind == NodeKind.Integer).First();
    Console.WriteLine($"{target.Name} {target.Value} -> 99999");
    target.Value = "99999";
    string dir = Path.GetFullPath(Path.Combine(root, "..", "Checks", "fixtures"));
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "renpy.save"), doc.Format.Save(doc));
    return 0;
}

if (args.Contains("--write-fixture"))
{
    string dir = Path.GetFullPath(Path.Combine(root, "..", "Checks", "fixtures"));
    Directory.CreateDirectory(dir);
    byte[] plain = File.ReadAllBytes(Directory.EnumerateFiles(root, "*.es3", SearchOption.AllDirectories).First());
    File.WriteAllBytes(Path.Combine(dir, "encrypted-sample.es3"), UnityEs3Format.Wrap(plain, "hunter2", gzip: true));
    Console.WriteLine("wrote " + dir);
    return 0;
}

int recoverIndex = Array.IndexOf(args, "--recover");
if (recoverIndex >= 0)
{
    byte[] saveData = File.ReadAllBytes(args[recoverIndex + 1]);
    byte[] metadata = File.ReadAllBytes(args[recoverIndex + 2]);
    string saveName = Path.GetFileName(args[recoverIndex + 1]);

    try
    {
        registry.Load(saveName, saveData);
        Console.WriteLine("Opened with no password (not encrypted, or no password needed).");
        return 0;
    }
    catch (PasswordRequiredException)
    {
        Console.WriteLine("Confirmed encrypted; recovering password...");
    }

    List<string> candidates = [.. PasswordRecovery.ExtractCandidateStrings(metadata)];
    Console.WriteLine($"Extracted {candidates.Count} candidate strings.");

    int expectIndex = Array.IndexOf(args, "--expect");
    if (expectIndex >= 0)
    {
        string expect = args[expectIndex + 1];
        int at = candidates.IndexOf(expect);
        Console.WriteLine(at >= 0
            ? $"Expected password \"{expect}\" found in candidates at index {at}."
            : $"Expected password \"{expect}\" was NOT extracted.");

        if (args.Contains("--extract-only"))
        {
            return 0;
        }
    }

    string? found = PasswordRecovery.Recover(metadata, candidate =>
    {
        try
        {
            registry.Load(saveName, saveData, candidate);
            return true;
        }
        catch (PasswordRequiredException)
        {
            return false;
        }
    });

    Console.WriteLine(found is null ? "No candidate opened the save." : $"RECOVERED PASSWORD: \"{found}\"");
    return 0;
}

int listIndex = Array.IndexOf(args, "--list");
IEnumerable<string> files = listIndex >= 0
    ? File.ReadAllLines(args[listIndex + 1]).Where(l => l.Length > 0)
    : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order();

failures += SecurityChecks.Run(registry, Path.GetFullPath(Path.Combine(root, "..", "Checks", "fixtures")));
failures += EncryptedJsonChecks.Run();
failures += PasswordRecoveryChecks.Run();
failures += RenpyCheatsChecks.Run();
failures += PickleMemoChecks();

if (listIndex < 0 && only is null)
{
    failures += await CacheChecks.RunAsync(registry);
}

foreach (string file in files)
{
    string rel = Path.GetRelativePath(root, file);
    if (only is not null && !rel.Contains(only, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    byte[] original = File.ReadAllBytes(file);
    try
    {
        SaveDocument doc = registry.Load(Path.GetFileName(file), original, password);
        byte[] saved = doc.Format.Save(doc);
        bool same = saved.AsSpan().SequenceEqual(original);
        if (!same && doc.Format is RenpyPersistentFormat)
        {
            same = RenpyPersistentFormat.SamePayload(original, saved);
        }

        if (!same && doc.Format is RenpyFormat)
        {
            same = RenpyFormat.SameExceptSignature(original, saved);
        }

        Console.WriteLine($"{(same ? "OK  " : "DIFF")} {rel} [{doc.Format.Name}] {Count(doc.Root)} nodes");
        if (!same)
        {
            int i = 0;
            while (i < Math.Min(saved.Length, original.Length) && saved[i] == original[i])
            {
                i++;
            }

            Console.WriteLine($"     first difference at byte {i} (original {original.Length} bytes, saved {saved.Length} bytes)");
            Console.WriteLine($"     original: {Show(original, i)}");
            Console.WriteLine($"     saved:    {Show(saved, i)}");
        }

        if (args.Contains("--edit"))
        {
            string? edit = EditRoundTrip(registry, doc, Path.GetFileName(file), password);
            Console.WriteLine("     " + edit);
            failures += edit!.StartsWith("MISMATCH") ? 1 : 0;
        }

        string? extra = doc.Format switch
        {
            UnityEs3Format => CryptoChecks(registry, Path.GetFileName(file), original, registry.Load(Path.GetFileName(file), original)),
            RpgMakerMvFormat => System.Text.Encoding.ASCII.GetString(original).TrimEnd() is string mvText
                && LzString.DecompressFromBase64(mvText) is string mvJson
                && (LzString.CompressToBase64(mvJson) == mvText || LzString.CompressToBase64Legacy(mvJson) == mvText)
                ? "recompress ok (lz-string output matches the game's file)"
                : "MISMATCH recompressing the unedited text differs from the game's file",
            GvasFormat => LegacyCheck(registry, Path.GetFileName(file), registry.Load(Path.GetFileName(file), original)),
            _ => null,
        };
        if (extra is not null)
        {
            Console.WriteLine("     " + extra);
            failures += extra.StartsWith("MISMATCH") ? 1 : 0;
        }

        string compare = CompareCheck(registry, Path.GetFileName(file), original, password);
        Console.WriteLine("     " + compare);
        failures += compare.StartsWith("MISMATCH") ? 1 : 0;

        failures += same ? 0 : 1;
    }
    catch (SaveException e)
    {
        Console.WriteLine($"SKIP {rel}: {e.Message}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"FAIL {rel}: {e.GetType().Name}: {e.Message}");
        failures++;
    }
}

if (args.Contains("--sig"))
{
    Console.WriteLine("Ren'Py signature check: " + RenpyFormat.DescribeSignatureScheme(Path.Combine(root, "renpy.save")));
}

return failures;

// A list holding one string twice, the second time through the memo (BINPUT/BINGET in protocol 2,
// MEMOIZE in protocol 4). Editing the first item must leave the second one as it was.
static int PickleMemoChecks()
{
    int failed = 0;
    foreach ((string label, byte[] data) in new (string, byte[])[]
    {
        ("protocol 2", [0x80, 2, (byte)']', (byte)'q', 0, (byte)'(', (byte)'X', 1, 0, 0, 0, (byte)'a', (byte)'q', 1, (byte)'h', 1, (byte)'e', (byte)'.']),
        ("protocol 4", [0x80, 4, (byte)']', 0x94, (byte)'(', 0x8c, 1, (byte)'a', 0x94, (byte)'h', 1, (byte)'e', (byte)'.']),
    })
    {
        PickleDocument doc = Pickle.Parse(data);
        doc.Root.Children[0].Value = "edited";
        PickleDocument again = Pickle.Parse(Pickle.Write(doc));
        bool ok = again.Root.Children.Select(c => c.Value).SequenceEqual(["edited", "a"]) && doc.Root.Children[1].Value == "a";
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} pickle: editing a shared string leaves its other uses alone ({label})");
        failed += ok ? 0 : 1;
    }

    return failed;
}

static string Show(byte[] data, int at) =>
    System.Text.Encoding.Latin1.GetString(data, Math.Max(0, at - 20), Math.Min(50, data.Length - Math.Max(0, at - 20)))
        .Replace("\r", "\r").Replace("\n", "\n").Replace("\t", "\t").Replace("\0", "\0");

static int Count(SaveNode n)
{
    int total = 1;
    foreach (SaveNode c in n.Children)
    {
        total += Count(c);
    }

    return total;
}

static string? EditRoundTrip(SaveFormatRegistry registry, SaveDocument doc, string fileName, string? password = null)
{
    SaveNode? target = Flatten(doc.Root, "").Select(x => x.Node).FirstOrDefault(n => n.IsEditable && n.Kind != NodeKind.Bool);
    if (target is null)
    {
        return "no editable value";
    }

    target.Value = target.Kind switch
    {
        NodeKind.Integer => target.Range is { } r && r.Max < 123456789 ? r.Max.ToString() : "123456789",
        NodeKind.Float => "12345.5",
        _ => "Edited_" + target.Value,
    };

    List<string> expected = [.. Flatten(doc.Root, "").Select(x => x.Path + "=" + x.Node.Value)];
    byte[] saved = doc.Format.Save(doc);
    SaveDocument reloaded = registry.Load(fileName, saved, password);
    List<string> actual = [.. Flatten(reloaded.Root, "").Select(x => x.Path + "=" + x.Node.Value)];
    if (expected.SequenceEqual(actual))
    {
        return $"edit kept ({target.Name} = {target.Value}, {saved.Length} bytes)";
    }

    int d = 0;
    while (d < Math.Min(expected.Count, actual.Count) && expected[d] == actual[d])
    {
        d++;
    }

    static string Clip(string s) => s.Length > 120 ? s[..120] + "..." : s;
    return $"MISMATCH after edit of {target.Name}: #{d} expected '{Clip(d < expected.Count ? expected[d] : "<end>")}' got '{Clip(d < actual.Count ? actual[d] : "<end>")}' ({expected.Count} vs {actual.Count} values)";
}

static IEnumerable<(string Path, SaveNode Node)> Flatten(SaveNode node, string path)
{
    foreach (SaveNode child in node.Children)
    {
        string p = path + "/" + child.Name;
        yield return (p, child);
        foreach ((string, SaveNode) deeper in Flatten(child, p))
        {
            yield return deeper;
        }
    }
}

static List<string> Values(SaveDocument doc) => [.. Flatten(doc.Root, "").Select(x => x.Path + "=" + x.Node.Value)];

static string CryptoChecks(SaveFormatRegistry registry, string name, byte[] original, SaveDocument doc)
{
    List<string> expected = Values(doc);
    List<string> problems = [];
    foreach ((string? password, bool gzip) in new (string?, bool)[] { ("hunter2", false), ("hunter2", true), (null, true) })
    {
        byte[] wrapped = UnityEs3Format.Wrap(original, password, gzip);
        string label = $"{(password is null ? "gzip" : gzip ? "encrypted+gzip" : "encrypted")}";

        if (password is not null)
        {
            foreach (string? attempt in new string?[] { null, "wrong" })
            {
                try
                {
                    registry.Load(name, wrapped, attempt);
                    problems.Add($"{label}: opened with {(attempt is null ? "no" : "wrong")} password");
                }
                catch (PasswordRequiredException)
                {
                }
            }
        }

        SaveDocument opened = registry.Load(name, wrapped, password);
        if (!Values(opened).SequenceEqual(expected))
        {
            problems.Add($"{label}: tree differs after open");
        }

        SaveDocument reopened = registry.Load(name, opened.Format.Save(opened), password);
        if (!Values(reopened).SequenceEqual(expected))
        {
            problems.Add($"{label}: tree differs after save and reopen");
        }
    }

    return problems.Count == 0 ? "crypto ok (encrypted, encrypted+gzip, gzip; wrong/missing password rejected)" : "MISMATCH " + string.Join("; ", problems);
}

static string LegacyCheck(SaveFormatRegistry registry, string name, SaveDocument doc)
{
    byte[] legacy = GvasFormat.SaveAsLegacyForTest(doc);
    SaveDocument reloaded = registry.Load(name, legacy);
    List<string> before = Values(doc);
    List<string> after = Values(reloaded);
    if (!after.SequenceEqual(before))
    {
        int d = 0;
        while (d < Math.Min(before.Count, after.Count) && before[d] == after[d])
        {
            d++;
        }

        return $"MISMATCH legacy layout changed the values at #{d}: expected '{(d < before.Count ? before[d] : "<end>")}' got '{(d < after.Count ? after[d] : "<end>")}' ({before.Count} vs {after.Count} nodes)";
    }

    return reloaded.Format.Save(reloaded).AsSpan().SequenceEqual(legacy)
        ? $"legacy layout ok ({legacy.Length} bytes, byte-exact on re-save)"
        : "MISMATCH legacy layout is not byte-exact on re-save";
}

static string CompareCheck(SaveFormatRegistry registry, string name, byte[] original, string? password = null)
{
    SaveDocument now = registry.Load(name, original, password);
    SaveDocument before = registry.Load(name, original, password);
    if (SaveTree.Compare(now.Root, before.Root).Count != 0)
    {
        return "MISMATCH identical files report changes";
    }

    SaveNode? target = SaveTree.Collect(now.Root, n => n.IsEditable && n.Kind == NodeKind.Integer
        && System.Numerics.BigInteger.TryParse(n.Value, out System.Numerics.BigInteger v) && v < 1000 && (n.Range is null || n.Range.Max > 1000)).FirstOrDefault();
    if (target is null)
    {
        return "compare skipped (no small integer)";
    }

    target.Value = (System.Numerics.BigInteger.Parse(target.Value!) + 5).ToString();
    Dictionary<SaveNode, Change> changes = SaveTree.Compare(now.Root, before.Root);
    return changes.Count == 1 && changes.TryGetValue(target, out Change? c) && c.Kind == ChangeKind.Increased && c.Delta == 5
        ? "compare ok (one increase of 5 detected)"
        : $"MISMATCH compare found {changes.Count} changes";
}
