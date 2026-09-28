namespace SpotGet.Models;

/// <summary>
/// DTO з інформацією про виконавця Spotify.
/// </summary>
public class SpotArtistDto
{
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Description { get; set; }
    public string SpotifyUrl { get; set; } = string.Empty;
}
