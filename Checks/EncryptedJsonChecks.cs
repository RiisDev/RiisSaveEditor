using System.Security.Cryptography;
using System.Text;
using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks;

// Layered encrypted JSON saves (base64, then Rijndael with a 256-bit block, then base64 of JSON), built
// here from invented data and an invented password.
public static class EncryptedJsonChecks
{
    const string Password = "check-password-123";

    public static int Run()
    {
        int failures = 0;

        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} encrypted json: {name}");
            failures += ok ? 0 : 1;
        }

        string dir = Path.Combine(Path.GetTempPath(), "riis-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string known = Path.Combine(dir, "known-passwords.txt");
        File.WriteAllText(known, "# one per line\n\nother-password\n" + Password + "\n");

        SaveFormatRegistry withList = new([new SaveGameFreeFormat(known)]);
        SaveFormatRegistry noList = new([new SaveGameFreeFormat()]);

        const string json = "{\"Name\":\"Aria\",\"Gold\":120,\"Level\":7,\"Flags\":{\"Done\":true},\"Big\":639298126188510225}";
        byte[] utf16 = Build(json, Password, Encoding.Unicode, utf16Inner: true);

        SaveDocument doc = withList.Load("slot", utf16);
        Check("a password in the known list opens the save with no prompt", doc.Root.Items.Count == 5 && doc.Format.Save(doc).AsSpan().SequenceEqual(utf16));

        Check("without the list it asks for a password", Throws<PasswordRequiredException>(() => noList.Load("slot", utf16)));
        Check("a typed password opens it", noList.Load("slot", utf16, Password).Root.Items.Count == 5);
        Check("a wrong password is refused", Throws<PasswordRequiredException>(() => noList.Load("slot", utf16, "nope")));
        Check("a given password is tried alone, not with the known list", Throws<PasswordRequiredException>(() => withList.Load("slot", utf16, "nope")));

        byte[] es3 = UnityEs3Format.Wrap(Encoding.UTF8.GetBytes(json), Password, gzip: true);
        SaveDocument es3Doc = new SaveFormatRegistry([new UnityEs3Format(known)]).Load("slot.es3", es3);
        Check("an encrypted ES3 save opens from the known list", es3Doc.Root.Items.Count == 5);
        Check("an ES3 save opened from the list re-saves encrypted", new SaveFormatRegistry([new UnityEs3Format(known)]).Load("slot.es3", es3Doc.Format.Save(es3Doc)).Root.Items.Count == 5
            && Throws<PasswordRequiredException>(() => new SaveFormatRegistry([new UnityEs3Format()]).Load("slot.es3", es3Doc.Format.Save(es3Doc))));
        Check("ES3 without the list asks for a password", Throws<PasswordRequiredException>(() => new SaveFormatRegistry([new UnityEs3Format()]).Load("slot.es3", es3)));

        // About 1 in 128 random IVs start like plain JSON or gzip; those files must still ask for the password.
        byte[] braced = es3;
        while (braced[0] != (byte)'{')
        {
            braced = UnityEs3Format.Wrap(Encoding.UTF8.GetBytes(json), Password, gzip: false);
        }

        Check("an encrypted ES3 save whose IV starts with '{' still asks for a password",
            Throws<PasswordRequiredException>(() => new SaveFormatRegistry([new UnityEs3Format()]).Load("slot.es3", braced))
            && new SaveFormatRegistry([new UnityEs3Format()]).Load("slot.es3", braced, Password).Root.Items.Count == 5);
        Check("ES3 integer dictionary keys parse and survive an edit", RoundTripsBareKeys());

        // Single-byte XOR + base64 text saves, built here with an invented key and fields.
        const string list = "saveVersion=1.0|gold=120|musicOnTrue|seen=True|bag=Rope~Lamp";
        byte[] xored = Encoding.ASCII.GetBytes(Convert.ToBase64String([.. Encoding.ASCII.GetBytes(list).Select(b => (byte)(b ^ 0x2A))]));
        SaveFormatRegistry xorRegistry = new([new XorTextFormat(), new SaveGameFreeFormat()]);
        SaveDocument xorDoc = xorRegistry.Load("slot.save", xored);
        Check("an XOR text save opens and round-trips", xorDoc.Format is XorTextFormat && xorDoc.Root.Items.Count == 5
            && xorDoc.Format.Save(xorDoc).AsSpan().SequenceEqual(xored));
        xorDoc.Root.Items[1].Value = "9999";
        xorDoc.Root.Items[3].Value = "false";
        SaveDocument xorAgain = xorRegistry.Load("slot.save", xorDoc.Format.Save(xorDoc));
        Check("an XOR text edit keeps other fields and the game's True/False casing",
            Decode(xorDoc.Format.Save(xorDoc)) == "saveVersion=1.0|gold=9999|musicOnTrue|seen=False|bag=Rope~Lamp" && xorAgain.Root.Items[1].Value == "9999");
        xorDoc.Root.Items[4].Value = "a|b";
        Check("an XOR text value with '|' is refused", Throws<SaveException>(() => xorDoc.Format.Save(xorDoc)));

        // RPG Maker MV: lz-string base64 of the save JSON.
        const string mvJson = "{\"system\":{\"_battleCount\":3,\"_saveCount\":12},\"party\":{\"_gold\":450,\"_actors\":[1,2]}}";
        byte[] mv = Encoding.ASCII.GetBytes(LzString.CompressToBase64(mvJson));
        SaveDocument mvDoc = new SaveFormatRegistry([new RpgMakerMvFormat()]).Load("file1.rpgsave", mv);
        SaveTree.Collect(mvDoc.Root, n => n.Name == "_gold").Single().Value = "99999";
        Check("an RPG Maker MV save opens, edits and recompresses", mvDoc.Root.Items.Count == 2
            && LzString.DecompressFromBase64(Encoding.ASCII.GetString(mvDoc.Format.Save(mvDoc))) == mvJson.Replace("450", "99999"));
        Check("lz-string decompresses its own output for every character range", LzString.DecompressFromBase64(LzString.CompressToBase64("aaaaaaaaaabcabcé中中😀")) == "aaaaaaaaaabcabcé中中😀");

        using (System.Text.Json.JsonDocument exported = System.Text.Json.JsonDocument.Parse(SaveTree.ToJson(doc.Root)))
        {
            Check("JSON export keeps types and big integers", exported.RootElement.GetProperty("Gold").GetInt32() == 120
                && exported.RootElement.GetProperty("Big").GetRawText() == "639298126188510225"
                && exported.RootElement.GetProperty("Flags").GetProperty("Done").GetBoolean());
        }

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Check("a cancelled recovery stops", Throws<OperationCanceledException>(() =>
            PasswordRecovery.Recover(Encoding.ASCII.GetBytes("\0candidate-one\0candidate-two\0"), _ => false, ct: cancelled.Token)));

        SaveNode gold = SaveTree.Collect(doc.Root, n => n.Name == "Gold").Single();
        gold.Value = "9999";
        byte[] edited = doc.Format.Save(doc);
        SaveDocument again = withList.Load("slot", edited);
        Check("an edit survives save and reload", SaveTree.Collect(again.Root, n => n.Name == "Gold").Single().Value == "9999"
            && SaveTree.Collect(again.Root, n => n.Name == "Big").Single().Value == "639298126188510225");
        Check("each save uses a fresh salt and IV", !edited.AsSpan().SequenceEqual(doc.Format.Save(doc)));

        byte[] utf8 = Build(json, Password, new UTF8Encoding(false), utf16Inner: false);
        Check("a plain UTF-8 base64 file works too", withList.Load("slot", utf8).Root.Items.Count == 5);
        Check("a Save Game Free file is not mistaken for XOR text", !new XorTextFormat().CanLoad("slot", utf8));
        Check("random text is not mistaken for one", Throws<SaveException>(() => withList.Load("x", Encoding.ASCII.GetBytes(new string('A', 400)))));

        Directory.Delete(dir, recursive: true);
        return failures;
    }

    static string Decode(byte[] xored) =>
        Encoding.ASCII.GetString([.. Convert.FromBase64String(Encoding.ASCII.GetString(xored)).Select(b => (byte)(b ^ 0x2A))]);

    static bool RoundTripsBareKeys()
    {
        const string text = "{\r\n\t\"Slots\" : {\r\n\t\t\"Products\" : {161:26,-2:7\r\n\t\t}\r\n\t}\r\n}";
        byte[] original = Encoding.UTF8.GetBytes(text);
        SaveFormatRegistry registry = new([new UnityEs3Format()]);
        SaveDocument doc = registry.Load("slot.es3", original);
        if (!doc.Format.Save(doc).AsSpan().SequenceEqual(original))
        {
            return false;
        }

        SaveTree.Collect(doc.Root, n => n.Name == "161").Single().Value = "99";
        return Encoding.UTF8.GetString(doc.Format.Save(doc)) == text.Replace("161:26", "161:99");
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    static byte[] Build(string json, string password, Encoding outer, bool utf16Inner)
    {
        byte[] serialized = utf16Inner ? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(json)] : Encoding.UTF8.GetBytes(json);
        byte[] inner = Encoding.ASCII.GetBytes(Convert.ToBase64String(serialized));
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(32);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 1000, HashAlgorithmName.SHA1, 32);
        byte[] cipher = new RijndaelCipher(key, 32).EncryptCbc(inner, iv);
        string text = Convert.ToBase64String([.. salt, .. iv, .. cipher]);
        byte[] preamble = outer is UnicodeEncoding ? [0xFF, 0xFE] : [];
        return [.. preamble, .. outer.GetBytes(text)];
    }
}
