using System.Management;
using Microsoft.Extensions.Logging;
using Corelight.Core.Platform;

namespace Corelight.Core.SystemInfo;

public enum RestorePointResult
{
    Created,
    /// <summary>Защита системы выключена — точку можно создать после её включения.</summary>
    ProtectionDisabled,
    Failed,
}

/// <summary>Создание точки восстановления Windows через WMI (класс SystemRestore).</summary>
public sealed class RestorePointService(IRegistryAccess registry, ILogger<RestorePointService> log)
{
    private const string SystemRestoreKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default) =>
        Task.Run(() => Create(description), ct);

    /// <summary>Включает защиту системы для системного диска.</summary>
    public Task EnableProtectionAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
        using var args = cls.GetMethodParameters("Enable");
        args["Drive"] = drive;
        using var result = cls.InvokeMethod("Enable", args, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code != 0)
            throw new InvalidOperationException($"Не удалось включить защиту системы (код {code}).");
    }, ct);

    private RestorePointResult Create(string description)
    {
        // Windows по умолчанию создаёт не больше одной точки в сутки и молча пропускает остальные.
        // На время вызова снимаем это ограничение и затем возвращаем исходное значение.
        var originalFrequency = registry.GetValue(SystemRestoreKey, FrequencyValue);
        try
        {
            registry.SetValue(SystemRestoreKey, FrequencyValue, RegValue.DWord(0));
            var before = LatestSequenceNumber();

            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var args = cls.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = ModifySettings;
            args["EventType"] = BeginSystemChange;
            using var result = cls.InvokeMethod("CreateRestorePoint", args, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);

            if (code == ErrorServiceDisabled)
                return RestorePointResult.ProtectionDisabled;
            if (code != 0)
            {
                log.LogWarning("CreateRestorePoint вернул код {Code}", code);
                return RestorePointResult.Failed;
            }
            // Код 0 не гарантирует, что точка создана: при выключенной защите Windows тоже может вернуть 0.
            return LatestSequenceNumber() > before ? RestorePointResult.Created : RestorePointResult.ProtectionDisabled;
        }
        catch (Exception ex)
        {
            // WMI может выбросить не только ManagementException (COMException, UnauthorizedAccessException).
            // В любом случае пользователь сам решит, продолжать ли без точки восстановления.
            log.LogWarning(ex, "Не удалось создать точку восстановления");
            return RestorePointResult.Failed;
        }
        finally
        {
            if (originalFrequency is null)
                registry.DeleteValue(SystemRestoreKey, FrequencyValue);
            else
                registry.SetValue(SystemRestoreKey, FrequencyValue, originalFrequency);
        }
    }

    // Сравниваем номера, а не количество: при нехватке места Windows удаляет старые точки.
    private static uint LatestSequenceNumber()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>()
                .Select(o => Convert.ToUInt32(o["SequenceNumber"]))
                .DefaultIfEmpty(0u)
                .Max();
        }
        catch (ManagementException)
        {
            return 0;
        }
    }
}
