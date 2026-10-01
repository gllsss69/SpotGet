using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpotGet.Models;

namespace SpotGet.Services;

public sealed record CollectionDownloadJobStatus(
    string Status,
    int Progress,
    int Total,
    int Skipped,
    string? FileName,
    string? Error);

public sealed record CollectionDownloadArtifact(string Path, string FileName, int Skipped, string Directory);

/// <summary>Processes long collection downloads outside the HTTP request lifetime.</summary>
public sealed class CollectionDownloadJobService : BackgroundService
{
    private readonly Channel<DownloadJob> _queue = Channel.CreateBounded<DownloadJob>(5);
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CollectionDownloadJobService> _logger;

    public CollectionDownloadJobService(IServiceScopeFactory scopeFactory, ILogger<CollectionDownloadJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Guid Enqueue(string url)
    {
        PruneExpiredJobs();
        var job = new DownloadJob(Guid.NewGuid(), url);
        _jobs[job.Id] = job;
        if (!_queue.Writer.TryWrite(job))
        {
            _jobs.TryRemove(job.Id, out _);
            throw new InvalidOperationException("Не вдалося додати завантаження до черги.");
        }
        return job.Id;
    }

    public CollectionDownloadJobStatus? GetStatus(Guid id)
    {
        PruneExpiredJobs();
        return _jobs.TryGetValue(id, out var job) ? job.GetStatus() : null;
    }

    public CollectionDownloadArtifact? GetArtifact(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return null;
        lock (job.Sync)
        {
            if (job.Status != "completed" || job.ZipPath is null || !File.Exists(job.ZipPath)) return null;
            return new CollectionDownloadArtifact(job.ZipPath, job.FileName!, job.Skipped, job.TempDirectory!);
        }
    }

    public bool TryBeginFileTransfer(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        lock (job.Sync)
        {
            if (job.Status != "completed") return false;
            job.SetStatus("sending");
            return true;
        }
    }

    public void MarkFileDelivered(Guid id, string directory)
    {
        if (_jobs.TryGetValue(id, out var job)) job.SetStatus("delivered");
        DeleteDirectory(directory, id);
    }

    public void Forget(Guid id, string? directory = null)
    {
        _jobs.TryRemove(id, out _);
        DeleteDirectory(directory, id);
    }

    private void DeleteDirectory(string? directory, Guid id)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Не вдалося прибрати завершене завантаження {JobId}", id); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessAsync(job, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Fail("Сервер зупинився під час завантаження.");
                DeleteDirectory(job.OutputDirectory, job.Id);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не вдалося виконати завантаження колекції {JobId}", job.Id);
                job.Fail(ex.Message);
                DeleteDirectory(job.OutputDirectory, job.Id);
            }
        }
    }

    private async Task ProcessAsync(DownloadJob job, CancellationToken stoppingToken)
    {
        job.SetStatus("preparing");
        using var scope = _scopeFactory.CreateScope();
        var spotify = scope.ServiceProvider.GetRequiredService<ISpotifyService>();
        var downloader = scope.ServiceProvider.GetRequiredService<IDownloadService>();
        var queue = scope.ServiceProvider.GetRequiredService<DownloadQueue>();
        var collection = await spotify.GetCollectionInfoAsync(job.Url);
        if (collection.Tracks.Count > 1000)
            throw new InvalidOperationException("За один раз можна завантажити не більше 1000 треків.");

        job.SetStatus("downloading");
        job.SetProgress(0, collection.Tracks.Count);
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"spotget_job_{job.Id:N}");
        Directory.CreateDirectory(tempDirectory);
        var activeMarker = Path.Combine(tempDirectory, ".in-progress");
        await File.WriteAllTextAsync(activeMarker, string.Empty, stoppingToken);
        job.SetOutputDirectory(tempDirectory);
        var zipPath = Path.Combine(tempDirectory, "collection.zip");
        var skippedTracks = new List<string>();
        var completed = 0;

        var results = await Task.WhenAll(collection.Tracks.Select(async (track, index) =>
        {
            try
            {
                var trackPath = await queue.EnqueueAsync(async () =>
                {
                    if (string.Equals(collection.Type, "playlist", StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(track.CoverUrl))
                    {
                        try
                        {
                            track.CoverUrl = await spotify.GetTrackCoverUrlAsync(track.SpotifyUrl)
                                ?? collection.CoverUrl;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Не вдалося отримати обкладинку треку {Title}; використовую обкладинку плейліста", track.Title);
                            track.CoverUrl = collection.CoverUrl;
                        }
                    }
                    return await downloader.DownloadAndTagTrackAsync(track);
                }, stoppingToken);
                var retainedPath = Path.Combine(tempDirectory, $"track_{index:D4}.mp3");
                File.Move(trackPath, retainedPath, overwrite: true);
                try
                {
                    var sourceDirectory = Path.GetDirectoryName(trackPath);
                    if (sourceDirectory is not null && Path.GetFileName(sourceDirectory).StartsWith("spotget_", StringComparison.Ordinal))
                        Directory.Delete(sourceDirectory, recursive: true);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Не вдалося прибрати тимчасовий файл треку {Title}", track.Title); }
                return (Path: (string?)retainedPath, Failure: (string?)null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var failure = $"{index + 1:00}. {track.Artist} - {track.Title}: {ex.Message}";
                _logger.LogWarning(ex, "Не вдалося завантажити трек {TrackNumber}: {Title}", index + 1, track.Title);
                return (Path: (string?)null, Failure: failure);
            }
            finally
            {
                job.SetProgress(Interlocked.Increment(ref completed), collection.Tracks.Count);
            }
        }));

        var downloaded = 0;
        job.SetStatus("packing");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (var i = 0; i < collection.Tracks.Count; i++)
            {
                var trackPath = results[i].Path;
                var track = collection.Tracks[i];
                if (trackPath is null)
                {
                    skippedTracks.Add(results[i].Failure!);
                    continue;
                }

                try
                {
                    var entry = archive.CreateEntry($"{i + 1:00} - {SafeFilePart(track.Title)}.mp3", CompressionLevel.Fastest);
                    await using var source = File.OpenRead(trackPath);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, stoppingToken);
                    downloaded++;
                }
                catch (Exception ex)
                {
                    skippedTracks.Add($"{i + 1:00}. {track.Artist} - {track.Title}: {ex.Message}");
                    _logger.LogWarning(ex, "Не вдалося додати трек {TrackNumber} до ZIP: {Title}", i + 1, track.Title);
                }
            }

            if (downloaded == 0)
                throw new InvalidOperationException("Не вдалося завантажити жодного треку з цієї колекції.");

            if (skippedTracks.Count > 0)
            {
                var report = archive.CreateEntry("_download-report.txt", CompressionLevel.Fastest);
                await using var reportStream = report.Open();
                await using var writer = new StreamWriter(reportStream);
                await writer.WriteLineAsync("Деякі треки не вдалося завантажити:");
                foreach (var failure in skippedTracks)
                    await writer.WriteLineAsync(failure);
            }
        }

        job.Complete(zipPath, $"{SafeFilePart(collection.Title)}.zip", skippedTracks.Count, tempDirectory);
    }

    private void PruneExpiredJobs()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var pair in _jobs)
        {
            if (!pair.Value.IsExpired(expiry)) continue;
            if (_jobs.TryRemove(pair.Key, out var job))
                Forget(job.Id, job.OutputDirectory);
        }
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(c => invalid.Contains(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Spotify collection" : safe[..Math.Min(safe.Length, 100)];
    }

    private sealed class DownloadJob(Guid id, string url)
    {
        public object Sync { get; } = new();
        public Guid Id { get; } = id;
        public string Url { get; } = url;
        public string Status { get; private set; } = "queued";
        public int Progress { get; private set; }
        public int Total { get; private set; }
        public int Skipped { get; private set; }
        public string? FileName { get; private set; }
        public string? Error { get; private set; }
        public string? ZipPath { get; private set; }
        public string? TempDirectory { get; private set; }
        public string? OutputDirectory => TempDirectory;
        private DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public void SetStatus(string status) { lock (Sync) { Status = status; UpdatedAt = DateTimeOffset.UtcNow; } }
        public void SetProgress(int progress, int total)
        {
            lock (Sync)
            {
                Progress = progress;
                Total = total;
                UpdatedAt = DateTimeOffset.UtcNow;
                if (TempDirectory is not null)
                {
                    var marker = Path.Combine(TempDirectory, ".in-progress");
                    try { if (File.Exists(marker)) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow); }
                    catch (IOException) { }
                }
            }
        }
        public void SetOutputDirectory(string directory) { lock (Sync) { TempDirectory = directory; } }
        public void Complete(string zipPath, string fileName, int skipped, string directory)
        {
            lock (Sync)
            {
                ZipPath = zipPath;
                FileName = fileName;
                Skipped = skipped;
                TempDirectory = directory;
                Status = "completed";
                UpdatedAt = DateTimeOffset.UtcNow;
                TouchMarker();
            }
        }
        public void Fail(string error) { lock (Sync) { Status = "failed"; Error = error; UpdatedAt = DateTimeOffset.UtcNow; } }
        public bool IsExpired(DateTimeOffset expiry) { lock (Sync) return UpdatedAt < expiry; }
        public CollectionDownloadJobStatus GetStatus()
        {
            lock (Sync)
            {
                if (Status == "sending") TouchMarker();
                return new(Status, Progress, Total, Skipped, FileName, Error);
            }
        }

        private void TouchMarker()
        {
            if (TempDirectory is null) return;
            var marker = Path.Combine(TempDirectory, ".in-progress");
            try { if (File.Exists(marker)) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow); }
            catch (IOException) { }
        }
    }
}
