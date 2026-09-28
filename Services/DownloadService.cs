using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotGet.Models;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Converter;
using YoutubeExplode.Search;

namespace SpotGet.Services;

public interface IDownloadService
{
    /// <summary>
    /// Знаходить трек на YouTube, завантажує його, конвертує в MP3 та додає ID3-теги.
    /// Повертає шлях до тимчасового файлу.
    /// </summary>
    Task<string> DownloadAndTagTrackAsync(SpotTrackDto track);
}

public class YoutubeDownloadService : IDownloadService
{
    private readonly YoutubeClient _youtube;
    private readonly HttpClient _httpClient;
    private readonly ILogger<YoutubeDownloadService> _logger;

    public YoutubeDownloadService(HttpClient httpClient, ILogger<YoutubeDownloadService> logger)
    {
        _httpClient = httpClient;
        _youtube = new YoutubeClient(httpClient);
        _logger = logger;
    }

    public async Task<string> DownloadAndTagTrackAsync(SpotTrackDto track)
    {
        var searchQuery = $"{track.Artist} {track.Title} audio";
        _logger.LogInformation("Шукаємо на YouTube: {Query}", searchQuery);
        
        var searchResults = await _youtube.Search.GetVideosAsync(searchQuery).CollectAsync(1);
        var video = searchResults.FirstOrDefault();

        if (video == null)
            throw new System.Exception("Не вдалося знайти відповідний трек на YouTube.");

        var tempPath = Path.GetTempFileName();
        var mp3Path = Path.ChangeExtension(tempPath, ".mp3");
        if (File.Exists(tempPath)) File.Delete(tempPath);

        _logger.LogInformation("Завантаження аудіо з YouTube ({Url}) у файл {Path}", video.Url, mp3Path);

        // Завантажуємо та конвертуємо в MP3 (потребує FFmpeg у системі)
        await _youtube.Videos.DownloadAsync(video.Url, mp3Path, builder => builder.SetPreset(ConversionPreset.UltraFast));

        _logger.LogInformation("Додавання ID3-тегів для {Title}", track.Title);
        await AddTagsAsync(mp3Path, track);

        return mp3Path;
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
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося завантажити обкладинку для тегів");
            }
        }

        file.Save();
    }
}
