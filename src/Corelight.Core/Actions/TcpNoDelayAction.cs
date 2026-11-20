using System.Text.Json;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>
/// Отключает алгоритм Нейгла (TcpAckFrequency=1, TCPNoDelay=1) на всех сетевых интерфейсах,
/// у которых есть IP-адрес. Набор интерфейсов на каждом ПК свой, поэтому это отдельное действие.
/// </summary>
public sealed class TcpNoDelayAction : TweakAction
{
    internal const string InterfacesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private static readonly string[] ValueNames = ["TcpAckFrequency", "TCPNoDelay"];

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var interfaces = ActiveInterfaces(p.Registry);
        if (interfaces.Count == 0)
            return Task.FromResult(ActionState.NotApplicable);

        var target = RegValue.DWord(1);
        var applied = interfaces.All(key => ValueNames.All(name => target.SameAs(p.Registry.GetValue(key, name))));
        return Task.FromResult(applied ? ActionState.Applied : ActionState.NotApplied);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        var snapshot = ActiveInterfaces(p.Registry).ToDictionary(
            key => key,
            key => ValueNames.ToDictionary(name => name, name => p.Registry.GetValue(key, name)));
        return Task.FromResult<string?>(JsonSerializer.Serialize(snapshot));
    }

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        foreach (var key in ActiveInterfaces(p.Registry))
            foreach (var name in ValueNames)
                p.Registry.SetValue(key, name, RegValue.DWord(1));
        return Task.CompletedTask;
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        if (captured is null)
            return Task.CompletedTask;

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RegValue?>>>(captured) ?? [];
        foreach (var (key, values) in snapshot)
            foreach (var (name, value) in values)
                RegistryValueAction.Restore(p.Registry, key, name, value);
        return Task.CompletedTask;
    }

    public override string Describe() => @"Tcpip\Parameters\Interfaces\*\TcpAckFrequency, TCPNoDelay";

    private static List<string> ActiveInterfaces(IRegistryAccess registry) =>
        registry.GetSubKeyNames(InterfacesKey)
            .Select(name => $@"{InterfacesKey}\{name}")
            .Where(key => HasAddress(registry.GetValue(key, "DhcpIPAddress")) || HasAddress(registry.GetValue(key, "IPAddress")))
            .ToList();

    private static bool HasAddress(RegValue? value) =>
        value is not null && value.Data.Split('\0').Any(ip => ip.Length > 0 && ip != "0.0.0.0");
}
