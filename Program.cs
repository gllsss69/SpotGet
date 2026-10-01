using System.Threading.RateLimiting;
using System.Net;
using SpotGet.Models;
using SpotGet.Services;

var builder = WebApplication.CreateBuilder(args);

// Кешування метаданих Spotify в пам'яті
builder.Services.AddMemoryCache();

// HTTP-клієнти для SpotifyService та DownloadService
builder.Services.AddHttpClient<ISpotifyService, SpotifyService>();
builder.Services.AddHttpClient<IDownloadService, YtDlpDownloadService>();
builder.Services.AddHttpClient("SpotifyImages")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Черга завантажень (макс. 3 одночасних завантаження з YouTube)
builder.Services.AddSingleton(new DownloadQueue(maxConcurrent: 3));
builder.Services.AddSingleton<CollectionDownloadJobService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CollectionDownloadJobService>());

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
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (string.Equals(Path.GetExtension(context.File.Name), ".html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    }
});

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

app.MapPost("/api/download-cover", async (CoverDownloadRequest request, IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000 ||
        string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 200)
        return Results.BadRequest(new { error = "Некоректне посилання або назва обкладинки." });

    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var imageUri) || !IsSpotifyImageUri(imageUri))
        return Results.BadRequest(new { error = "Дозволено завантажувати лише зображення з CDN Spotify." });

    const long maxImageBytes = 15 * 1024 * 1024;
    using var client = httpClientFactory.CreateClient("SpotifyImages");
    HttpResponseMessage? imageResponse = null;

    try
    {
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            var response = await client.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == 3)
                    return Results.Problem("Spotify CDN повернув забагато перенаправлень.", statusCode: StatusCodes.Status502BadGateway);

                var nextUri = location.IsAbsoluteUri ? location : new Uri(imageUri, location);
                if (!IsSpotifyImageUri(nextUri))
                    return Results.BadRequest(new { error = "Перенаправлення веде за межі CDN Spotify." });
                imageUri = nextUri;
                continue;
            }

            imageResponse = response;
            break;
        }

        if (imageResponse is null || !imageResponse.IsSuccessStatusCode)
        {
            var status = imageResponse?.StatusCode ?? HttpStatusCode.BadGateway;
            imageResponse?.Dispose();
            return Results.Problem("Не вдалося отримати обкладинку зі Spotify CDN.", statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["upstreamStatus"] = (int)status });
        }

        using (imageResponse)
        {
            var contentType = imageResponse.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            var extension = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/avif" => ".avif",
                _ => null
            };
            if (extension is null)
                return Results.Problem("Spotify CDN повернув непідтримуваний формат обкладинки.", statusCode: StatusCodes.Status502BadGateway);

            if (imageResponse.Content.Headers.ContentLength is > maxImageBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            await using var source = await imageResponse.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > maxImageBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }

            var baseName = SafeDownloadFilePart(request.FileName);
            return Results.File(buffer.ToArray(), contentType, $"{baseName}{extension}");
        }
    }
    catch (HttpRequestException)
    {
        imageResponse?.Dispose();
        return Results.Problem("Не вдалося отримати обкладинку зі Spotify CDN.", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("DownloadCover")
.WithDescription("Завантажує обкладинку Spotify з читабельною назвою файла.");

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

app.MapPost("/api/download-collection", (TrackRequest request, HttpContext context, CollectionDownloadJobService jobs) =>
{
    if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > 2000)
        return Results.BadRequest(new { error = "Посилання не може бути порожнім або занадто довгим." });

    if (!context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = "Оновіть сторінку перед початком завантаження." });

    try
    {
        var jobId = jobs.Enqueue(request.Url);
        return Results.Accepted($"/api/download-collection/{jobId}", new { jobId });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("DownloadCollection")
.WithDescription("Завантажує треки альбому або плейліста в ZIP-архів.");

app.MapGet("/api/download-collection/{jobId:guid}", (Guid jobId, HttpContext context, CollectionDownloadJobService jobs) =>
{
    var status = jobs.GetStatus(jobId);
    if (status is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(status);
});

app.MapGet("/api/download-collection/{jobId:guid}/file", (Guid jobId, HttpContext context, CollectionDownloadJobService jobs) =>
{
    var artifact = jobs.GetArtifact(jobId);
    if (artifact is null)
    {
        var status = jobs.GetStatus(jobId);
        if (status is null) return Results.NotFound();
        if (status.Status == "failed")
            return Results.Problem(detail: status.Error ?? "Не вдалося зібрати ZIP-архів.", statusCode: StatusCodes.Status500InternalServerError);
        return Results.Conflict(new { error = "Архів ще готується." });
    }

    context.Response.Headers["X-SpotGet-Skipped-Tracks"] = artifact.Skipped.ToString();
    if (!jobs.TryBeginFileTransfer(jobId))
        return Results.Conflict(new { error = "Архів уже передається або його вже завантажили." });

    context.Response.OnCompleted(() =>
    {
        jobs.MarkFileDelivered(jobId, artifact.Directory);
        return Task.CompletedTask;
    });
    return Results.File(artifact.Path, "application/zip", artifact.FileName);
});

static bool IsSpotifyImageUri(Uri uri)
{
    var host = uri.Host;
    var isSpotifyCdn = host.Equals("i.scdn.co", StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("spotifycdn.com", StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);
    return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && isSpotifyCdn;
}

static string SafeDownloadFilePart(string value)
{
    var safe = new string(value
        .Select(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) ? '_' : ch)
        .ToArray())
        .Trim()
        .TrimEnd('.');
    return string.IsNullOrWhiteSpace(safe) ? "Spotify cover" : safe[..Math.Min(safe.Length, 120)];
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
