namespace SpotGet.Models;

/// <summary>
/// A Spotify album or playlist and its track list.
/// </summary>
public class SpotCollectionDto
{
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SpotifyUrl { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public SpotArtistDto? ArtistInfo { get; set; }
    public List<SpotArtistDto> CreatorInfos { get; set; } = [];
    public List<SpotTrackDto> Tracks { get; set; } = [];
}
