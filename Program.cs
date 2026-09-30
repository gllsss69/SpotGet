using System.Threading.RateLimiting;
using System.IO.Compression;
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

// Фоновий сервіс для очищення тимчасових файлів spotget_*
builder.Services.AddHostedService<TempFileCleanupService>();

// Перевірка здоров'я системи (yt-dlp, ffmpeg, дисковий простір)
builder.Services.AddHealthChecks()
    .AddCheck<SystemHealthCheck>("system_health");

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
        return Results.File(stream, "audio/mpeg");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("DownloadTrack")
.WithDescription("Завантажує MP3 треку за Spotify посиланням.");

app.MapPost("/api/collection-info", async (TrackRequest request, ISpotifyService spotify) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000)
        return Results.BadRequest(new { error = "Посилання не може бути порожнім або занадто довгим." });

    try
    {
        var collection = await spotify.GetCollectionInfoAsync(request.Url);
        return Results.Ok(collection);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetCollectionInfo")
.WithDescription("Повертає список треків Spotify-альбому або плейліста.");

app.MapPost("/api/download-collection", async (TrackRequest request, HttpContext context, ISpotifyService spotify, IDownloadService downloader, DownloadQueue queue, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000)
        return Results.BadRequest(new { error = "Посилання не може бути порожнім або занадто довгим." });

    string? tempDir = null;
    try
    {
        var collection = await spotify.GetCollectionInfoAsync(request.Url);
        if (collection.Tracks.Count > 100)
            return Results.BadRequest(new { error = "За один раз можна завантажити не більше 100 треків." });

        tempDir = Path.Combine(Path.GetTempPath(), $"spotget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "collection.zip");
        var failedTracks = new List<string>();
        var downloadedTracks = 0;

        // Download tracks concurrently; DownloadQueue enforces the global concurrency limit.
        var downloadResults = await Task.WhenAll(collection.Tracks.Select(async (track, index) =>
        {
            try
            {
                var trackPath = await queue.EnqueueAsync(() => downloader.DownloadAndTagTrackAsync(track));
                return (TrackPath: (string?)trackPath, Failure: (string?)null);
            }
            catch (Exception ex)
            {
                var failure = $"{index + 1:00}. {track.Artist} - {track.Title}: {ex.Message}";
                logger.LogWarning(ex, "Не вдалося завантажити трек {TrackNumber}: {Title}", index + 1, track.Title);
                return (TrackPath: (string?)null, Failure: failure);
            }
        }));

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (var i = 0; i < collection.Tracks.Count; i++)
            {
                var track = collection.Tracks[i];
                var trackPath = downloadResults[i].TrackPath;
                if (trackPath is null)
                {
                    failedTracks.Add(downloadResults[i].Failure!);
                    continue;
                }

                try
                {
                    var entryName = $"{i + 1:00} - {SafeFilePart(track.Artist)} - {SafeFilePart(track.Title)}.mp3";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var source = File.OpenRead(trackPath);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination);
                    downloadedTracks++;
                }
                catch (Exception ex)
                {
                    var failure = $"{i + 1:00}. {track.Artist} - {track.Title}: {ex.Message}";
                    failedTracks.Add(failure);
                    logger.LogWarning(ex, "Не вдалося додати трек {TrackNumber} до ZIP: {Title}", i + 1, track.Title);
                }
                finally
                {
                    try
                    {
                        var trackDir = Path.GetDirectoryName(trackPath);
                        if (trackDir is not null && Path.GetFileName(trackDir).StartsWith("spotget_", StringComparison.Ordinal))
                            Directory.Delete(trackDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Не вдалося прибрати тимчасові файли треку {Title}", track.Title);
                    }
                }
            }

            if (downloadedTracks == 0)
                throw new InvalidOperationException("Не вдалося завантажити жодного треку з цієї колекції.");

            if (failedTracks.Count > 0)
            {
                var report = archive.CreateEntry("_download-report.txt", CompressionLevel.Fastest);
                await using var reportStream = report.Open();
                await using var writer = new StreamWriter(reportStream);
                await writer.WriteLineAsync("Деякі треки не вдалося завантажити:");
                foreach (var failure in failedTracks)
                    await writer.WriteLineAsync(failure);
            }
        }

        context.Response.Headers["X-SpotGet-Skipped-Tracks"] = failedTracks.Count.ToString();
        var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        return Results.File(stream, "application/zip", $"{SafeFilePart(collection.Title)}.zip");
    }
    catch (ArgumentException ex)
    {
        if (tempDir is not null) try { Directory.Delete(tempDir, recursive: true); } catch { }
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        if (tempDir is not null) try { Directory.Delete(tempDir, recursive: true); } catch { }
        logger.LogError(ex, "Помилка завантаження Spotify-колекції");
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("DownloadCollection")
.WithDescription("Завантажує треки альбому або плейліста в ZIP-архів.");

static string SafeFilePart(string value)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var safe = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
    return string.IsNullOrWhiteSpace(safe) ? "Spotify collection" : safe[..Math.Min(safe.Length, 100)];
}

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

// Ендпоінт для перевірки стану застосунку та залежностей (Docker Healthcheck)
app.MapHealthChecks("/health");

app.Run();
