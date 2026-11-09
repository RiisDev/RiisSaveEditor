using System.IO.Compression;
using System.Text;

namespace RiisSaveEditor.Saves
{
	// RPG Maker MZ (.rmmzsave): the save JSON is zlib-deflated by pako into a "binary string", and
// Node then writes that string as UTF-8, so every compressed byte above 0x7F became two bytes.
// Loading reverses both steps. An untouched file is returned byte-for-byte; an edited one is
// recompressed, which the game's inflate accepts although the compressed bytes differ.
	public sealed class RpgMakerFormat : ISaveFormat
	{
		private sealed record State(byte[] Original, string Text);

		private static readonly UTF8Encoding Strict = new(false, true);

		public string Name => "RPG Maker MZ";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data) =>
			fileName.EndsWith(".rmmzsave", StringComparison.OrdinalIgnoreCase);

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			try
			{
				string packed = Strict.GetString(data);
				byte[] deflated = new byte[packed.Length];
				for (int i = 0; i < packed.Length; i++)
				{
					if (packed[i] > 0xFF)
					{
						throw new SaveException("Not an RPG Maker MZ save: the compressed data is not byte-sized text.");
					}

					deflated[i] = (byte)packed[i];
				}

				using ZLibStream zlib = new(new MemoryStream(deflated), CompressionMode.Decompress);
				string text = Strict.GetString(SafeIo.ReadAll(zlib));
				return new SaveDocument(this, fileName, JsonText.Parse(text, fileName), new State(data, text));
			}
			catch (Exception e) when (e is ArgumentException or InvalidDataException)
			{
				throw new SaveException("Not an RPG Maker MZ save: the file could not be decompressed.");
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

			using MemoryStream packed = new();
			using (ZLibStream zlib = new(packed, CompressionLevel.Fastest, leaveOpen: true))
			{
				zlib.Write(Encoding.UTF8.GetBytes(text));
			}

			return Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(packed.ToArray()));
		}
	}
}
