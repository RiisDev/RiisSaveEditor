namespace RiisSaveEditor.Saves;

// The operator-wide list of game passwords (App_Data/known-passwords.txt), one per line; blank lines and
// lines starting with # are ignored. Every encrypted load without a typed password tries each entry, and
// anyone can add one by opening a save they encrypted themselves, so the list is capped.
public static class KnownPasswords
{
    public const int Max = 500;

    public static IEnumerable<string> Read(string? path) =>
        path is not null && File.Exists(path)
            ? File.ReadLines(path).Select(l => l.TrimEnd('\r', '\n')).Where(l => l.Length > 0 && l[0] != '#').Take(Max)
            : [];
}
