using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace Corelight.Core.Platform;

public enum ServiceStartType
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

public interface IServiceControl
{
    /// <returns>null, если службы нет в системе.</returns>
    ServiceStartType? GetStartType(string serviceName);
    /// <returns>true, если служба ещё работает или останавливается; false, если остановлена.</returns>
    bool IsRunning(string serviceName);

    Task SetStartTypeAsync(string serviceName, ServiceStartType startType, CancellationToken ct = default);

    /// <summary>Пытается остановить службу; ошибки не пробрасываются (служба отключится после перезагрузки).</summary>
    bool TryStop(string serviceName);

    bool TryStart(string serviceName);
}

public sealed class WindowsServiceControl(IRegistryAccess registry, IProcessRunner runner, ILogger<WindowsServiceControl> log) : IServiceControl
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    public ServiceStartType? GetStartType(string serviceName)
    {
        var start = registry.GetValue(ServicesKey + serviceName, "Start");
        if (start is null)
            return null;
        if (start.Kind != Microsoft.Win32.RegistryValueKind.DWord)
            throw new InvalidDataException($"Некорректный тип параметра Start службы {serviceName}.");

        return uint.Parse(start.Normalize().Data) switch
        {
            0 => ServiceStartType.Boot,
            1 => ServiceStartType.System,
            2 when registry.GetValue(ServicesKey + serviceName, "DelayedAutostart")?.Normalize().Data == "1" => ServiceStartType.AutomaticDelayed,
            2 => ServiceStartType.Automatic,
            3 => ServiceStartType.Manual,
            4 => ServiceStartType.Disabled,
            _ => throw new InvalidDataException($"Неизвестный тип запуска службы {serviceName}."),
        };
    }

    public bool IsRunning(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        return sc.Status != ServiceControllerStatus.Stopped;
    }

    public async Task SetStartTypeAsync(string serviceName, ServiceStartType startType, CancellationToken ct = default)
    {
        // sc.exe идёт через диспетчер служб, поэтому изменение вступает в силу сразу, без перезагрузки.
        var mode = startType switch
        {
            ServiceStartType.Boot => "boot",
            ServiceStartType.System => "system",
            ServiceStartType.Automatic => "auto",
            ServiceStartType.AutomaticDelayed => "delayed-auto",
            ServiceStartType.Manual => "demand",
            ServiceStartType.Disabled => "disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(startType)),
        };
        var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"),
            ["config", serviceName, "start=", mode], TimeSpan.FromSeconds(30), ct);
        // Код выхода sc.exe — это код ошибки Win32, у которого есть готовое описание на языке системы.
        if (!result.Succeeded)
            throw new InvalidOperationException($"Не удалось изменить тип запуска службы {serviceName}: {new Win32Exception(result.ExitCode).Message}");
    }

    public bool TryStop(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped) return true;
            if (sc.Status != ServiceControllerStatus.StopPending)
            {
                if (!sc.CanStop) return false;
                sc.Stop(stopDependentServices: false);
            }
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            log.LogWarning(ex, "Служба {Service} не остановлена сразу; изменения вступят в силу после перезагрузки", serviceName);
            return false;
        }
    }

    public bool TryStart(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running) return true;
            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            log.LogWarning(ex, "Не удалось запустить службу {Service}", serviceName);
            return false;
        }
    }
}
