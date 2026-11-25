using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Corelight.Core.SystemInfo;

public enum RestorePointResult { Created, ProtectionDisabled, NotCreated, Failed }

internal interface ISystemRestoreAccess
{
    bool ContainsPoint(string description);
    uint Create(string description);
    uint Enable(string drive);
}

internal sealed class WindowsSystemRestoreAccess : ISystemRestoreAccess
{
    private static ManagementClass OpenClass() => new(
        new ManagementScope(@"\\.\root\default", new ConnectionOptions { EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate }),
        new ManagementPath("SystemRestore"), null);

    public uint Enable(string drive)
    {
        using var cls = OpenClass();
        using var args = cls.GetMethodParameters("Enable");
        args["Drive"] = drive;
        using var result = cls.InvokeMethod("Enable", args, new InvokeMethodOptions { Timeout = TimeSpan.FromMinutes(1) });
        return Convert.ToUInt32(result["ReturnValue"]);
    }

    public uint Create(string description)
    {
        using var cls = OpenClass();
        using var args = cls.GetMethodParameters("CreateRestorePoint");
        args["Description"] = description;
        args["RestorePointType"] = 12u; // MODIFY_SETTINGS
        args["EventType"] = 100u; // BEGIN_SYSTEM_CHANGE
        using var result = cls.InvokeMethod("CreateRestorePoint", args, new InvokeMethodOptions { Timeout = TimeSpan.FromMinutes(2) });
        return Convert.ToUInt32(result["ReturnValue"]);
    }

    public bool ContainsPoint(string description)
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT Description FROM SystemRestore");
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
            using (item)
                if (string.Equals(item["Description"] as string, description, StringComparison.Ordinal))
                    return true;
        return false;
    }
}

public sealed class RestorePointService
{
    private readonly ILogger<RestorePointService> _log;
    private readonly ISystemRestoreAccess _access;

    public RestorePointService(ILogger<RestorePointService> log) : this(log, new WindowsSystemRestoreAccess()) { }

    internal RestorePointService(ILogger<RestorePointService> log, ISystemRestoreAccess access)
        => (_log, _access) = (log, access);

    public string? LastError { get; private set; }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default) => Task.Run(() => Create(description, ct), ct);

    public async Task EnableProtectionAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            var code = _access.Enable(drive);
            if (code != 0) throw new InvalidOperationException($"Не удалось включить защиту системы (0x{code:X8}).");
        }, ct);
        // WMI Enable возвращает управление до завершения включения защиты.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    private RestorePointResult Create(string description, CancellationToken ct)
    {
        LastError = null;
        var request = $"{description} [{Guid.NewGuid():N}]";
        try
        {
            ct.ThrowIfCancellationRequested();
            var code = _access.Create(request);
            _log.LogInformation("SystemRestore.CreateRestorePoint: код 0x{Code:X8}, запрос {Request}", code, request);
            if (code is 1058u or 0x80070422u)
            {
                LastError = "Windows сообщает, что служба восстановления системы отключена (0x80070422). Включите защиту системы и повторите создание точки.";
                return RestorePointResult.ProtectionDisabled;
            }
            if (code != 0)
            {
                LastError = $"Windows вернула код 0x{code:X8}: {new Win32Exception(unchecked((int)code)).Message}";
                _log.LogWarning("Создание точки восстановления: {Reason}", LastError);
                return RestorePointResult.Failed;
            }
            // WMI can return before the new point becomes visible in the SystemRestore list.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (_access.ContainsPoint(request))
                {
                    _log.LogInformation("Создание точки восстановления подтверждено: {Request}", request);
                    return RestorePointResult.Created;
                }
                Thread.Sleep(500);
            }
            LastError = "Точка Corelight не появилась в списке Windows. Возможно, действует штатное ограничение частоты создания точек. Проверьте защиту системы и существующие точки.";
            _log.LogWarning("{Reason}", LastError);
            return RestorePointResult.NotCreated;
        }
        catch (COMException ex) when (unchecked((uint)ex.ErrorCode) == 0x80070422u)
        {
            LastError = "Windows сообщает, что служба восстановления системы отключена (0x80070422). Включите защиту системы и повторите создание точки.";
            _log.LogWarning(ex, "Защита системы выключена: {Reason}", LastError);
            return RestorePointResult.ProtectionDisabled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = string.IsNullOrWhiteSpace(ex.Message)
                ? $"Ошибка Windows 0x{unchecked((uint)ex.HResult):X8} при создании точки восстановления."
                : ex.Message;
            _log.LogWarning(ex, "Не удалось создать точку восстановления");
            return RestorePointResult.Failed;
        }

    }
}
