using System.Text;
using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks;

// Checks the string-scan password recovery against synthetic binary noise standing in for a
// global-metadata.dat or Assembly-CSharp.dll, since a real game file should not be in the repo.
public static class PasswordRecoveryChecks
{
    public static int Run()
    {
        int failures = 0;

        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} password recovery: {name}");
            failures += ok ? 0 : 1;
        }

        const string password = "sup3r-secret-pw";
        Random rng = new(1234);

        // Random bytes with a few embedded zero/control bytes to mimic a binary section, the ASCII
        // password dropped in once, and the UTF-16LE encoding of it dropped in separately.
        byte[] noise = new byte[4096];
        rng.NextBytes(noise);
        byte[] asciiNeedle = Encoding.ASCII.GetBytes(password);
        byte[] utf16Needle = Encoding.Unicode.GetBytes(password);
        asciiNeedle.CopyTo(noise, 100);
        utf16Needle.CopyTo(noise, 2000);

        List<string> candidates = [.. PasswordRecovery.ExtractCandidateStrings(noise)];
        Check("finds the ASCII-embedded password", candidates.Contains(password));

        string? recovered = PasswordRecovery.Recover(noise, candidate => candidate == password);
        Check("Recover returns the matching candidate", recovered == password);

        string? none = PasswordRecovery.Recover(noise, _ => false);
        Check("Recover returns null when nothing matches", none is null);

        // A too-short run (below MinLength) must not be returned as a candidate on its own.
        byte[] shortOnly = Encoding.ASCII.GetBytes("ab");
        Check("very short runs are dropped", !PasswordRecovery.ExtractCandidateStrings(shortOnly).Any());

        return failures;
    }
}
