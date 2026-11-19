using RiisSaveEditor.Saves;

namespace RiisSaveEditor.Checks
{
	internal static class RenpyCheatsChecks
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
			Expect(all.Split('\n').All(l => l.Length == 0 || l.StartsWith('#') || l.StartsWith("init") || l.StartsWith("    ") || l.StartsWith("screen ") || l.StartsWith("label ") || l.StartsWith("style ")), "body indented");
			Expect(all.Contains("screen rcheats(") && all.Contains("config.overlay_screens") && !RenpyCheats.Build(new CheatOptions(Menu: false)).Contains("rcheats"), "menu framework toggles");

			Expect(!all.Contains("{{TABS}}") && !all.Contains("{{BRANCHES}}"), "placeholders replaced");
			foreach (string tab in new[] { "rcheats_tab_values", "rcheats_tab_notify", "rcheats_tab_log", "rcheats_tab_strings", "rcheats_tab_general" })
			{
				Expect(all.Contains("screen " + tab + "()") && all.Contains("use " + tab), "tab " + tab);
			}

			Expect(all.Contains("use rcheats_sec_hud") && all.Contains("use rcheats_sec_walk") && !all.Contains("{{SECTIONS}}"), "general page sections");
			Expect(all.Contains("def rcheats_walk_layout") && all.Contains("def rcheats_grow_apply") && all.Contains("rcheats_walk_fit") && all.Contains("rcheats_walk_wrap"), "walkthrough fit modes");
			string noFeatures = RenpyCheats.Build(new CheatOptions(Values: false, Strings: false, Hud: false, Walkthrough: false));
			Expect(noFeatures.Contains("screen rcheats(") && !noFeatures.Contains("rcheats_tab_values") && !noFeatures.Contains("replace_text"), "features toggle individually");

			string only = RenpyCheats.Build(new CheatOptions(Console: true, QuickSave: false, Skip: false, Rollback: false, Menu: false));
			Expect(only.Contains("config.console") && !only.Contains("K_F5") && !only.Contains("rollback"), "only console");

			Expect(!RenpyCheats.Build(new CheatOptions(false, false, false, false, false)).Contains("init 999"), "none emits no code");
			string html = RenpyCheats.Highlight(all);
			Expect(html.Contains("<span class=\"tk-keyword\">init</span>") && html.Contains("tk-comment") && html.Contains("<span class=\"tk-string\">&#39;K_F5&#39;</span>") && html.Contains("<span class=\"tk-literal\">True</span>"), "highlight spans");
			Expect(!html.Contains("<script") && RenpyCheats.Highlight("# <b>&").Contains("&lt;b&gt;&amp;"), "highlight encodes html");

			Console.WriteLine($"Renpy cheats checks: {(failures == 0 ? "ok" : "FAILED")}");
			return failures;
		}
	}
}
