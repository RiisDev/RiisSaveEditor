using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RiisSaveEditor.Saves
{
	// Easy Save 3 files are JSON, optionally gzipped and then AES-encrypted. The encryption layer is
// AES-128-CBC with the key derived from the game's password (PBKDF2-SHA1, 100 iterations) and the
// 16 byte IV stored at the start of the file doubling as the PBKDF2 salt.
	public sealed class UnityEs3Format : ISaveFormat
	{
		private sealed record State(bool Bom, string Trailing, string? Password, bool Gzip);

		private static readonly byte[] BomBytes = [0xEF, 0xBB, 0xBF];

		public string Name => "Unity (Easy Save 3)";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data) =>
			LooksLikeJson(data) || IsGzip(data) || (data.Length >= 32 && data.Length % 16 == 0);

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			byte[] bytes = data;
			string? usedPassword = null;
			bool gzip = false;

			if (!LooksLikeJson(bytes) && !IsGzip(bytes))
			{
				if (string.IsNullOrEmpty(password))
				{
					throw new PasswordRequiredException("This file looks encrypted. Enter its password to open it.");
				}

				bytes = Decrypt(data, password);
				usedPassword = password;
			}

			if (IsGzip(bytes))
			{
				try
				{
					using GZipStream gz = new(new MemoryStream(bytes), CompressionMode.Decompress);
					bytes = SafeIo.ReadAll(gz);
					gzip = true;
				}
				catch (InvalidDataException)
				{
					throw Failure(usedPassword);
				}
			}

			if (!LooksLikeJson(bytes))
			{
				throw Failure(usedPassword);
			}

			bool bom = bytes.AsSpan().StartsWith(BomBytes);
			string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
			string trailing = text[text.TrimEnd().Length..];

			try
			{
				using JsonDocument json = JsonDocument.Parse(text);
				return new SaveDocument(this, fileName, ToNode(fileName, json.RootElement), new State(bom, trailing, usedPassword, gzip));
			}
			catch (JsonException e)
			{
				throw Failure(usedPassword) is PasswordRequiredException p ? p : new SaveException($"Invalid JSON: {e.Message}");
			}
		}

		public byte[] Save(SaveDocument document)
		{
			State state = (State)document.State!;
			StringBuilder sb = new();
			WriteNode(sb, document.Root, 0);
			sb.Append(state.Trailing);

			byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
			byte[] json = state.Bom ? [.. BomBytes, .. body] : body;
			return Wrap(json, state.Password, state.Gzip);
		}

		internal static byte[] Wrap(byte[] json, string? password, bool gzip)
		{
			byte[] bytes = json;
			if (gzip)
			{
				using MemoryStream compressed = new();
				using (GZipStream gz = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
				{
					gz.Write(bytes);
				}

				bytes = compressed.ToArray();
			}

			if (string.IsNullOrEmpty(password))
			{
				return bytes;
			}

			byte[] iv = RandomNumberGenerator.GetBytes(16);
			using Aes aes = Aes.Create();
			aes.Key = DeriveKey(password, iv);
			return [.. iv, .. aes.EncryptCbc(bytes, iv, PaddingMode.PKCS7)];
		}

		private static byte[] Decrypt(byte[] data, string password)
		{
			byte[] iv = data[..16];
			using Aes aes = Aes.Create();
			aes.Key = DeriveKey(password, iv);
			try
			{
				return aes.DecryptCbc(data.AsSpan(16), iv, PaddingMode.PKCS7);
			}
			catch (CryptographicException)
			{
				throw Failure(password);
			}
		}

		private static byte[] DeriveKey(string password, byte[] iv) =>
			Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), iv, 100, HashAlgorithmName.SHA1, 16);

		private static SaveException Failure(string? password) => password is null
			? new SaveException("Not a valid Easy Save 3 file")
			: new PasswordRequiredException("Wrong password, or this is not an Easy Save 3 file.");

		private static bool IsGzip(ReadOnlySpan<byte> d) => d.Length > 2 && d[0] == 0x1F && d[1] == 0x8B;

		private static bool LooksLikeJson(ReadOnlySpan<byte> data)
		{
			int i = data.StartsWith(BomBytes) ? 3 : 0;
			while (i < data.Length && data[i] is 0x20 or 0x09 or 0x0D or 0x0A)
			{
				i++;
			}

			return i < data.Length && data[i] == (byte)'{';
		}

		private static SaveNode ToNode(string name, JsonElement e)
		{
			switch (e.ValueKind)
			{
				case JsonValueKind.Object:
					SaveNode obj = new(name, NodeKind.Object);
					foreach (JsonProperty p in e.EnumerateObject())
					{
						obj.Children.Add(ToNode(p.Name, p.Value));
					}

					return obj;
				case JsonValueKind.Array:
					SaveNode arr = new(name, NodeKind.Array);
					foreach (JsonElement item in e.EnumerateArray())
					{
						arr.Children.Add(ToNode($"[{arr.Children.Count}]", item));
					}

					return arr;
				case JsonValueKind.String:
					return new SaveNode(name, NodeKind.String, e.GetString());
				case JsonValueKind.Number:
					string raw = e.GetRawText();
					bool integral = raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;
					return new SaveNode(name, integral ? NodeKind.Integer : NodeKind.Float, raw);
				case JsonValueKind.True:
					return new SaveNode(name, NodeKind.Bool, "true");
				case JsonValueKind.False:
					return new SaveNode(name, NodeKind.Bool, "false");
				case JsonValueKind.Undefined:
				case JsonValueKind.Null:
				default:
					return new SaveNode(name, NodeKind.Null);
			}
		}

		private static void WriteNode(StringBuilder sb, SaveNode n, int depth)
		{
			switch (n.Kind)
			{
				case NodeKind.Object:
					if (!n.HasChildren)
					{
						sb.Append("{}");
						return;
					}

					sb.Append("{\r\n");
					for (int i = 0; i < n.Children.Count; i++)
					{
						if (i > 0)
						{
							sb.Append(",\r\n");
						}

						sb.Append('\t', depth + 1);
						WriteString(sb, n.Children[i].Name);
						sb.Append(" : ");

						// ES3 writes the payload of a typed entry one level shallower than normal.
						bool payload = n.Children[i].Name == "value" && n.Children[0].Name == "__type";
						WriteNode(sb, n.Children[i], payload ? depth : depth + 1);
					}

					sb.Append("\r\n").Append('\t', depth).Append('}');
					return;
				case NodeKind.Array:
					if (!n.HasChildren)
					{
						sb.Append("[]");
						return;
					}

					sb.Append("[\r\n").Append('\t', depth + 1);
					for (int i = 0; i < n.Children.Count; i++)
					{
						if (i > 0)
						{
							sb.Append(',');
						}

						WriteNode(sb, n.Children[i], depth + 1);
					}

					sb.Append("\r\n").Append('\t', depth).Append(']');
					return;
				case NodeKind.String:
					WriteString(sb, n.Value ?? "");
					return;
				case NodeKind.Null:
					sb.Append("null");
					return;
				default:
					sb.Append(n.Value);
					return;
			}
		}

		// ES3 escapes the forward slash and leaves non-ASCII text as-is.
		private static void WriteString(StringBuilder sb, string s)
		{
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '/': sb.Append("\\/"); break;
					case '\b': sb.Append("\\b"); break;
					case '\f': sb.Append("\\f"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
					default: sb.Append(c); break;
				}
			}

			sb.Append('"');
		}
	}
}
