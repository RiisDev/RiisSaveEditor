using RiisSaveEditor.Saves;

namespace RiisSaveEditor
{
	// Hourly sweep that deletes cached saves nobody has used for 30 days.
	public sealed class CacheCleanup(SaveCache cache, ILogger<CacheCleanup> logger) : BackgroundService
	{
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			using PeriodicTimer timer = new(TimeSpan.FromHours(1));
			do
			{
				try
				{
					int removed = await cache.SweepAsync(stoppingToken);
					if (removed > 0)
					{
						logger.LogInformation("Removed {Count} expired cached saves", removed);
					}
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					logger.LogWarning(e, "Cache cleanup failed");
				}
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
