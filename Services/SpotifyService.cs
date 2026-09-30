using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using SpotGet.Models;

namespace SpotGet.Services;

/// <summary>
/// Реалізація ISpotifyService — отримує метадані треку 
/// комбінуючи Spotify oEmbed API (назва + обкладинка) та
/// HTML meta-теги сторінки треку (артист, альбом, тривалість).
/// Не потребує API-ключів.
/// </summary>
public partial class SpotifyService : ISpotifyService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyService> _logger;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    // Пул User-Agent рядків для ротації при запитах до Spotify.
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "Mozilla/5.0 (compatible; Bingbot/2.0; +http://www.bing.com/bingbot.htm)",
        "Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)",
        "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",
        "Twitterbot/1.0",
        "Mozilla/5.0 (compatible; DuckDuckBot-Https/1.1; https://duckduckgo.com/duckduckbot)",
        "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
        "TelegramBot (like TwitterBot)"
    ];

    // Regex для парсингу Track ID з різних форматів посилань Spotify.
    [GeneratedRegex(@"(?:spotify\.com/track/|spotify:track:)([a-zA-Z0-9]{22})")]
    private static partial Regex SpotifyTrackIdRegex();

    [GeneratedRegex(@"(?:open\.)?spotify\.com/(?:embed/)?(?<type>album|playlist)/(?<id>[a-zA-Z0-9]{22})")]
    private static partial Regex SpotifyCollectionRegex();

    [GeneratedRegex("<script[^>]+id=\\\"__NEXT_DATA__\\\"[^>]*>(?<json>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    // Regex для витягування значення content із <meta> тегів.
    // Підтримує обидва порядки атрибутів: property/name → content та content → property/name.
    [GeneratedRegex("""<meta\s+(?:(?:property|name)="(?<prop>[^"]+)"\s+content="(?<val>[^"]*)"|content="(?<val2>[^"]*)"\s+(?:property|name)="(?<prop2>[^"]+)")\s*/?>""")] 
    private static partial Regex MetaTagRegex();

    private static string GetRandomUserAgent()
    {
        return UserAgents[Random.Shared.Next(UserAgents.Length)];
    }

    public SpotifyService(
        HttpClient httpClient,
        ILogger<SpotifyService> logger,
        IMemoryCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<SpotTrackDto> GetTrackInfoAsync(string spotifyUrl)
    {
        var trackId = ParseTrackId(spotifyUrl);
        var cacheKey = $"track:{trackId}";

        if (_cache.TryGetValue(cacheKey, out SpotTrackDto? cached) && cached is not null)
        {
            _logger.LogInformation("Cache hit for track {TrackId}", trackId);
            return cached;
        }

        var canonicalUrl = $"https://open.spotify.com/track/{trackId}";

        _logger.LogInformation("Fetching metadata for track {TrackId}", trackId);

        // Крок 1: oEmbed API (назва + обкладинка, дає 404 для неіснуючих треків)
        var oembed = await FetchOEmbedAsync(trackId);

        // Крок 2: HTML meta-теги (артист, альбом, тривалість)
        var meta = await FetchHtmlMetaAsync(trackId);

        // Парсимо артиста та альбом з og:description (формат: "Artist · Album · Song · Year")
        var artist = meta.GetValueOrDefault("music:musician_description", "");
        var album = "";
        if (meta.TryGetValue("og:description", out var desc))
        {
            var parts = desc.Split(" · ");
            if (parts.Length >= 2)
                album = parts[1];
            if (string.IsNullOrEmpty(artist) && parts.Length >= 1)
                artist = parts[0];
        }

        var durationSec = 0;
        if (meta.TryGetValue("music:duration", out var durStr))
            int.TryParse(durStr, out durationSec);

        // Обкладинка: з HTML meta (640px) або з oEmbed (300px) як fallback.
        var coverUrl = meta.GetValueOrDefault("og:image") ?? oembed.ThumbnailUrl;

        // Крок 3: Інформація про виконавця
        SpotArtistDto? artistInfo = null;
        var artistUrl = meta.GetValueOrDefault("music:musician");
        if (!string.IsNullOrEmpty(artistUrl))
        {
            try
            {
                artistInfo = await FetchArtistInfoAsync(artistUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося отримати інформацію про виконавця");
            }
        }

        var result = new SpotTrackDto
        {
            Title = oembed.Title,
            Artist = artist,
            Album = album,
            CoverUrl = coverUrl,
            PreviewUrl = meta.GetValueOrDefault("og:audio"),
            DurationMs = durationSec * 1000,
            SpotifyUrl = meta.GetValueOrDefault("og:url", canonicalUrl),
            ArtistInfo = artistInfo
        };

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    /// <inheritdoc />
    public async Task<SpotCollectionDto> GetCollectionInfoAsync(string spotifyUrl)
    {
        if (!Uri.TryCreate(spotifyUrl, UriKind.Absolute, out var parsedUrl) ||
            !string.Equals(parsedUrl.Host, "open.spotify.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Вставте коректне посилання open.spotify.com на альбом або плейліст.");

        var match = SpotifyCollectionRegex().Match(parsedUrl.AbsoluteUri);
        if (!match.Success)
            throw new ArgumentException("Вставте посилання Spotify на альбом або плейліст.");

        var type = match.Groups["type"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        var cacheKey = $"collection:{type}:{id}";
        if (_cache.TryGetValue(cacheKey, out SpotCollectionDto? cached) && cached is not null)
            return cached;

        var canonicalUrl = $"https://open.spotify.com/{type}/{id}";
        var embedUrl = $"https://open.spotify.com/embed/{type}/{id}";
        using var request = new HttpRequestMessage(HttpMethod.Get, embedUrl);
        request.Headers.UserAgent.ParseAdd(GetRandomUserAgent());
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

        using var response = await _httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ArgumentException("Альбом або плейліст не знайдений чи недоступний.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Spotify повернув помилку (HTTP {(int)response.StatusCode}).");

        var html = await response.Content.ReadAsStringAsync();
        var jsonMatch = NextDataRegex().Match(html);
        if (!jsonMatch.Success)
            throw new InvalidOperationException("Spotify не повернув список треків. Спробуйте пізніше.");

        using var document = JsonDocument.Parse(WebUtility.HtmlDecode(jsonMatch.Groups["json"].Value));
        var entity = FindEntity(document.RootElement);
        var trackList = FindTrackList(document.RootElement);
        if (trackList is null)
            throw new InvalidOperationException("Не вдалося прочитати список треків Spotify.");

        var metadata = ParseMetaTags(html);
        var title = GetString(entity, "name", "title")
                    ?? metadata.GetValueOrDefault("og:title")
                    ?? (type == "album" ? "Spotify album" : "Spotify playlist");
        var coverUrl = FindCoverUrl(entity) ?? metadata.GetValueOrDefault("og:image");
        if (string.IsNullOrWhiteSpace(coverUrl))
            coverUrl = await FetchOEmbedThumbnailAsync(canonicalUrl);
        var tracks = new List<SpotTrackDto>();

        var trackNumber = 0u;
        foreach (var item in trackList.Value.EnumerateArray())
        {
            var track = item;
            if (TryGetProperty(item, "track", out var nestedTrack) && nestedTrack.ValueKind == JsonValueKind.Object)
                track = nestedTrack;

            var trackTitle = GetString(track, "title", "name");
            if (string.IsNullOrWhiteSpace(trackTitle))
                continue;

            trackNumber++;

            var artists = GetArtists(track);
            var uri = GetString(track, "uri", "spotifyUri", "id") ?? string.Empty;
            var trackId = uri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase)
                ? uri["spotify:track:".Length..]
                : uri;
            var trackUrl = trackId.Length == 22
                ? $"https://open.spotify.com/track/{trackId}"
                : canonicalUrl;

            tracks.Add(new SpotTrackDto
            {
                Title = trackTitle,
                Artist = artists,
                Album = type == "album" ? title : GetAlbumName(track),
                CoverUrl = FindCoverUrl(track) ?? coverUrl,
                DurationMs = GetDurationMs(track),
                TrackNumber = GetUInt32(track, "trackNumber", "track_number") is var explicitNumber && explicitNumber > 0
                    ? explicitNumber
                    : trackNumber,
                SpotifyUrl = trackUrl
            });
        }

        if (tracks.Count == 0)
            throw new InvalidOperationException("У цьому альбомі або плейлісті не знайдено доступних треків.");

        foreach (var track in tracks)
            track.TrackCount = (uint)tracks.Count;

        SpotArtistDto? artistInfo = null;
        if (tracks[0].SpotifyUrl != canonicalUrl)
        {
            try
            {
                var firstTrackInfo = await GetTrackInfoAsync(tracks[0].SpotifyUrl);
                artistInfo = firstTrackInfo.ArtistInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося отримати профіль виконавця першого треку колекції {CollectionId}", id);
            }
        }

        var result = new SpotCollectionDto
        {
            Title = title,
            Type = type,
            SpotifyUrl = canonicalUrl,
            CoverUrl = coverUrl,
            ArtistInfo = artistInfo,
            Tracks = tracks
        };
        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private static JsonElement? FindTrackList(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("trackList") && property.Value.ValueKind == JsonValueKind.Array)
                    return property.Value;
                var nested = FindTrackList(property.Value);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindTrackList(item);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private static JsonElement FindEntity(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, "entity", out var entity) && entity.ValueKind == JsonValueKind.Object)
                return entity;
            foreach (var property in element.EnumerateObject())
            {
                var found = FindEntity(property.Value);
                if (found.ValueKind == JsonValueKind.Object) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindEntity(item);
                if (found.ValueKind == JsonValueKind.Object) return found;
            }
        }

        return default;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static string GetArtists(JsonElement track)
    {
        if (TryGetProperty(track, "artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(artist => GetString(artist, "name", "title"))
                .Where(name => !string.IsNullOrWhiteSpace(name));
            var joined = string.Join(", ", names);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }

        return GetString(track, "subtitle", "artist") ?? "Unknown artist";
    }

    private static string GetAlbumName(JsonElement track)
    {
        var directName = GetString(track, "albumName", "albumTitle");
        if (!string.IsNullOrWhiteSpace(directName)) return directName;

        if (TryGetProperty(track, "album", out var album))
            return GetString(album, "name", "title") ?? string.Empty;
        if (TryGetProperty(track, "albumOfTrack", out var albumOfTrack))
            return GetString(albumOfTrack, "name", "title") ?? string.Empty;
        return string.Empty;
    }

    private static uint GetUInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.TryGetUInt32(out var number))
                return number;
        }
        return 0;
    }

    private static string? FindCoverUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, "coverArt", out var coverArt))
            {
                if (TryGetProperty(coverArt, "sources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
                    return GetString(sources[0], "url");
            }
            if (TryGetProperty(element, "images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                return GetString(images[0], "url");

            foreach (var property in element.EnumerateObject())
            {
                var url = FindCoverUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var url = FindCoverUrl(item);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return null;
    }

    private static int GetDurationMs(JsonElement track)
    {
        if (TryGetProperty(track, "duration_ms", out var milliseconds) && milliseconds.TryGetInt32(out var ms))
            return ms;
        if (TryGetProperty(track, "duration", out var duration))
        {
            if (duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var durationNumber))
                return durationNumber;
            if (TryGetProperty(duration, "totalMilliseconds", out var totalMs) && totalMs.TryGetInt32(out var total))
                return total;
        }
        return 0;
    }

    // Helpers

    /// <summary>
    /// Витягує Track ID із посилання. Підтримує формати:
    ///   • https://open.spotify.com/track/6rqhFgbbKwnb9MLmUQDhG6
    ///   • https://open.spotify.com/track/6rqhFgbbKwnb9MLmUQDhG6?si=...
    ///   • spotify:track:6rqhFgbbKwnb9MLmUQDhG6
    /// </summary>
    private static string ParseTrackId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Spotify URL не може бути порожнім.");

        var match = SpotifyTrackIdRegex().Match(url);
        if (!match.Success)
            throw new ArgumentException(
                $"Не вдалося розпізнати Spotify Track ID у посиланні: {url}");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Запитує Spotify oEmbed API. Повертає назву треку та thumbnail.
    /// Кидає виняток якщо трек не знайдений (404).
    /// </summary>
    private async Task<OEmbedResult> FetchOEmbedAsync(string trackId)
    {
        var oembedUrl = $"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{trackId}";
        var response = await _httpClient.GetAsync(oembedUrl);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ArgumentException(
                $"Трек з ID '{trackId}' не знайдений на Spotify.");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Spotify oEmbed returned {StatusCode}", response.StatusCode);
            throw new HttpRequestException(
                $"Spotify oEmbed повернув помилку (HTTP {(int)response.StatusCode}).");
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());

        var root = doc.RootElement;

        return new OEmbedResult
        {
            Title = root.GetProperty("title").GetString() ?? "Невідомий трек",
            ThumbnailUrl = root.TryGetProperty("thumbnail_url", out var thumb)
                ? thumb.GetString()
                : null
        };
    }

    private async Task<string?> FetchOEmbedThumbnailAsync(string spotifyUrl)
    {
        try
        {
            var oembedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(spotifyUrl)}";
            using var response = await _httpClient.GetAsync(oembedUrl);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return doc.RootElement.TryGetProperty("thumbnail_url", out var thumbnail)
                ? thumbnail.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося отримати обкладинку колекції через Spotify oEmbed");
            return null;
        }
    }

    /// <summary>
    /// Завантажує HTML-сторінку треку з User-Agent Googlebot
    /// (Spotify віддає мета-теги тільки ботам) і парсить &lt;meta&gt; теги.
    /// </summary>
    private async Task<Dictionary<string, string>> FetchHtmlMetaAsync(string trackId)
    {
        var url = $"https://open.spotify.com/track/{trackId}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Spotify повертає повні OG/music мета-теги лише для пошукових ботів.
        request.Headers.UserAgent.ParseAdd(GetRandomUserAgent());

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("HTML page returned {StatusCode} for track {TrackId}, skipping meta",
                response.StatusCode, trackId);
            return new Dictionary<string, string>();
        }

        var html = await response.Content.ReadAsStringAsync();
        return ParseMetaTags(html);
    }

    /// <summary>
    /// Парсить усі &lt;meta&gt; теги з HTML і повертає словник property → content.
    /// </summary>
    private static Dictionary<string, string> ParseMetaTags(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MetaTagRegex().Matches(html))
        {
            // Підтримка обох порядків атрибутів у <meta> тезі.
            var prop = match.Groups["prop"].Success
                ? match.Groups["prop"].Value
                : match.Groups["prop2"].Value;

            var val = match.Groups["val"].Success
                ? match.Groups["val"].Value
                : match.Groups["val2"].Value;

            val = WebUtility.HtmlDecode(val);

            // Не перезаписуємо — перші значення мають пріоритет.
            result.TryAdd(prop, val);
        }

        return result;
    }

    /// <summary>
    /// Завантажує сторінку виконавця та парсить OG мета-теги
    /// для отримання аватарки, імені та опису.
    /// </summary>
    private async Task<SpotArtistDto> FetchArtistInfoAsync(string artistPageUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, artistPageUrl);
        request.Headers.UserAgent.ParseAdd(GetRandomUserAgent());

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var meta = ParseMetaTags(html);

        return new SpotArtistDto
        {
            Name = meta.GetValueOrDefault("og:title", "Невідомий виконавець"),
            AvatarUrl = meta.GetValueOrDefault("og:image"),
            Description = meta.GetValueOrDefault("og:description"),
            SpotifyUrl = meta.GetValueOrDefault("og:url", artistPageUrl)
        };
    }

    /// <summary>
    /// Внутрішня структура для результату oEmbed.
    /// </summary>
    private sealed class OEmbedResult
    {
        public string Title { get; init; } = "";
        public string? ThumbnailUrl { get; init; }
    }
}
