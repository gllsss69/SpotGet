using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotGet.Models;

namespace SpotGet.Services;

public interface IDownloadService
{
    /// <summary>
    /// Знаходить трек на YouTube, завантажує його, конвертує в MP3 та додає ID3-теги.
    /// Повертає шлях до тимчасового файлу.
    /// </summary>
    Task<string> DownloadAndTagTrackAsync(SpotTrackDto track);
}

public class YtDlpDownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YtDlpDownloadService> _logger;

    public YtDlpDownloadService(HttpClient httpClient, ILogger<YtDlpDownloadService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadAndTagTrackAsync(SpotTrackDto track)
    {
        var searchQuery = $"{track.Artist} - {track.Title}";
        _logger.LogInformation("Шукаємо на YouTube через yt-dlp: {Query}", searchQuery);

        // Створюємо тимчасову директорію для завантаження
        var tempDir = Path.Combine(Path.GetTempPath(), $"spotget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var outputTemplate = Path.Combine(tempDir, "audio.%(ext)s");
        var expectedMp3 = Path.Combine(tempDir, "audio.mp3");

        try
        {
            var cookiesPath = Environment.GetEnvironmentVariable("YTDLP_COOKIES_PATH")
                              ?? "/app/data/cookies.txt";

            // Перевіряємо, що шлях існує і це файл (а не папку, яку міг створити Docker) та файл не порожній
            var hasCookies = File.Exists(cookiesPath) 
                             && !Directory.Exists(cookiesPath) 
                             && new FileInfo(cookiesPath).Length > 0;

            // Переглядаємо кілька результатів і беремо той, чия довжина ближча до Spotify.
            var videoUrl = await FindBestYoutubeMatchAsync(searchQuery, track, hasCookies ? cookiesPath : null);
            var argsList = BuildYtDlpArgs(videoUrl ?? $"ytsearch1:{searchQuery}", outputTemplate,
                hasCookies ? cookiesPath : null);

            _logger.LogInformation("Завантажуємо вибране відео (cookies={HasCookies}): {Args}",
                hasCookies, string.Join(" ", argsList));

            var (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", argsList, timeoutSeconds: 120);

            // Якщо не вдалось з cookies — спробувати без них
            if (exitCode != 0 && hasCookies && IsCookieRelatedError(stderr))
            {
                _logger.LogWarning("Cookies протухли або недійсні. Повторюємо без cookies. stderr: {Stderr}", stderr);

                // Очищаємо тимчасову директорію перед повторною спробою
                foreach (var f in Directory.GetFiles(tempDir)) File.Delete(f);

                argsList = BuildYtDlpArgs(videoUrl ?? $"ytsearch1:{searchQuery}", outputTemplate, cookiesPath: null);

                _logger.LogInformation("Запускаємо yt-dlp (спроба 2, без cookies): {Args}",
                    string.Join(" ", argsList));

                (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", argsList, timeoutSeconds: 120);
            }

            if (exitCode != 0)
            {
                _logger.LogError("yt-dlp завершився з кодом {ExitCode}. stderr: {Stderr}", exitCode, stderr);

                // Формуємо зрозуміле повідомлення для користувача
                if (stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("YouTube тимчасово блокує завантаження з цього сервера. Спробуйте пізніше або зверніться до адміністратора для оновлення cookies.");
                if (stderr.Contains("No video results", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("no results", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Не вдалося знайти трек \"{track.Artist} - {track.Title}\" на YouTube.");

                throw new Exception($"Не вдалося завантажити трек. Помилка yt-dlp (код {exitCode}).");
            }

            if (!File.Exists(expectedMp3) && Directory.GetFiles(tempDir).Length == 0)
            {
                var fallbackQuery = $"{track.Title} {track.Artist}";
                _logger.LogWarning("yt-dlp не створив файл за запитом {Query}. Повторюємо пошук як {FallbackQuery}",
                    searchQuery, fallbackQuery);

                argsList = BuildYtDlpArgs($"ytsearch1:{fallbackQuery}", outputTemplate,
                    hasCookies ? cookiesPath : null);
                (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", argsList, timeoutSeconds: 120);
                if (exitCode != 0)
                {
                    _logger.LogWarning("Повторний пошук завершився з кодом {ExitCode}. stderr: {Stderr}", exitCode, stderr);
                    throw new Exception($"Не вдалося завантажити трек \"{track.Artist} - {track.Title}\" з YouTube.");
                }
            }

            // Перевіряємо чи файл створився
            if (!File.Exists(expectedMp3))
            {
                // Можливо файл має інше розширення, шукаємо будь-який аудіофайл
                var files = Directory.GetFiles(tempDir);
                _logger.LogWarning("Очікуваний файл {Expected} не знайдений. Файли в директорії: {Files}",
                    expectedMp3, string.Join(", ", files));

                if (files.Length == 0)
                    throw new Exception($"YouTube не повернув аудіофайл для треку \"{track.Artist} - {track.Title}\".");

                expectedMp3 = files[0];
            }

            _logger.LogInformation("Аудіо завантажено: {Path}", expectedMp3);

            // Додаємо ID3-теги
            _logger.LogInformation("Додавання ID3-тегів для {Title}", track.Title);
            await AddTagsAsync(expectedMp3, track);

            return expectedMp3;
        }
        catch
        {
            // При помилці очищаємо тимчасову директорію
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Шукає кілька відео-кандидатів і вибирає результат із найближчою тривалістю.
    /// </summary>
    private async Task<string?> FindBestYoutubeMatchAsync(string searchQuery, SpotTrackDto track, string? cookiesPath)
    {
        var args = new List<string>
        {
            "--flat-playlist",
            "--dump-single-json",
            "--skip-download",
            "--playlist-end", "5",
            "--no-check-certificates",     // Не перевіряти SSL
            "--js-runtimes", "deno",       // Використовувати Deno для розв'язання EJS/n-sig челенджів
            "--extractor-args", "youtube:player-client=android,mweb,web_safari,web_embedded", // Оптимальні клієнти для обходу блокувань
            "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "--socket-timeout", "30",      // Таймаут сокету
            "--retries", "3",              // 3 спроби
            $"ytsearch5:{searchQuery}"
        };

        if (cookiesPath != null)
            args.AddRange(new[] { "--cookies", cookiesPath });

        var (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", args, timeoutSeconds: 120);
        if (exitCode != 0 && cookiesPath is not null && IsCookieRelatedError(stderr))
        {
            _logger.LogWarning("Не вдалося шукати з cookies; повторюємо без них. stderr: {Stderr}", stderr);
            args.RemoveRange(args.Count - 2, 2);
            (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", args, timeoutSeconds: 120);
        }

        if (exitCode != 0)
        {
            _logger.LogWarning("Не вдалося отримати результати пошуку YouTube (код {ExitCode}); беремо перший результат звичайним пошуком. stderr: {Stderr}",
                exitCode, stderr);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var entries = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var items)
                ? items
                : default;
            var candidates = new List<(string Url, string Title, int? Duration)>();

            void AddCandidate(JsonElement item)
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return;

                var url = GetString(item, "webpage_url") ?? GetString(item, "original_url");
                var id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
                    url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}";
                if (string.IsNullOrWhiteSpace(url))
                    return;

                int? duration = null;
                if (item.TryGetProperty("duration", out var durationValue) &&
                    durationValue.ValueKind == JsonValueKind.Number && durationValue.TryGetDouble(out var seconds) &&
                    seconds > 0)
                    duration = (int)Math.Round(seconds);

                candidates.Add((url, GetString(item, "title") ?? "(назва недоступна)", duration));
            }

            if (entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entries.EnumerateArray())
                    AddCandidate(item);
            }
            else
            {
                AddCandidate(root);
            }

            if (candidates.Count == 0)
            {
                _logger.LogWarning("yt-dlp не повернув придатних кандидатів; беремо перший результат пошуку.");
                return null;
            }

            var firstWithDuration = candidates.FirstOrDefault(candidate => candidate.Duration.HasValue);
            var selected = track.DurationMs > 0 && firstWithDuration.Duration.HasValue
                ? candidates.Where(candidate => candidate.Duration.HasValue)
                    .OrderBy(candidate => Math.Abs(candidate.Duration!.Value - track.DurationMs / 1000d))
                    .First()
                : candidates[0];

            if (track.DurationMs > 0 && selected.Duration.HasValue)
            {
                var difference = Math.Abs(selected.Duration.Value - track.DurationMs / 1000d);
                var tolerance = Math.Max(15, track.DurationMs / 1000d * 0.05);
                if (difference > tolerance)
                    _logger.LogWarning("Найкращий збіг за довжиною все одно відрізняється на {DifferenceSeconds:F0} с: Spotify={SpotifySeconds:F0} с, YouTube={YoutubeSeconds} с, {Title}",
                        difference, track.DurationMs / 1000d, selected.Duration, selected.Title);
                else
                    _logger.LogInformation("Вибрано YouTube-результат за довжиною: Spotify={SpotifySeconds:F0} с, YouTube={YoutubeSeconds} с, {Title}",
                        track.DurationMs / 1000d, selected.Duration, selected.Title);
            }
            else
            {
                _logger.LogWarning("Тривалість кандидатів або Spotify відсутня; беремо перший результат: {Title}", selected.Title);
            }

            return selected.Url;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Не вдалося розібрати результати пошуку yt-dlp; беремо перший результат звичайним пошуком.");
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Аргументи для завантаження вибраного відео.
    /// </summary>
    private static List<string> BuildYtDlpArgs(string videoUrl, string outputTemplate, string? cookiesPath)
    {
        var args = new List<string>
        {
            videoUrl,
            "-x",                          // Витягнути тільки аудіо
            "--audio-format", "mp3",       // Конвертувати в MP3
            "--audio-quality", "0",        // Найкраща якість
            "--no-playlist",               // Без плейлистів
            "--no-check-certificates",     // Не перевіряти SSL
            "--js-runtimes", "deno",       // Використовувати Deno для розв'язання EJS/n-sig челенджів
            "--extractor-args", "youtube:player-client=android,mweb,web_safari,web_embedded",
            "--user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "--socket-timeout", "30",
            "--retries", "3",
            "-o", outputTemplate
        };

        if (cookiesPath != null)
            args.AddRange(new[] { "--cookies", cookiesPath });

        return args;
    }

    /// <summary>
    /// Перевіряє, чи помилка пов'язана з протухлими/недійсними cookies.
    /// </summary>
    private static bool IsCookieRelatedError(string stderr)
    {
        return stderr.Contains("cookies are no longer valid", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("cookies have been rotated", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("cookie", StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddTagsAsync(string filePath, SpotTrackDto track)
    {
        using var file = TagLib.File.Create(filePath);

        file.Tag.Title = track.Title;
        file.Tag.Performers = new[] { track.Artist };
        file.Tag.AlbumArtists = new[] { track.Artist };
        file.Tag.Track = track.TrackNumber;
        file.Tag.TrackCount = track.TrackCount;
        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            file.Tag.Album = track.Album;
        }

        if (!string.IsNullOrEmpty(track.CoverUrl))
        {
            try
            {
                using var coverResponse = await _httpClient.GetAsync(track.CoverUrl);
                coverResponse.EnsureSuccessStatusCode();
                var coverBytes = await coverResponse.Content.ReadAsByteArrayAsync();
                var mimeType = GetImageMimeType(coverResponse.Content.Headers.ContentType?.MediaType, coverBytes);
                if (mimeType is null)
                {
                    _logger.LogWarning("Обкладинка має непідтримуваний формат: {ContentType}",
                        coverResponse.Content.Headers.ContentType?.MediaType ?? "unknown");
                    file.Save();
                    return;
                }

                var picture = new TagLib.Picture(new TagLib.ByteVector(coverBytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    Description = "Cover",
                    MimeType = mimeType
                };
                file.Tag.Pictures = new TagLib.IPicture[] { picture };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося завантажити обкладинку для тегів");
            }
        }
        else
        {
            _logger.LogWarning("Для треку {Title} немає URL обкладинки", track.Title);
        }

        file.Save();
    }

    private static string? GetImageMimeType(string? contentType, byte[] bytes)
    {
        if (contentType is not null && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg"
                : contentType;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, List<string> arguments, int timeoutSeconds = 60)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        process.StartInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
        process.StartInfo.EnvironmentVariables["LANG"] = "C.UTF-8";
        process.StartInfo.EnvironmentVariables["LC_ALL"] = "C.UTF-8";

        // Передаємо кожен аргумент окремо через ArgumentList —
        // це коректно працює на Linux без проблем з екрануванням
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        // Читаємо stdout та stderr асинхронно, щоб уникнути дедлоків
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"yt-dlp не завершився за {timeoutSeconds} секунд.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
