using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RiisSaveEditor.Saves
{
	// Easy Save 3 files are JSON, optionally gzipped and then AES-encrypted. The encryption layer is
// AES-128-CBC with the key derived from the game's password (PBKDF2-SHA1, 100 iterations) and the
// 16 byte IV stored at the start of the file doubling as the PBKDF2 salt. The JSON layer goes through
// JsonText, so only edited values are rewritten and an untouched file is returned byte-for-byte.
	public sealed class UnityEs3Format(string? knownPasswordsPath = null) : ISaveFormat
	{
		private sealed record State(byte[] Original, bool Bom, string Text, string? Password, bool Gzip);

		private static readonly byte[] BomBytes = [0xEF, 0xBB, 0xBF];
		private static readonly UTF8Encoding Strict = new(false, true);

		public string Name => "Unity (Easy Save 3)";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data) =>
			LooksLikeJson(data) || IsGzip(data) || (data.Length >= 32 && data.Length % 16 == 0);

		// Without a typed password an encrypted file tries the operator's known passwords. A given password is
		// tried alone, so password recovery pays for one derivation per candidate.
		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			// The random IV of an encrypted file starts with '{' or the gzip magic about 1 time in 128, so a
			// plain read that fails on a cipher-shaped file falls through to decryption.
			if (LooksLikeJson(data) || IsGzip(data))
			{
				try
				{
					return LoadWith(fileName, data, null, encrypted: false);
				}
				catch (SaveException) when (data.Length >= 32 && data.Length % 16 == 0 && !System.Text.Unicode.Utf8.IsValid(data))
				{
				}
			}

			if (!string.IsNullOrEmpty(password))
			{
				return LoadWith(fileName, data, password, encrypted: true);
			}

			foreach (string known in KnownPasswords.Read(knownPasswordsPath))
			{
				try
				{
					return LoadWith(fileName, data, known, encrypted: true);
				}
				catch (PasswordRequiredException)
				{
				}
			}

			throw new PasswordRequiredException("This file looks encrypted. Enter its password to open it.");
		}

		private SaveDocument LoadWith(string fileName, byte[] data, string? password, bool encrypted)
		{
			byte[] bytes = data;
			string? usedPassword = null;
			bool gzip = false;

			if (encrypted)
			{
				bytes = Decrypt(data, password!);
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
			try
			{
				string text = Strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
				return new SaveDocument(this, fileName, JsonText.Parse(text, fileName), new State(data, bom, text, usedPassword, gzip));
			}
			catch (Exception e) when (e is SaveException or DecoderFallbackException)
			{
				// A wrong password can still decrypt to valid padding; garbage text then means the password, not the file.
				throw Failure(usedPassword) is PasswordRequiredException p ? p : new SaveException($"Invalid JSON: {e.Message}");
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

			byte[] body = Encoding.UTF8.GetBytes(text);
			return Wrap(state.Bom ? [.. BomBytes, .. body] : body, state.Password, state.Gzip);
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
	}
}
