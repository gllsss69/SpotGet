using System.Threading;

namespace SpotGet.Services;

/// <summary>
/// Обмежує кількість одночасних завантажень з YouTube.
/// Якщо ліміт досягнуто, нові запити чекають у черзі.
/// </summary>
public class DownloadQueue
{
    private readonly SemaphoreSlim _semaphore;

    public DownloadQueue(int maxConcurrent = 3)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public int AvailableSlots => _semaphore.CurrentCount;

    /// <summary>
    /// Виконує задачу завантаження в рамках обмеження.
    /// Якщо всі слоти зайняті, запит чекає своєї черги.
    /// </summary>
    public async Task<T> EnqueueAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
