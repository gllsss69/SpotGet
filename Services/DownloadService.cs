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
            var hasCookies = File.Exists(cookiesPath) && new FileInfo(cookiesPath).Length > 0;

            // Спроба 1: з cookies (якщо є) або з extractor-args (якщо немає)
            var argsList = BuildYtDlpArgs(searchQuery, outputTemplate, hasCookies ? cookiesPath : null);

            _logger.LogInformation("Запускаємо yt-dlp (спроба 1, cookies={HasCookies}): {Args}",
                hasCookies, string.Join(" ", argsList));

            var (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", argsList, timeoutSeconds: 120);

            // Якщо не вдалось з cookies — спробувати без них
            if (exitCode != 0 && hasCookies && IsCookieRelatedError(stderr))
            {
                _logger.LogWarning("Cookies протухли або недійсні. Повторюємо без cookies. stderr: {Stderr}", stderr);

                // Очищаємо тимчасову директорію перед повторною спробою
                foreach (var f in Directory.GetFiles(tempDir)) File.Delete(f);

                argsList = BuildYtDlpArgs(searchQuery, outputTemplate, cookiesPath: null);

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

            // Перевіряємо чи файл створився
            if (!File.Exists(expectedMp3))
            {
                // Можливо файл має інше розширення, шукаємо будь-який аудіофайл
                var files = Directory.GetFiles(tempDir);
                _logger.LogWarning("Очікуваний файл {Expected} не знайдений. Файли в директорії: {Files}",
                    expectedMp3, string.Join(", ", files));

                if (files.Length == 0)
                    throw new Exception("yt-dlp не створив жодного файлу.");

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
    /// Збирає аргументи для yt-dlp. Якщо cookiesPath == null, використовує extractor-args як фолбек.
    /// </summary>
    private static List<string> BuildYtDlpArgs(string searchQuery, string outputTemplate, string? cookiesPath)
    {
        var args = new List<string>
        {
            $"ytsearch1:{searchQuery}",
            "-x",                          // Витягнути тільки аудіо
            "--audio-format", "mp3",       // Конвертувати в MP3
            "--audio-quality", "0",        // Найкраща якість
            "--no-playlist",               // Без плейлистів
            "--no-check-certificates",     // Не перевіряти SSL
            "--js-runtimes", "deno",       // Використовувати Deno для розв'язання EJS/n-sig челенджів
            "--socket-timeout", "30",      // Таймаут сокету
            "--retries", "3",              // 3 спроби
            "-o", outputTemplate           // Шлях до файлу
        };

        if (cookiesPath != null)
        {
            args.AddRange(new[] { "--cookies", cookiesPath });
        }
        else
        {
            args.AddRange(new[] { "--extractor-args", "youtube:player-client=web_safari,web_embedded,-tv_downgraded" });
        }

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
        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            file.Tag.Album = track.Album;
        }

        if (!string.IsNullOrEmpty(track.CoverUrl))
        {
            try
            {
                var coverBytes = await _httpClient.GetByteArrayAsync(track.CoverUrl);
                var picture = new TagLib.Picture(new TagLib.ByteVector(coverBytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    Description = "Cover",
                    MimeType = "image/jpeg"
                };
                file.Tag.Pictures = new TagLib.IPicture[] { picture };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося завантажити обкладинку для тегів");
            }
        }

        file.Save();
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
