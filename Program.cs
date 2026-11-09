using Microsoft.AspNetCore.DataProtection;
using RiisSaveEditor;
using RiisSaveEditor.Components;
using RiisSaveEditor.Saves;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Keys and the Ren'Py signing key live here so a container volume keeps them across restarts.
string dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");

// A circuit holds the parsed save in memory. Keep abandoned circuits for seconds, not the default minutes,
// so repeated upload-and-disconnect cannot pile up memory.
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
    options.DisconnectedCircuitMaxRetained = 10;
});
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddSingleton<ISaveFormat, GvasFormat>();
builder.Services.AddSingleton<ISaveFormat, RenpyFormat>();
builder.Services.AddSingleton<ISaveFormat, RenpyPersistentFormat>();
builder.Services.AddSingleton<ISaveFormat, RpgMakerFormat>();
builder.Services.AddSingleton<ISaveFormat, PlainJsonFormat>();
builder.Services.AddSingleton<ISaveFormat>(_ => new SaveGameFreeFormat(Path.Combine(dataDir, "known-passwords.txt")));
builder.Services.AddSingleton<ISaveFormat, UnityEs3Format>();
builder.Services.AddSingleton<SaveFormatRegistry>();
builder.Services.AddSingleton(_ => new SaveCache(Path.Combine(dataDir, "cache"), TimeProvider.System));
builder.Services.AddHostedService<CacheCleanup>();

WebApplication app = builder.Build();

app.Use((context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    return next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// The container only serves plain HTTP, so there is no HTTPS port to redirect to.
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
