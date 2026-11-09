using System.Text;

namespace RiisSaveEditor.Saves
{
	// Plain JSON saves such as Godot's JSON.stringify output. Only .json files are claimed so Easy Save
// files (which are also JSON) keep going to their own reader.
	public sealed class PlainJsonFormat : ISaveFormat
	{
		private sealed record State(bool Bom, string Text);

		private static readonly UTF8Encoding Strict = new(false, true);

		public string Name => "JSON (Godot and others)";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data)
		{
			if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			int i = data.StartsWith("﻿"u8) ? 3 : 0;
			while (i < data.Length && data[i] is 0x20 or 0x09 or 0x0D or 0x0A)
			{
				i++;
			}

			return i < data.Length && data[i] is (byte)'{' or (byte)'[';
		}

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			bool bom = data.AsSpan().StartsWith("﻿"u8);
			string text;
			try
			{
				text = Strict.GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0));
			}
			catch (ArgumentException)
			{
				throw new SaveException("The file is not valid UTF-8 text.");
			}

			return new SaveDocument(this, fileName, JsonText.Parse(text, fileName), new State(bom, text));
		}

		public byte[] Save(SaveDocument document)
		{
			State state = (State)document.State!;
			byte[] body = Encoding.UTF8.GetBytes(JsonText.Write(state.Text, document.Root));
			return state.Bom ? [.. "﻿"u8, .. body] : body;
		}
	}
}
