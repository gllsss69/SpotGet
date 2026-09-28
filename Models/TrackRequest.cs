namespace SpotGet.Models;

/// <summary>
/// Тіло POST-запиту для ендпоінту /api/track-info.
/// </summary>
public class TrackRequest
{
    public string Url { get; set; } = string.Empty;
}
