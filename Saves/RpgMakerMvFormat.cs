using System.Text;

namespace RiisSaveEditor.Saves;

// RPG Maker MV (.rpgsave): the save JSON compressed with lz-string's compressToBase64. An untouched file is
// returned byte-for-byte; an edited one is recompressed, which is exactly what the game would write.
public sealed class RpgMakerMvFormat : ISaveFormat
{
    sealed record State(byte[] Original, string Text);

    public string Name => "RPG Maker MV";

    public bool CanLoad(string fileName, ReadOnlySpan<byte> data) =>
        fileName.EndsWith(".rpgsave", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".rpgsave.bak", StringComparison.OrdinalIgnoreCase);

    public SaveDocument Load(string fileName, byte[] data, string? password = null)
    {
        string text = LzString.DecompressFromBase64(Encoding.ASCII.GetString(data).TrimEnd());
        if (text.Length == 0)
        {
            throw new SaveException("Not an RPG Maker MV save: the file did not decompress.");
        }

        return new SaveDocument(this, fileName, JsonText.Parse(text, fileName), new State(data, text));
    }

    public byte[] Save(SaveDocument document)
    {
        State state = (State)document.State!;
        string text = JsonText.Write(state.Text, document.Root);
        if (text == state.Text)
        {
            return state.Original;
        }

        // Write with whichever lz-string version produced the original, so the file looks like the game's own.
        bool modern = LzString.CompressToBase64(state.Text) == Encoding.ASCII.GetString(state.Original).TrimEnd();
        return Encoding.ASCII.GetBytes(modern ? LzString.CompressToBase64(text) : LzString.CompressToBase64Legacy(text));
    }
}
