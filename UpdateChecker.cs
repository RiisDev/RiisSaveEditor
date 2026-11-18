using System.Reflection;
using System.Text.Json;

namespace RiisSaveEditor
{
	// Polls the latest GitHub release every 30 minutes. One instance serves every circuit.
	public sealed class UpdateChecker(IHttpClientFactory httpFactory, ILogger<UpdateChecker> logger) : BackgroundService
	{
		private const string LatestUrl = "https://api.github.com/repos/RiisDev/RiisSaveEditor/releases/latest";

		// Stamped from the git tag at publish time. Dev builds ("0.0.0-dev") do not parse and never show a banner.
		public static readonly string CurrentText = (Assembly.GetEntryAssembly()?
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];

		private static readonly Version? Current = Version.TryParse(CurrentText, out Version? v) ? v : null;

		public Version? Latest { get; private set; }
		public string? LatestUrlHtml { get; private set; }

		public bool UpdateAvailable => Current is not null && Latest is not null && Latest > Current;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			using PeriodicTimer timer = new(TimeSpan.FromMinutes(30));
			do
			{
				try
				{
					await CheckAsync(stoppingToken);
				}
				catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
				{
					logger.LogWarning("Update check failed: {Message}", e.Message);
				}
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}

		private async Task CheckAsync(CancellationToken ct)
		{
			using HttpClient http = httpFactory.CreateClient();
			http.Timeout = TimeSpan.FromSeconds(15);
			using HttpRequestMessage request = new(HttpMethod.Get, LatestUrl);
			request.Headers.UserAgent.ParseAdd("RiisSaveEditor-UpdateCheck");
			using HttpResponseMessage response = await http.SendAsync(request, ct);
			response.EnsureSuccessStatusCode();

			using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
			string? tag = doc.RootElement.GetProperty("tag_name").GetString();
			if (tag is not null && Version.TryParse(tag.TrimStart('v'), out Version? latest))
			{
				LatestUrlHtml = doc.RootElement.TryGetProperty("html_url", out JsonElement url) ? url.GetString() : null;
				Latest = latest;
			}
		}
	}
}
