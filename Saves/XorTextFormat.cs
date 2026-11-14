using System.Globalization;
using System.Text;

namespace RiisSaveEditor.Saves;

// A common home-made Unity obfuscation: the save text XORed with one byte, then base64. The key is found by
// trying all 256 and keeping the one that turns the whole file into printable text, which random ciphertext
// (Save Game Free, ES3) never does. The text is either JSON or a "key=value|key=value" list.
public sealed class XorTextFormat : ISaveFormat
{
    sealed record State(byte[] Original, byte Key, string Text, bool Json);

    // Remembers how a list value was spelled so unchanged and boolean values keep the game's casing.
    sealed record Field(string Raw, bool Capitalized);

    public string Name => "Unity (XOR text)";

    public bool CanLoad(string fileName, ReadOnlySpan<byte> data) => TryDecode(data, out _, out _);

    public SaveDocument Load(string fileName, byte[] data, string? password = null)
    {
        if (!TryDecode(data, out byte key, out string? text))
        {
            throw new SaveException("Not an XOR text save.");
        }

        bool json = text![0] is '{' or '[';
        SaveNode root = json ? JsonText.Parse(text, fileName) : ParseList(text, fileName);
        return new SaveDocument(this, fileName, root, new State(data, key, text, json));
    }

    public byte[] Save(SaveDocument document)
    {
        State state = (State)document.State!;
        string text = state.Json ? JsonText.Write(state.Text, document.Root) : WriteList(document.Root);
        if (text == state.Text)
        {
            return state.Original;
        }

        byte[] bytes = Encoding.Latin1.GetBytes(text);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= state.Key;
        }

        return Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes));
    }

    static bool TryDecode(ReadOnlySpan<byte> data, out byte key, out string? text)
    {
        key = 0;
        text = null;
        ReadOnlySpan<byte> trimmed = data.TrimEnd("\r\n "u8);
        if (trimmed.Length < 8 || trimmed.Length % 4 != 0)
        {
            return false;
        }

        foreach (byte b in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit((char)b) || b is (byte)'+' or (byte)'/' or (byte)'='))
            {
                return false;
            }
        }

        byte[] raw = new byte[trimmed.Length / 4 * 3];
        if (System.Buffers.Text.Base64.DecodeFromUtf8(trimmed, raw, out _, out int written) != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        raw = raw[..written];

        // Several keys can make the same bytes printable (k and k ^ 0x08 often do), so the most text-like
        // result (most lowercase letters) that also has the expected structure wins.
        int bestScore = -1;
        for (int k = 0; k < 256; k++)
        {
            if (!raw.All(b => (b ^ k) is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D))
            {
                continue;
            }

            string candidate = Encoding.Latin1.GetString([.. raw.Select(b => (byte)(b ^ k))]);
            int score = candidate.Count(char.IsAsciiLetterLower);
            if (score > bestScore && HasStructure(candidate))
            {
                bestScore = score;
                key = (byte)k;
                text = candidate;
            }
        }

        return text is not null;
    }

    static bool HasStructure(string candidate)
    {
        if (candidate.TrimStart() is ['{' or '[', ..])
        {
            try
            {
                JsonText.Parse(candidate, "");
                return true;
            }
            catch (SaveException)
            {
                return false;
            }
        }

        string[] fields = candidate.Split('|');
        return fields.Length >= 2 && fields.Any(f => f.Contains('=')) && fields.All(f =>
        {
            int eq = f.IndexOf('=');
            string name = eq < 0 ? f : f[..eq];
            return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        });
    }

    // Fields without '=' are kept as read-only names so they are written back as they were.
    static SaveNode ParseList(string text, string rootName)
    {
        SaveNode root = new(rootName, NodeKind.Object);
        foreach (string part in text.Split('|'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                root.Children.Add(new SaveNode(part, NodeKind.Null) { ReadOnly = true, Meta = new Field(part, false) });
                continue;
            }

            string name = part[..eq];
            string value = part[(eq + 1)..];
            SaveNode node = value switch
            {
                "True" or "False" or "true" or "false" => new SaveNode(name, NodeKind.Bool, value.ToLowerInvariant()) { Meta = new Field(value, char.IsUpper(value[0])) },
                _ when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) && !value.StartsWith('+')
                    => new SaveNode(name, NodeKind.Integer, value) { Meta = new Field(value, false), Range = new IntRange(int.MinValue, int.MaxValue) },
                _ => new SaveNode(name, NodeKind.String, value) { Meta = new Field(value, false) },
            };
            root.Children.Add(node);
        }

        return root;
    }

    static string WriteList(SaveNode root) => string.Join('|', root.Items.Select(n =>
    {
        Field field = (Field)n.Meta!;
        if (n.Kind == NodeKind.Null)
        {
            return field.Raw;
        }

        string value = n.Kind == NodeKind.Bool
            ? (n.Value == field.Raw.ToLowerInvariant() ? field.Raw : field.Capitalized ? (n.Value == "true" ? "True" : "False") : n.Value!)
            : n.Value ?? "";
        if (value.Contains('|') || value.Any(c => c > 0xFF))
        {
            throw new SaveException($"{n.Name} cannot contain '|' or characters outside Latin-1 in this save format.");
        }

        return n.Name + "=" + value;
    }));
}
