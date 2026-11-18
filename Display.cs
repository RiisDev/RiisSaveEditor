namespace RiisSaveEditor
{
	// Text formatting shared by the UI components.
	public static class Display
	{
		public static string Bytes(long bytes) => bytes switch
		{
			< 1024 => $"{bytes} B",
			< 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
			_ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
		};

		public static string Ago(DateTimeOffset when, DateTimeOffset now)
		{
			TimeSpan age = now - when;
			return age.TotalMinutes < 1 ? "just now"
				: age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
				: age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
				: $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
		}
	}
}
