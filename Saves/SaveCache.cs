using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RiisSaveEditor.Saves
{
	public sealed record CacheEntry(
		string Id,
		string FileName,
		string Format,
		long Size,
		string Hash,
		DateTimeOffset UploadedUtc,
		DateTimeOffset LastUsedUtc,
		bool Edited,
		string Sketch)
	{
		public DateTimeOffset ExpiresUtc => LastUsedUtc + SaveCache.Retention;
	}

	// Raised when the whole cache is at its cap; the message is safe to show to the user.
	public sealed class CacheFullException(string message) : IOException(message);

// Keeps uploaded saves on disk so a later upload of the same game can be compared automatically.
// People are not identified: a resume code (a random GUID kept in the browser) is hashed into the folder name,
// so whoever holds the code holds the cache. Files expire 30 days after they were last used.
	public sealed class SaveCache(
		string root,
		TimeProvider clock,
		long maxBytesPerCode = 200L * 1024 * 1024,
		long maxTotalBytes = 5L * 1024 * 1024 * 1024)
	{
		public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

		private const int MaxPerGame = 5;
		private const int SketchSize = 256;
		private const double SameGame = 0.5;

		private readonly SemaphoreSlim _gate = new(1, 1);

		// A random (version 4) GUID: 122 random bits, so a code cannot be guessed or searched for.
		public static string NewCode() => Guid.NewGuid().ToString("D");

		// Braces, missing hyphens and capitals all mean the same code.
		public static string Normalize(string code) =>
			Guid.TryParse(code.Trim(), out Guid guid) ? guid.ToString("D") : code.Trim();

		// Names come from the browser. They are only ever stored as data and shown escaped, but keep them short
		// and free of path separators and control characters anyway.
		public static string SafeFileName(string name)
		{
			string leaf = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
			string clean = new([.. leaf.Select(c => char.IsControl(c) || c == ':' ? '_' : c)]);
			return clean.Length == 0 ? "save" : clean.Length > 120 ? clean[..120] : clean;
		}

		public string KeyPathFor(string code) => Path.Combine(DirectoryFor(code), "renpy-signing.key");

		public static string? CodeProblem(string? code) =>
			Guid.TryParse(code?.Trim(), out Guid guid) && guid != Guid.Empty
				? null
				: "Enter a valid code. It looks like 123e4567-e89b-12d3-a456-426614174000.";

		public static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

		public static string Ago(DateTimeOffset when, DateTimeOffset now)
		{
			TimeSpan age = now - when;
			return age.TotalMinutes < 1 ? "just now"
				: age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
				: age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
				: $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
		}

		public async Task<CacheEntry> StoreAsync(string code, SaveDocument doc, byte[] data, bool edited, CancellationToken ct = default)
		{
			string dir = DirectoryFor(code);
			string hash = HashOf(data);
			string sketch = EncodeSketch(Sketch(doc.Root));

			await _gate.WaitAsync(ct);
			try
			{
				DateTimeOffset now = clock.GetUtcNow();
				Directory.CreateDirectory(dir);
				List<CacheEntry> entries = await LoadAllAsync(dir, now, ct);

				CacheEntry? existing = entries.FirstOrDefault(e => e.Hash == hash);
				if (existing is not null)
				{
					existing = existing with { LastUsedUtc = now };
					await WriteMetaAsync(dir, existing, ct);
					return existing;
				}

				// The hourly sweep trims the cache, but anyone can mint codes, so the overall cap is also checked on
				// every write; otherwise the disk could be filled between two sweeps.
				if (UsedBytes() + data.Length > maxTotalBytes)
				{
					throw new CacheFullException("The server's saved-files storage is full, so this file was not kept for comparison.");
				}

				string id = Guid.NewGuid().ToString("N");
				CacheEntry entry = new(id, SafeFileName(doc.FileName), doc.Format.Name, data.Length, hash, now, now, edited, sketch);
				await WriteAtomicAsync(Path.Combine(dir, id + ".bin"), data, ct);
				await WriteMetaAsync(dir, entry, ct);
				entries.Add(entry);
				EnforceLimits(dir, entries, entry);
				return entry;
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task<IReadOnlyList<CacheEntry>> ListAsync(string code, CancellationToken ct = default)
		{
			string dir = DirectoryFor(code);
			await _gate.WaitAsync(ct);
			try
			{
				return Directory.Exists(dir)
					? [.. (await LoadAllAsync(dir, clock.GetUtcNow(), ct)).OrderByDescending(e => e.LastUsedUtc)]
					: [];
			}
			finally
			{
				_gate.Release();
			}
		}

		// Earlier saves of the same game, best match first. Identical bytes are skipped so re-uploading
		// a file never compares it with itself.
		public async Task<IReadOnlyList<CacheEntry>> SimilarAsync(string code, SaveDocument doc, string? excludeHash = null, CancellationToken ct = default)
		{
			ulong[] sketch = Sketch(doc.Root);
			IReadOnlyList<CacheEntry> all = await ListAsync(code, ct);
			return
			[
				.. all
					.Where(e => e.Format == doc.Format.Name && e.Hash != excludeHash)
					.Select(e => (Entry: e, Score: Similarity(sketch, DecodeSketch(e.Sketch))))
					.Where(x => x.Score >= SameGame)
					.OrderByDescending(x => Math.Round(x.Score, 1))
					.ThenByDescending(x => x.Entry.UploadedUtc)
					.Select(x => x.Entry),
			];
		}

		// Reading an entry counts as using it, which pushes its expiry back.
		public async Task<byte[]?> ReadAsync(string code, string id, CancellationToken ct = default)
		{
			if (!Guid.TryParseExact(id, "N", out _))
			{
				return null;
			}

			string dir = DirectoryFor(code);
			await _gate.WaitAsync(ct);
			try
			{
				DateTimeOffset now = clock.GetUtcNow();
				CacheEntry? entry = (await LoadAllAsync(dir, now, ct)).FirstOrDefault(e => e.Id == id);
				string path = Path.Combine(dir, id + ".bin");
				if (entry is null || !File.Exists(path))
				{
					return null;
				}

				await WriteMetaAsync(dir, entry with { LastUsedUtc = now }, ct);
				return await File.ReadAllBytesAsync(path, ct);
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task DeleteAsync(string code, string id, CancellationToken ct = default)
		{
			if (!Guid.TryParseExact(id, "N", out _))
			{
				return;
			}

			await _gate.WaitAsync(ct);
			try
			{
				DeleteEntry(DirectoryFor(code), id);
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task DeleteAllAsync(string code, CancellationToken ct = default)
		{
			string dir = DirectoryFor(code);
			await _gate.WaitAsync(ct);
			try
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
			finally
			{
				_gate.Release();
			}
		}

		// Removes expired files everywhere, then trims the oldest-used files if the whole cache is too big.
		public async Task<int> SweepAsync(CancellationToken ct = default)
		{
			if (!Directory.Exists(root))
			{
				return 0;
			}

			await _gate.WaitAsync(ct);
			try
			{
				DateTimeOffset now = clock.GetUtcNow();
				int removed = 0;
				List<(string Dir, CacheEntry Entry)> all = [];
				foreach (string dir in Directory.EnumerateDirectories(root))
				{
					int before = Directory.EnumerateFiles(dir, "*.json").Count();
					List<CacheEntry> live = await LoadAllAsync(dir, now, ct);
					removed += before - live.Count;
					all.AddRange(live.Select(e => (dir, e)));
					if (live.Count == 0)
					{
						Directory.Delete(dir, recursive: true);
					}
				}

				long total = all.Sum(x => x.Entry.Size);
				foreach ((string dir, CacheEntry entry) in all.OrderBy(x => x.Entry.LastUsedUtc))
				{
					if (total <= maxTotalBytes)
					{
						break;
					}

					DeleteEntry(dir, entry.Id);
					total -= entry.Size;
					removed++;
				}

				return removed;
			}
			finally
			{
				_gate.Release();
			}
		}

		// Names down to two levels below the root identify a game: variable names, property names, keys.
		// Only the smallest hashes are kept, which still allows a fair estimate of overlap.
		public static ulong[] Sketch(SaveNode root)
		{
			HashSet<ulong> hashes = [];
			foreach (SaveNode first in root.Items)
			{
				hashes.Add(Fnv(first.Name));
				foreach (SaveNode second in first.Items)
				{
					hashes.Add(Fnv(second.Name));
				}
			}

			ulong[] sorted = [.. hashes];
			Array.Sort(sorted);
			return sorted.Length > SketchSize ? sorted[..SketchSize] : sorted;
		}

		// Jaccard overlap, measured only below the hash threshold both sketches fully cover.
		public static double Similarity(ulong[] a, ulong[] b)
		{
			ulong limit = Math.Min(a.Length == SketchSize ? a[^1] : ulong.MaxValue, b.Length == SketchSize ? b[^1] : ulong.MaxValue);
			int i = 0, j = 0, both = 0, union = 0;
			while (true)
			{
				bool hasA = i < a.Length && a[i] <= limit;
				bool hasB = j < b.Length && b[j] <= limit;
				if (!hasA && !hasB)
				{
					break;
				}

				union++;
				if (hasA && hasB && a[i] == b[j])
				{
					both++;
					i++;
					j++;
				}
				else if (!hasB || (hasA && a[i] < b[j]))
				{
					i++;
				}
				else
				{
					j++;
				}
			}

			return union == 0 ? 0 : (double)both / union;
		}

		private string DirectoryFor(string code)
		{
			if (CodeProblem(code) is { } problem)
			{
				throw new ArgumentException(problem, nameof(code));
			}

			byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("riis-save-cache:v1:" + Normalize(code)));
			return Path.Combine(root, Convert.ToHexString(hash)[..32]);
		}

		private long UsedBytes() =>
			Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

		private void EnforceLimits(string dir, List<CacheEntry> entries, CacheEntry newest)
		{
			ulong[] sketch = DecodeSketch(newest.Sketch);
			IEnumerable<CacheEntry> sameGame = entries
				.Where(e => e.Format == newest.Format && Similarity(sketch, DecodeSketch(e.Sketch)) >= SameGame)
				.OrderByDescending(e => e.UploadedUtc)
				.Skip(MaxPerGame);
			foreach (CacheEntry old in sameGame.ToList())
			{
				DeleteEntry(dir, old.Id);
				entries.Remove(old);
			}

			long total = entries.Sum(e => e.Size);
			foreach (CacheEntry old in entries.Where(e => e.Id != newest.Id).OrderBy(e => e.LastUsedUtc).ToList())
			{
				if (total <= maxBytesPerCode)
				{
					break;
				}

				DeleteEntry(dir, old.Id);
				entries.Remove(old);
				total -= old.Size;
			}
		}

		private static async Task<List<CacheEntry>> LoadAllAsync(string dir, DateTimeOffset now, CancellationToken ct)
		{
			List<CacheEntry> entries = [];
			if (!Directory.Exists(dir))
			{
				return entries;
			}

			foreach (string meta in Directory.EnumerateFiles(dir, "*.json"))
			{
				CacheEntry? entry = null;
				try
				{
					entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllBytesAsync(meta, ct));
				}
				catch (JsonException)
				{
				}

				if (entry is null || now - entry.LastUsedUtc > Retention)
				{
					DeleteEntry(dir, Path.GetFileNameWithoutExtension(meta));
					continue;
				}

				entries.Add(entry);
			}

			return entries;
		}

		private static void DeleteEntry(string dir, string id)
		{
			File.Delete(Path.Combine(dir, id + ".bin"));
			File.Delete(Path.Combine(dir, id + ".json"));
		}

		private static Task WriteMetaAsync(string dir, CacheEntry entry, CancellationToken ct = default) =>
			WriteAtomicAsync(Path.Combine(dir, entry.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(entry), ct);

		private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
		{
			string temp = path + ".tmp";
			await File.WriteAllBytesAsync(temp, data, ct);
			File.Move(temp, path, overwrite: true);
		}

		private static string EncodeSketch(ulong[] sketch)
		{
			byte[] bytes = new byte[sketch.Length * 8];
			for (int i = 0; i < sketch.Length; i++)
			{
				BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), sketch[i]);
			}

			return Convert.ToBase64String(bytes);
		}

		private static ulong[] DecodeSketch(string encoded)
		{
			byte[] bytes = Convert.FromBase64String(encoded);
			ulong[] sketch = new ulong[bytes.Length / 8];
			for (int i = 0; i < sketch.Length; i++)
			{
				sketch[i] = BitConverter.ToUInt64(bytes, i * 8);
			}

			return sketch;
		}

		// FNV-1a, because string.GetHashCode changes between runs and these hashes are stored.
		private static ulong Fnv(string text)
		{
			ulong hash = 14695981039346656037;
			foreach (byte b in Encoding.UTF8.GetBytes(text))
			{
				hash = (hash ^ b) * 1099511628211;
			}

			return hash;
		}
	}
}
