using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RiisSaveEditor.Saves
{
	// A Ren'Py save is a zip holding the pickled game log plus a signature over that log. Edited saves are
// re-signed with a key owned by this editor, so Ren'Py treats them as coming from another device and
// asks the player once to trust that key.
	public sealed class RenpyFormat : ISaveFormat
	{
		private sealed record State(List<KeyValuePair<string, byte[]>> Entries, PickleDocument Log);

		private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

		// Each resume code signs with its own key, kept in that code's cache folder. A shared key would let any
		// visitor of a public instance get a crafted save signed with the key other players already trusted.
		private static ECDsa KeyFor(string? keyPath)
		{
			ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			if (keyPath is null)
			{
				return key;
			}

			if (File.Exists(keyPath))
			{
				key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath)), out _);
				return key;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
			try
			{
				using FileStream file = new(keyPath, FileMode.CreateNew, FileAccess.Write);
				file.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(key.ExportPkcs8PrivateKey())));
			}
			catch (IOException)
			{
				// Another save created it first; use that one so both agree.
				key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath)), out _);
			}

			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			return key;
		}

		public string Name => "Ren'Py";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data)
		{
			if (!data.StartsWith(ZipMagic))
			{
				return false;
			}

			try
			{
				using ZipArchive zip = new(new MemoryStream(data.ToArray()), ZipArchiveMode.Read);
				return zip.GetEntry("log") is not null;
			}
			catch (InvalidDataException)
			{
				return false;
			}
		}

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			List<KeyValuePair<string, byte[]>> entries = ReadEntries(data);
			byte[] log = entries.First(e => e.Key == "log").Value;
			PickleDocument pickle = Pickle.Parse(log);
			pickle.Root.Name = fileName;
			return new SaveDocument(this, fileName, pickle.Root, new State(entries, pickle));
		}

		public byte[] Save(SaveDocument document)
		{
			State state = (State)document.State!;
			byte[] log = Pickle.Write(state.Log);
			using ECDsa key = KeyFor(document.SigningKeyPath);

			using MemoryStream output = new();
			using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
			{
				foreach (KeyValuePair<string, byte[]> entry in state.Entries)
				{
					byte[] content = entry.Key switch
					{
						"log" => log,
						"signatures" => Encoding.ASCII.GetBytes(Sign(log, key)),
						_ => entry.Value,
					};

					ZipArchiveEntry zipEntry = zip.CreateEntry(entry.Key, CompressionLevel.Optimal);
					using Stream s = zipEntry.Open();
					s.Write(content);
				}
			}

			return output.ToArray();
		}

		// Ren'Py's format: "signature <base64 SubjectPublicKeyInfo> <base64 raw r||s>".
		private static string Sign(byte[] log, ECDsa key)
		{
			byte[] sig = key.SignData(log, HashAlgorithmName.SHA1, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
			return $"signature {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())} {Convert.ToBase64String(sig)}";
		}

		private static List<KeyValuePair<string, byte[]>> ReadEntries(byte[] data)
		{
			using ZipArchive zip = new(new MemoryStream(data), ZipArchiveMode.Read);
			List<KeyValuePair<string, byte[]>> entries = [];
			long budget = SafeIo.MaxExpandedBytes;
			foreach (ZipArchiveEntry e in zip.Entries)
			{
				using Stream s = e.Open();
				byte[] content = SafeIo.ReadAll(s, budget);
				budget -= content.Length;
				entries.Add(new KeyValuePair<string, byte[]>(e.FullName, content));
			}

			return entries;
		}

		public static bool SameExceptSignature(byte[] a, byte[] b)
		{
			List<KeyValuePair<string, byte[]>> x = ReadEntries(a);
			List<KeyValuePair<string, byte[]>> y = ReadEntries(b);
			return x.Count == y.Count && x.Zip(y).All(p =>
				p.First.Key == p.Second.Key && (p.First.Key == "signatures" || p.First.Value.AsSpan().SequenceEqual(p.Second.Value)));
		}

		// Used by the checks project to confirm which hash the real signatures use.
		public static string DescribeSignatureScheme(string path)
		{
			List<KeyValuePair<string, byte[]>> entries = ReadEntries(File.ReadAllBytes(path));
			byte[] log = entries.First(e => e.Key == "log").Value;
			string[] parts = Encoding.ASCII.GetString(entries.First(e => e.Key == "signatures").Value).Split(' ');
			using ECDsa pub = ECDsa.Create();
			pub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(parts[1]), out _);
			byte[] sig = Convert.FromBase64String(parts[2]);

			List<string> valid = [];
			foreach (HashAlgorithmName hash in new[] { HashAlgorithmName.SHA1, HashAlgorithmName.SHA256 })
			{
				if (pub.VerifyData(log, sig, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
				{
					valid.Add(hash.Name!);
				}
			}

			return valid.Count == 0 ? "no scheme verified" : "verified with " + string.Join(", ", valid);
		}
	}

// The `persistent` file next to the saves is a zlib-compressed pickle of the game's persistent data.
	public sealed class RenpyPersistentFormat : ISaveFormat
	{
		public string Name => "Ren'Py persistent data";

		public bool CanLoad(string fileName, ReadOnlySpan<byte> data)
		{
			if (data.Length < 8 || data[0] != 0x78 || data[1] is not (0x01 or 0x5E or 0x9C or 0xDA))
			{
				return false;
			}

			try
			{
				using ZLibStream z = new(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
				byte[] head = new byte[2];
				return z.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2 && head[0] == 0x80;
			}
			catch (InvalidDataException)
			{
				return false;
			}
		}

		public SaveDocument Load(string fileName, byte[] data, string? password = null)
		{
			using ZLibStream z = new(new MemoryStream(data), CompressionMode.Decompress);
			PickleDocument pickle = Pickle.Parse(SafeIo.ReadAll(z));
			pickle.Root.Name = fileName;
			return new SaveDocument(this, fileName, pickle.Root, pickle);
		}

		public static bool SamePayload(byte[] a, byte[] b)
		{
			return Inflate(a).AsSpan().SequenceEqual(Inflate(b));

			static byte[] Inflate(byte[] data)
			{
				using ZLibStream z = new(new MemoryStream(data), CompressionMode.Decompress);
				return SafeIo.ReadAll(z);
			}
		}

		public byte[] Save(SaveDocument document)
		{
			byte[] plain = Pickle.Write((PickleDocument)document.State!);
			using MemoryStream output = new();
			using (ZLibStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
			{
				z.Write(plain);
			}

			return output.ToArray();
		}
	}
}
