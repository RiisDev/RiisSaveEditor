using System.Diagnostics;
using System.Text;

namespace RiisSaveEditor.Saves;

// When a Unity save is encrypted and the player has no password, the game's own IL2CPP metadata
// (global-metadata.dat) or managed assembly (Assembly-CSharp.dll) usually contains the password as a
// plain string literal somewhere in its string table. This tries every extracted string as a candidate
// password against the save until one works, so the player never has to find the password by hand.
public static class PasswordRecovery
{
    const int MinLength = 4;
    const int MaxLength = 128;

    // ponytail: global cap, not per-game tuned, only to bound memory on a pathological file. A real
    // global-metadata.dat has landed its password past 100,000 raw string occurrences (most of them
    // duplicates or too short to be a candidate), so this must stay well above that.
    const int MaxCandidates = 2_000_000;

    // IL2CPP stores string literals as raw UTF-8/ASCII bytes in global-metadata.dat, and .NET stores the
    // "user strings" heap of a managed DLL as UTF-16LE, but neither layout needs to be parsed exactly:
    // the password text shows up as a run of printable characters either way, so this just scans for runs
    // of plain ASCII and of UTF-16LE text, the same approach the Unix `strings` tool uses. That is far
    // simpler than, and robust across Unity/.NET versions that, a precise section-offset reader would be.
    public static IEnumerable<string> ExtractCandidateStrings(byte[] metadataFile)
    {
        HashSet<string> seen = [];

        foreach (string candidate in ScanAscii(metadataFile).Concat(ScanUtf16Le(metadataFile)))
        {
            if (candidate.Length < MinLength || candidate.Length > MaxLength || !seen.Add(candidate))
            {
                continue;
            }

            yield return candidate;

            if (seen.Count >= MaxCandidates)
            {
                yield break;
            }
        }
    }

    static bool IsPrintableAscii(byte b) => b is >= 0x20 and <= 0x7E;

    static IEnumerable<string> ScanAscii(byte[] data)
    {
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && IsPrintableAscii(data[i]);
            if (printable && start < 0)
            {
                start = i;
            }
            else if (!printable && start >= 0)
            {
                yield return Encoding.ASCII.GetString(data, start, i - start);
                start = -1;
            }
        }
    }

    // .NET string literals are UTF-16LE: each character is a printable ASCII byte followed by a zero byte.
    // Scanned at both even and odd alignment since the heap is not guaranteed to start on an even offset.
    static IEnumerable<string> ScanUtf16Le(byte[] data)
    {
        for (int align = 0; align < 2; align++)
        {
            int startChar = -1;
            int count = 0;
            int limit = (data.Length - align) / 2 * 2 + align;
            for (int i = align; i < limit; i += 2)
            {
                bool printable = i + 1 < data.Length && IsPrintableAscii(data[i]) && data[i + 1] == 0;
                if (printable)
                {
                    if (startChar < 0)
                    {
                        startChar = i;
                        count = 0;
                    }

                    count++;
                }
                else if (startChar >= 0)
                {
                    yield return Encoding.Unicode.GetString(data, startChar, count * 2);
                    startChar = -1;
                }
            }

            if (startChar >= 0)
            {
                yield return Encoding.Unicode.GetString(data, startChar, count * 2);
            }
        }
    }

    // Tries every candidate string as a password via tryLoad and returns the first that opens the save.
    // tryLoad should attempt ISaveFormat.Load with the candidate and return true on success. Each attempt
    // is a PBKDF2 derivation plus a decrypt, so with tens of thousands of candidates this is run in
    // parallel across all cores rather than one candidate at a time. onProgress, if given, is reported
    // once immediately (0, total) as soon as extraction finishes, so a UI has something real to show
    // before the brute force even starts, then on a wall-clock cadence (not a candidate count, since a
    // fixed count jumps unevenly across machines of different speeds) so it reads as live progress.
    public static string? Recover(byte[] metadataFile, Func<string, bool> tryLoad, Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        List<string> candidates = [.. ExtractCandidateStrings(metadataFile)];
        ct.ThrowIfCancellationRequested();
        int total = candidates.Count;
        int tried = 0;
        string? found = null;
        onProgress?.Invoke(0, total);

        Stopwatch clock = Stopwatch.StartNew();
        long lastReportMs = -1;

        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct }, (candidate, loop) =>
        {
            if (tryLoad(candidate))
            {
                Interlocked.CompareExchange(ref found, candidate, null);
                loop.Stop();
                return;
            }

            int done = Interlocked.Increment(ref tried);
            if (onProgress is null)
            {
                return;
            }

            long now = clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref lastReportMs);
            if (now - last >= 80 && Interlocked.CompareExchange(ref lastReportMs, now, last) == last)
            {
                onProgress(done, total);
            }
        });

        onProgress?.Invoke(found is null ? tried : total, total);
        return found;
    }

    // A recovery uses every core for minutes, so only one runs at a time across all users.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Busy => Gate.CurrentCount == 0;

    public static async Task<string?> RecoverAsync(byte[] metadataFile, Func<string, bool> tryLoad, Action<int, int>? onProgress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Recover(metadataFile, tryLoad, onProgress, ct), ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
