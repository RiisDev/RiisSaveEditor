using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks;

static class RenpyCheatsChecks
{
    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok)
            {
                Console.WriteLine($"MISMATCH cheats: {what}");
                failures++;
            }
        }

        string all = RenpyCheats.Build(new CheatOptions());
        Expect(all.Contains("config.console = True") && all.Contains("K_F5") && all.Contains("skip_unseen") && all.Contains("rollback_enabled"), "all options present");
        Expect(all.Split('\n').Count(l => l.StartsWith("init 999 python:")) == 1, "single init block");
        Expect(all.Split('\n').All(l => l.Length == 0 || l.StartsWith('#') || l.StartsWith("init") || l.StartsWith("    ")), "body indented");

        string only = RenpyCheats.Build(new CheatOptions(Console: true, QuickSave: false, Skip: false, Rollback: false));
        Expect(only.Contains("config.console") && !only.Contains("K_F5") && !only.Contains("rollback"), "only console");

        Expect(!RenpyCheats.Build(new CheatOptions(false, false, false, false)).Contains("init 999"), "none emits no code");
        string html = RenpyCheats.Highlight(all);
        Expect(html.Contains("<span class=\"tk-keyword\">init</span>") && html.Contains("tk-comment") && html.Contains("<span class=\"tk-string\">&#39;K_F5&#39;</span>") && html.Contains("<span class=\"tk-literal\">True</span>"), "highlight spans");
        Expect(!html.Contains("<script") && RenpyCheats.Highlight("# <b>&").Contains("&lt;b&gt;&amp;"), "highlight encodes html");

        Console.WriteLine($"Renpy cheats checks: {(failures == 0 ? "ok" : "FAILED")}");
        return failures;
    }
}
