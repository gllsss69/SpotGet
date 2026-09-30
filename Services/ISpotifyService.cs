using SpotGet.Models;

namespace SpotGet.Services;

/// <summary>
/// Інтерфейс для роботи зі Spotify Web API.
/// </summary>
public interface ISpotifyService
{
    /// <summary>
    /// Отримує метадані треку за Spotify-посиланням.
    /// </summary>
    /// <param name="spotifyUrl">Повне посилання на трек (https://open.spotify.com/track/...).</param>
    /// <returns>Об'єкт SpotTrackDto з метаданими.</returns>
    Task<SpotTrackDto> GetTrackInfoAsync(string spotifyUrl);

    /// <summary>
    /// Gets the track list for a Spotify album or playlist URL.
    /// </summary>
    Task<SpotCollectionDto> GetCollectionInfoAsync(string spotifyUrl);
}
