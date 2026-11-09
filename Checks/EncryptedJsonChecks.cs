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

        SaveNode gold = SaveTree.Collect(doc.Root, n => n.Name == "Gold").Single();
        gold.Value = "9999";
        byte[] edited = doc.Format.Save(doc);
        SaveDocument again = withList.Load("slot", edited);
        Check("an edit survives save and reload", SaveTree.Collect(again.Root, n => n.Name == "Gold").Single().Value == "9999"
            && SaveTree.Collect(again.Root, n => n.Name == "Big").Single().Value == "639298126188510225");
        Check("each save uses a fresh salt and IV", !edited.AsSpan().SequenceEqual(doc.Format.Save(doc)));

        byte[] utf8 = Build(json, Password, new UTF8Encoding(false), utf16Inner: false);
        Check("a plain UTF-8 base64 file works too", withList.Load("slot", utf8).Root.Items.Count == 5);
        Check("random text is not mistaken for one", Throws<SaveException>(() => withList.Load("x", Encoding.ASCII.GetBytes(new string('A', 400)))));

        Directory.Delete(dir, recursive: true);
        return failures;
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
