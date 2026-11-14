using System.Security.Cryptography;
using System.Text;

namespace RiisSaveEditor.Saves;

// The "Save Game Free" Unity asset writes JSON saves in layers. Outermost is base64 text (in whatever
// encoding the game chose, often UTF-16). That decodes to a 32 byte salt, a 32 byte IV and the ciphertext
// of a Rijndael cipher with a 256-bit block, keyed by PBKDF2-SHA1 (1000 rounds) over the game's password.
// The plaintext is base64 again, of the serialized JSON text. The password is a per-game secret and is
// looked up in an operator-managed file of known passwords (App_Data/known-passwords.txt) or typed by
// the user, and never stored in the code. An untouched save is returned byte-for-byte; an edited one is
// rebuilt with a fresh salt and IV, as the game itself does on every save.
public sealed class SaveGameFreeFormat(string? knownPasswordsPath = null) : ISaveFormat
{
    const int SaltBytes = 32;
    const int IvBytes = 32;
    const int Block = 32;
    const int Rounds = 1000;

    sealed record TextShape(Encoding Encoding, byte[] Preamble)
    {
        public static TextShape Detect(ReadOnlySpan<byte> bytes, out int skip)
        {
            if (bytes.StartsWith("﻿"u8))
            {
                skip = 3;
                return new TextShape(new UTF8Encoding(false, true), [0xEF, 0xBB, 0xBF]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                skip = 2;
                return new TextShape(new UnicodeEncoding(false, false, true), [0xFF, 0xFE]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                skip = 2;
                return new TextShape(new UnicodeEncoding(true, false, true), [0xFE, 0xFF]);
            }

            skip = 0;
            return new TextShape(new UTF8Encoding(false, true), []);
        }

        public string Decode(ReadOnlySpan<byte> bytes, int skip) => Encoding.GetString(bytes[skip..]);

        public byte[] Encode(string text) => [.. Preamble, .. Encoding.GetBytes(text)];
    }

    sealed record State(byte[] Original, string Text, TextShape Outer, TextShape Inner, string Password);

    public string Name => "Unity (Save Game Free)";

    public bool CanLoad(string fileName, ReadOnlySpan<byte> data) => TryUnwrapOuter(data, out _, out _);

    public SaveDocument Load(string fileName, byte[] data, string? password = null)
    {
        if (!TryUnwrapOuter(data, out byte[] raw, out TextShape? outer))
        {
            throw new SaveException("Not a Save Game Free file.");
        }

        // A given password is tried alone: password recovery calls this once per candidate, and also running
        // the whole known list each time would multiply the cost by its length.
        IEnumerable<string> candidates = string.IsNullOrEmpty(password) ? KnownPasswords.Read(knownPasswordsPath) : [password];
        foreach (string candidate in candidates)
        {
            if (TryDecrypt(raw, candidate) is (string text, TextShape inner))
            {
                return new SaveDocument(this, fileName, JsonText.Parse(text, fileName), new State(data, text, outer!, inner, candidate));
            }
        }

        throw new PasswordRequiredException(string.IsNullOrEmpty(password)
            ? "This save is encrypted. Enter its password to open it."
            : "Wrong password, or this is not a Save Game Free file.");
    }

    static (string Text, TextShape Inner)? TryDecrypt(byte[] raw, string password)
    {
        try
        {
            byte[] salt = raw[..SaltBytes];
            byte[] iv = raw[SaltBytes..(SaltBytes + IvBytes)];
            byte[] cipherText = raw[(SaltBytes + IvBytes)..];
            RijndaelCipher cipher = new(DeriveKey(password, salt), Block);

            // CBC's first block decrypts independently of the rest (plaintext = Decrypt(C1) XOR IV), and
            // the plaintext here is always base64 text. So a wrong password is almost always caught right
            // here, without paying for a full-buffer decrypt (this cipher is a hand-written, non-hardware
            // 256-bit-block Rijndael) on every one of possibly tens of thousands of password candidates.
            byte[] firstBlock = cipher.DecryptBlock(cipherText[..Block]);
            for (int i = 0; i < Block; i++)
            {
                firstBlock[i] ^= iv[i];
            }

            if (!firstBlock.All(b => char.IsAsciiLetterOrDigit((char)b) || b is (byte)'+' or (byte)'/' or (byte)'='))
            {
                return null;
            }

            byte[] plain = cipher.DecryptCbc(cipherText, iv);
            byte[] json = Convert.FromBase64String(Encoding.ASCII.GetString(plain));
            TextShape inner = TextShape.Detect(json, out int skip);
            string text = inner.Decode(json, skip);
            return text.TrimStart().StartsWith('{') ? (text, inner) : null;
        }
        catch (Exception e) when (e is SaveException or FormatException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    public byte[] Save(SaveDocument document)
    {
        State state = (State)document.State!;
        string text = JsonText.Write(state.Text, document.Root);
        if (text == state.Text)
        {
            return state.Original;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] iv = RandomNumberGenerator.GetBytes(IvBytes);
        byte[] inner = Encoding.ASCII.GetBytes(Convert.ToBase64String(state.Inner.Encode(text)));
        byte[] cipher = new RijndaelCipher(DeriveKey(state.Password, salt), Block).EncryptCbc(inner, iv);
        return state.Outer.Encode(Convert.ToBase64String([.. salt, .. iv, .. cipher]));
    }

    static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Rounds, HashAlgorithmName.SHA1, 32);

    // Cheap structural test: the file is base64 text that decodes to salt + IV + whole cipher blocks.
    static bool TryUnwrapOuter(ReadOnlySpan<byte> data, out byte[] raw, out TextShape? outer)
    {
        raw = [];
        outer = null;
        if (data.Length < 4 * (SaltBytes + IvBytes + Block) / 3)
        {
            return false;
        }

        TextShape shape = TextShape.Detect(data, out int skip);
        string text;
        try
        {
            text = shape.Decode(data, skip);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        foreach (char c in text.AsSpan(0, Math.Min(text.Length, 64)))
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '\r' or '\n'))
            {
                return false;
            }
        }

        byte[] buffer = new byte[text.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(text, buffer, out int written)
            || written < SaltBytes + IvBytes + Block
            || (written - SaltBytes - IvBytes) % Block != 0)
        {
            return false;
        }

        raw = buffer[..written];
        outer = shape;
        return true;
    }
}
