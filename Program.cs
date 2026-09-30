using System.Threading.RateLimiting;
using SpotGet.Models;
using SpotGet.Services;

var builder = WebApplication.CreateBuilder(args);

// Кешування метаданих Spotify в пам'яті
builder.Services.AddMemoryCache();

// HTTP-клієнти для SpotifyService та DownloadService
builder.Services.AddHttpClient<ISpotifyService, SpotifyService>();
builder.Services.AddHttpClient<IDownloadService, YtDlpDownloadService>();

// Черга завантажень (макс. 3 одночасних завантаження з YouTube)
builder.Services.AddSingleton(new DownloadQueue(maxConcurrent: 3));

// Лічильник унікальних відвідувачів
builder.Services.AddSingleton<VisitorService>();

// Rate Limiter (обмеження запитів за IP)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// OpenAPI (Swagger)
builder.Services.AddOpenApi();

var app = builder.Build();

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

// HTTP Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; img-src 'self' data: https:; media-src 'self' https:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; connect-src 'self'");
    await next();
});

// Статичні файли з wwwroot (HTML/JS/CSS для фронтенду).
app.UseDefaultFiles();  // index.html як дефолтний документ
app.UseStaticFiles();

// Endpoints

app.MapPost("/api/track-info", async (TrackRequest request, ISpotifyService spotify) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000)
        return Results.BadRequest(new { error = "Поле 'url' не може бути порожнім або занадто довгим." });

    try
    {
        var track = await spotify.GetTrackInfoAsync(request.Url);
        return Results.Ok(track);
    }
    catch (ArgumentException ex)
    {
        // Невалідне посилання.
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        // Помилка при зверненні до Spotify API.
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetTrackInfo")
.WithDescription("Повертає метадані Spotify-треку за посиланням.");

app.MapPost("/api/download", async (TrackRequest request, ISpotifyService spotify, IDownloadService downloader, DownloadQueue queue) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000)
        return Results.BadRequest(new { error = "Поле 'url' не може бути порожнім або занадто довгим." });

    try
    {
        var track = await spotify.GetTrackInfoAsync(request.Url);
        var filePath = await queue.EnqueueAsync(() => downloader.DownloadAndTagTrackAsync(track));
        
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        return Results.File(stream, "audio/mpeg", $"{track.Artist} - {track.Title}.mp3");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("DownloadTrack")
.WithDescription("Завантажує MP3 треку за Spotify посиланням.");

app.MapGet("/api/visitors", (HttpContext context, string? vid, VisitorService visitors) =>
{
    var count = visitors.GetCount();

    // Якщо клієнт передав свій унікальний ID з LocalStorage — реєструємо його
    if (!string.IsNullOrWhiteSpace(vid))
    {
        count = visitors.TrackVisitor(vid);
    }

    // Забороняємо Cloudflare та браузеру кешувати цю відповідь
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";

    return Results.Ok(new { count });
})
.WithName("GetVisitorCount")
.WithDescription("Повертає кількість унікальних відвідувачів сайту.");

app.Run();
