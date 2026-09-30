namespace SpotGet.Models;

/// <summary>
/// DTO з метаданими Spotify-треку.
/// </summary>
public class SpotTrackDto
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? PreviewUrl { get; set; }
    public int DurationMs { get; set; }
    public uint TrackNumber { get; set; }
    public uint TrackCount { get; set; }
    public string SpotifyUrl { get; set; } = string.Empty;
    public SpotArtistDto? ArtistInfo { get; set; }
}
