using System.Collections.Concurrent;
using System.Text.Json;

namespace SpotGet.Services;

/// <summary>
/// Підраховує унікальних відвідувачів сайту за IP-адресою.
/// Зберігає дані у JSON-файл, щоб лічильник не скидався при перезапуску Docker.
/// </summary>
public class VisitorService
{
    private readonly ConcurrentDictionary<string, byte> _uniqueIps = new();
    private readonly string _dataFilePath;
    private readonly object _fileLock = new();

    public VisitorService()
    {
        // Зберігаємо файл у /app/data, який можна примонтувати як Docker volume
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _dataFilePath = Path.Combine(dataDir, "visitors.json");

        LoadFromFile();
    }

    /// <summary>
    /// Реєструє відвідувача за IP. Повертає загальну кількість унікальних відвідувачів.
    /// </summary>
    public int TrackVisitor(string? ipAddress)
    {
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            if (_uniqueIps.TryAdd(ipAddress, 0))
            {
                // Новий унікальний відвідувач — зберігаємо на диск
                SaveToFile();
            }
        }

        return _uniqueIps.Count;
    }

    /// <summary>
    /// Повертає поточну кількість унікальних відвідувачів без реєстрації нового.
    /// </summary>
    public int GetCount() => _uniqueIps.Count;

    private void LoadFromFile()
    {
        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = File.ReadAllText(_dataFilePath);
                var ips = JsonSerializer.Deserialize<string[]>(json);
                if (ips != null)
                {
                    foreach (var ip in ips)
                        _uniqueIps.TryAdd(ip, 0);
                }
            }
        }
        catch
        {
            // Якщо файл пошкоджений, починаємо з нуля
        }
    }

    private void SaveToFile()
    {
        lock (_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_uniqueIps.Keys.ToArray());
                File.WriteAllText(_dataFilePath, json);
            }
            catch
            {
                // Ігноруємо помилки запису (наприклад, недостатньо прав)
            }
        }
    }
}
