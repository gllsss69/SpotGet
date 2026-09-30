using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace SpotGet.Services;

/// <summary>
/// Перевірка здоров'я системи: наявність yt-dlp, ffmpeg та вільний дисковий простір.
/// </summary>
public class SystemHealthCheck : IHealthCheck
{
    private readonly ILogger<SystemHealthCheck> _logger;
    private const long MinimumFreeBytes = 250 * 1024 * 1024; // 250 MB

    public SystemHealthCheck(ILogger<SystemHealthCheck> logger)
    {
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new System.Collections.Generic.Dictionary<string, object>();

        // 1. Перевірка вільного місця на диску в тимчасовій директорії
        try
        {
            var tempPath = Path.GetTempPath();
            var driveInfo = new DriveInfo(Path.GetPathRoot(tempPath) ?? "/");
            var freeBytes = driveInfo.AvailableFreeSpace;
            var freeMb = freeBytes / (1024 * 1024);

            data["disk_free_mb"] = freeMb;

            if (freeBytes < MinimumFreeBytes)
            {
                return HealthCheckResult.Unhealthy($"Мало вільного місця на диску: {freeMb} MB доступно (мінімум 250 MB).", data: data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося перевірити дисковий простір для HealthCheck.");
        }

        // 2. Перевірка yt-dlp
        var ytDlpOk = await CanRunCommandAsync("yt-dlp", "--version");
        data["yt_dlp_installed"] = ytDlpOk;
        if (!ytDlpOk)
        {
            return HealthCheckResult.Degraded("Утиліта yt-dlp не знайдена або не запускається.", data: data);
        }

        // 3. Перевірка ffmpeg
        var ffmpegOk = await CanRunCommandAsync("ffmpeg", "-version");
        data["ffmpeg_installed"] = ffmpegOk;
        if (!ffmpegOk)
        {
            return HealthCheckResult.Degraded("Утиліта ffmpeg не знайдена або не запускається.", data: data);
        }

        return HealthCheckResult.Healthy("Усі залежності в нормі.", data);
    }

    private static async Task<bool> CanRunCommandAsync(string command, string args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var delayTask = Task.Delay(5000);
            var waitForExitTask = process.WaitForExitAsync();

            if (await Task.WhenAny(waitForExitTask, delayTask) == waitForExitTask)
            {
                return process.ExitCode == 0;
            }

            process.Kill();
            return false;
        }
        catch
        {
            return false;
        }
    }
}
