using System.ComponentModel;
using System.Management;
using Microsoft.Extensions.Logging;
using Corelight.Core.Platform;

namespace Corelight.Core.SystemInfo;

public enum RestorePointResult { Created, ProtectionDisabled, NotCreated, Failed }

internal interface ISystemRestoreAccess
{
    uint LatestSequenceNumber();
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

    public uint LatestSequenceNumber()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
        using var results = searcher.Get();
        uint latest = 0;
        foreach (ManagementBaseObject item in results)
            using (item) latest = Math.Max(latest, Convert.ToUInt32(item["SequenceNumber"]));
        return latest;
    }
}

public sealed class RestorePointService
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string Frequency = "SystemRestorePointCreationFrequency";
    private readonly IRegistryAccess _registry;
    private readonly ILogger<RestorePointService> _log;
    private readonly ISystemRestoreAccess _access;

    public RestorePointService(IRegistryAccess registry, ILogger<RestorePointService> log)
        : this(registry, log, new WindowsSystemRestoreAccess()) { }

    internal RestorePointService(IRegistryAccess registry, ILogger<RestorePointService> log, ISystemRestoreAccess access)
        => (_registry, _log, _access) = (registry, log, access);

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
        RegValue? original = null;
        var changed = false;
        try
        {
            original = _registry.GetValue(Key, Frequency);
            _registry.SetValue(Key, Frequency, RegValue.DWord(0));
            changed = true;
            var before = _access.LatestSequenceNumber();
            var code = _access.Create(description);
            if (code is 1058u or 0x80070422u) return RestorePointResult.ProtectionDisabled;
            if (code != 0)
            {
                LastError = $"Windows вернула код 0x{code:X8}: {new Win32Exception(unchecked((int)code)).Message}";
                _log.LogWarning("Создание точки восстановления: {Reason}", LastError);
                return RestorePointResult.Failed;
            }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (_access.LatestSequenceNumber() > before) return RestorePointResult.Created;
                Thread.Sleep(200);
            }
            LastError = "Windows завершила запрос, но новая точка восстановления не появилась. Возможно, защита системы ещё не включена.";
            _log.LogWarning("{Reason}", LastError);
            return RestorePointResult.NotCreated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            _log.LogWarning(ex, "Не удалось создать точку восстановления");
            return RestorePointResult.Failed;
        }
        finally
        {
            if (changed)
            {
                try
                {
                    if (original is null) _registry.DeleteValue(Key, Frequency);
                    else _registry.SetValue(Key, Frequency, original);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Не удалось вернуть частоту создания точек восстановления"); }
            }
        }
    }
}
