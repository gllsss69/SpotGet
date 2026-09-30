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
        var searchQuery = $"{track.Artist} - {track.Title} audio";
        _logger.LogInformation("Шукаємо на YouTube через yt-dlp: {Query}", searchQuery);

        // Створюємо тимчасову директорію для завантаження
        var tempDir = Path.Combine(Path.GetTempPath(), $"spotget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var outputTemplate = Path.Combine(tempDir, "audio.%(ext)s");
        var expectedMp3 = Path.Combine(tempDir, "audio.mp3");

        try
        {
            // Збираємо аргументи для yt-dlp (кожен елемент — окремий аргумент)
            var argsList = new List<string>
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

            // Якщо є cookies файл — додаємо його для обходу блокування YouTube
            var cookiesPath = Environment.GetEnvironmentVariable("YTDLP_COOKIES_PATH")
                              ?? "/app/data/cookies.txt";
            if (File.Exists(cookiesPath) && new FileInfo(cookiesPath).Length > 0)
            {
                argsList.AddRange(new[] { "--cookies", cookiesPath });
                _logger.LogInformation("Використовуємо cookies файл: {Path}", cookiesPath);
            }
            else
            {
                argsList.AddRange(new[] { "--extractor-args", "youtube:player-client=web_safari,web_embedded,-tv_downgraded" });
                _logger.LogDebug("Cookies файл не знайдено за шляхом {Path}, продовжуємо без нього", cookiesPath);
            }

            _logger.LogInformation("Запускаємо yt-dlp з аргументами: {Args}", string.Join(" ", argsList));

            var (exitCode, stdout, stderr) = await RunProcessAsync("yt-dlp", argsList, timeoutSeconds: 120);

            if (exitCode != 0)
            {
                _logger.LogError("yt-dlp завершився з кодом {ExitCode}. stderr: {Stderr}", exitCode, stderr);
                throw new Exception($"Не вдалося завантажити трек. yt-dlp повернув код {exitCode}: {stderr}");
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
