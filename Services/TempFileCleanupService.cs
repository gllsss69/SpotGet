using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SpotGet.Services;

/// <summary>
/// Фоновий сервіс для автоматичного прибирання застарілих тимчасових папок завантаження (spotget_*).
/// Запобігає переповненню диска при раптових помилках завантаження або обривах з'єднання.
/// </summary>
public class TempFileCleanupService : BackgroundService
{
    private readonly ILogger<TempFileCleanupService> _logger;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromMinutes(30);

    public TempFileCleanupService(ILogger<TempFileCleanupService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TempFileCleanupService запущено. Інтервал прибирання: {Interval}", CleanupInterval);

        // Початкове прибирання при старті застосунку
        CleanupStaleTempDirectories();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
                CleanupStaleTempDirectories();
            }
            catch (OperationCanceledException)
            {
                // Завершення роботи сервісу
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Помилка під час виконання регулярного прибирання тимчасових файлів.");
            }
        }
    }

    private void CleanupStaleTempDirectories()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var directories = Directory.GetDirectories(tempPath, "spotget_*");
            var now = DateTime.UtcNow;
            int deletedCount = 0;

            foreach (var dir in directories)
            {
                try
                {
                    var activeMarker = Path.Combine(dir, ".in-progress");
                    if (File.Exists(activeMarker))
                    {
                        if (now - File.GetLastWriteTimeUtc(activeMarker) > MaxFileAge)
                        {
                            Directory.Delete(dir, recursive: true);
                            deletedCount++;
                        }
                        continue;
                    }

                    var dirInfo = new DirectoryInfo(dir);
                    var age = now - dirInfo.LastWriteTimeUtc;

                    if (age > MaxFileAge)
                    {
                        Directory.Delete(dir, recursive: true);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не вдалося видалити застарілу тимчасову директорію {Dir}", dir);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Очищено {Count} застарілих тимчасових директорій spotget_*.", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не вдалося отримати список тимчасових директорій для очищення.");
        }
    }
}
