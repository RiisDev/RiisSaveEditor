using System.Text;
using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks
{
	internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
	{
		private DateTimeOffset _now = start;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}

	// The cache checks run on synthetic saves built here, so they need no real game files.
	internal static class CacheChecks
	{
		// A save of an imaginary game: `fields` integer settings plus an inventory object. Games with
		// different prefixes share no names, which is how the cache tells games apart.
		private static byte[] Game(string prefix, int fields = 40)
		{
			StringBuilder json = new("{");
			for (int i = 0; i < fields; i++)
			{
				json.Append($"\"{prefix}_stat_{i}\":{i},");
			}

			json.Append($"\"{prefix}_inventory\":{{\"{prefix}_item_a\":1,\"{prefix}_item_b\":2,\"{prefix}_item_c\":3}}}}");
			return Encoding.UTF8.GetBytes(json.ToString());
		}

		public static async Task<int> RunAsync(SaveFormatRegistry registry)
		{
			int failures = 0;

			string dir = Path.Combine(Path.GetTempPath(), "riis-cache-check-" + Guid.NewGuid().ToString("N"));
			FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
			SaveCache cache = new(dir, clock);

			string alice = SaveCache.NewCode();
			string bob = SaveCache.NewCode();

			Check("generated codes are GUIDs and different", Guid.TryParse(alice, out Guid g) && g != Guid.Empty && alice != bob && SaveCache.CodeProblem(alice) is null, alice);
			Check("guessable codes are rejected", new[] { "abc", "my-own-secret-code", "12345678", "", Guid.Empty.ToString() }.All(x => SaveCache.CodeProblem(x) is not null));

			(SaveDocument doc, byte[] data) = Load("slot.es3", Game("alpha"));
			CacheEntry first = await cache.StoreAsync(alice, doc, data, edited: false);
			Check("another code sees nothing", (await cache.ListAsync(bob)).Count == 0);
			Check("another code cannot read by id", await cache.ReadAsync(bob, first.Id) is null);
			Check("one GUID written in any common form is the same code", (await cache.ListAsync("  " + alice.ToUpperInvariant() + " ")).Count == 1 && (await cache.ListAsync(Guid.Parse(alice).ToString("N"))).Count == 1 && (await cache.ListAsync("{" + alice + "}")).Count == 1);
			Check("the code never appears on disk", !Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
				.Any(p => p.Contains(alice, StringComparison.OrdinalIgnoreCase)) && !File.ReadAllText(Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)[0]).Contains(alice));

			CacheEntry again = await cache.StoreAsync(alice, doc, data, edited: false);
			Check("identical upload is de-duplicated", again.Id == first.Id && (await cache.ListAsync(alice)).Count == 1);

			(SaveDocument edited, byte[] editedBytes) = Load("slot.es3", Variant("slot.es3", Game("alpha"), "77"));
			IReadOnlyList<CacheEntry> match = await cache.SimilarAsync(alice, edited, SaveCache.HashOf(editedBytes));
			Check("a later save of the same game finds its earlier save", match.Count == 1 && match[0].Id == first.Id);

			(SaveDocument other, _) = Load("slot.es3", Game("beta"));
			Check("a different game does not match", (await cache.SimilarAsync(alice, other)).Count == 0);

			(SaveDocument sameBytes, _) = Load("slot.es3", Game("alpha"));
			Check("the same bytes are never their own baseline", (await cache.SimilarAsync(alice, sameBytes, SaveCache.HashOf(data))).Count == 0);

			// Same names as the cached game, but a different save format, so it must not be offered.
			SaveDocument otherEngine = registry.Load("slot.json", Game("alpha"));
			Check("a different engine does not match", (await cache.SimilarAsync(alice, otherEngine)).Count == 0);

			clock.Advance(TimeSpan.FromMinutes(1));
			CacheEntry editedEntry = await cache.StoreAsync(alice, edited, editedBytes, edited: true);
			(SaveDocument next, byte[] nextBytes) = Load("slot.es3", Variant("slot.es3", Game("alpha"), "78"));
			IReadOnlyList<CacheEntry> pick = await cache.SimilarAsync(alice, next, SaveCache.HashOf(nextBytes));
			Check("the newest matching snapshot is the default baseline", pick.Count == 2 && pick[0].Id == editedEntry.Id && pick[0].Edited);

			for (int i = 0; i < 8; i++)
			{
				(SaveDocument d, byte[] b) = Load("slot.es3", Variant("slot.es3", Game("alpha"), (100 + i).ToString()));
				clock.Advance(TimeSpan.FromMinutes(1));
				await cache.StoreAsync(alice, d, b, edited: false);
			}

			Check("at most 5 snapshots are kept per game", (await cache.ListAsync(alice)).Count == 5, (await cache.ListAsync(alice)).Count.ToString());

			// Sliding expiry: only use pushes the expiry back.
			string carol = SaveCache.NewCode();
			(SaveDocument cdoc, byte[] cdata) = Load("slot.es3", Game("gamma"));
			CacheEntry stored = await cache.StoreAsync(carol, cdoc, cdata, edited: false);
			clock.Advance(TimeSpan.FromDays(29));
			Check("still there after 29 days", (await cache.ListAsync(carol)).Count == 1);
			Check("reading returns the bytes", (await cache.ReadAsync(carol, stored.Id))?.Length == cdata.Length);
			clock.Advance(TimeSpan.FromDays(29));
			Check("using it renewed the expiry", (await cache.ListAsync(carol)).Count == 1);
			clock.Advance(TimeSpan.FromDays(2));
			Check("expires 30 days after last use", (await cache.ListAsync(carol)).Count == 0 && await cache.ReadAsync(carol, stored.Id) is null);

			// Sweep removes what expired and keeps what did not.
			string dave = SaveCache.NewCode();
			(SaveDocument ddoc, byte[] ddata) = Load("slot.es3", Game("delta"));
			await cache.StoreAsync(dave, ddoc, ddata, edited: false);
			clock.Advance(TimeSpan.FromDays(31));
			(SaveDocument edoc, byte[] edata) = Load("slot.es3", Game("epsilon"));
			await cache.StoreAsync(SaveCache.NewCode(), edoc, edata, edited: false);
			int removed = await cache.SweepAsync();
			Check("sweep removes expired files and keeps fresh ones", removed >= 1 && (await cache.ListAsync(dave)).Count == 0
				&& Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories).Count() == 1, $"removed {removed}");

			// Per-code size limit evicts the least recently used file.
			SaveCache small = new(Path.Combine(dir, "small"), clock, maxBytesPerCode: 20_000);
			string erin = SaveCache.NewCode();
			foreach (string prefix in new[] { "one", "two", "three" })
			{
				(SaveDocument d, byte[] b) = Load(prefix + ".es3", Game(prefix, fields: 400));
				clock.Advance(TimeSpan.FromMinutes(1));
				await small.StoreAsync(erin, d, b, edited: false);
			}

			IReadOnlyList<CacheEntry> kept = await small.ListAsync(erin);
			Check("per-code size limit evicts the oldest", kept.Sum(e => e.Size) <= 20_000 && kept.Any(e => e.FileName == "three.es3") && kept.Count < 3,
				string.Join(",", kept.Select(e => e.FileName)));

			// Passwords typed for encrypted saves are remembered per code, newest first, and bounded.
			(SaveDocument zdoc, byte[] zdata) = Load("slot.es3", Game("zeta"));
			await cache.StoreAsync(alice, zdoc, zdata, edited: false);
			await cache.AddPasswordAsync(alice, "first");
			await cache.AddPasswordAsync(alice, "second");
			await cache.AddPasswordAsync(alice, "first");
			await cache.AddPasswordAsync(alice, "bad\npassword");
			await cache.AddPasswordAsync(alice, new string('x', 500));
			await cache.AddPasswordAsync(alice, "");
			Check("a password is remembered once, newest first", (await cache.PasswordsAsync(alice)).SequenceEqual(["first", "second"]));
			Check("another code does not see it", (await cache.PasswordsAsync(bob)).Count == 0);
			for (int i = 0; i < 30; i++)
			{
				await cache.AddPasswordAsync(alice, "p" + i);
			}

			Check("at most 20 passwords are kept", (await cache.PasswordsAsync(alice)).Count == 20 && (await cache.PasswordsAsync(alice))[0] == "p29");
			await cache.ForgetPasswordsAsync(alice);
			Check("forgetting passwords keeps the saves", (await cache.PasswordsAsync(alice)).Count == 0 && (await cache.ListAsync(alice)).Count > 0);
			await cache.AddPasswordAsync(alice, "again");

			await cache.DeleteAllAsync(alice);
			Check("forgetting everything removes the passwords too", (await cache.PasswordsAsync(alice)).Count == 0);
			Check("forgetting removes everything for a code", (await cache.ListAsync(alice)).Count == 0);

			// Anyone can add to the shared list, so it stops growing at its cap.
			string knownPath = Path.Combine(dir, "known-passwords.txt");
			File.WriteAllText(knownPath, "# operator notes\n");
			SaveCache shared = new(Path.Combine(dir, "shared"), clock, knownPasswordsPath: knownPath);
			for (int i = 0; i < KnownPasswords.Max + 10; i++)
			{
				await shared.AddPasswordAsync(alice, "k" + i);
			}

			Check("the shared password list is capped", KnownPasswords.Read(knownPath).Count() == KnownPasswords.Max
				&& File.ReadLines(knownPath).Count(l => l.Length > 0 && l[0] != '#') == KnownPasswords.Max);

			Directory.Delete(dir, recursive: true);
			return failures;

			void Check(string name, bool ok, string? detail = null)
			{
				Console.WriteLine($"{(ok ? "OK  " : "FAIL")} cache: {name}{(ok || detail is null ? "" : " (" + detail + ")")}");
				failures += ok ? 0 : 1;
			}

			// Loads the save, raises its first editable integer to `value` and returns the saved bytes.
			byte[] Variant(string file, byte[] original, string value)
			{
				SaveDocument d = registry.Load(file, original);
				SaveNode target = SaveTree.Collect(d.Root, n => n.IsEditable && n.Kind == NodeKind.Integer).First();
				target.Value = value;
				return d.Format.Save(d);
			}

			(SaveDocument Doc, byte[] Data) Load(string file, byte[] bytes) => (registry.Load(file, bytes), bytes);
		}
	}
}
