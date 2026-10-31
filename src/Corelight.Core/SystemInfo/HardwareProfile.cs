namespace Corelight.Core.SystemInfo;

public enum DiskKind
{
    Unknown,
    Hdd,
    Ssd,
}

public sealed record HardwareProfile
{
    /// <summary>Условия, которые можно использовать в полях recommendWhen и requires каталога.</summary>
    public static readonly IReadOnlySet<string> KnownConditions = new HashSet<string>(StringComparer.Ordinal)
    {
        "desktop", "laptop", "noPrinter", "noBluetooth", "ssd", "hdd", "hagsSupported",
    };

    public string CpuName { get; init; } = "";
    public IReadOnlyList<string> Gpus { get; init; } = [];
    public ulong TotalMemoryBytes { get; init; }
    public string OsName { get; init; } = "";
    public string OsDisplayVersion { get; init; } = "";
    public int OsBuild { get; init; }
    public int OsRevision { get; init; }
    public bool HasBattery { get; init; }
    public bool HasPrinter { get; init; }
    public bool HasBluetooth { get; init; }
    public DiskKind SystemDisk { get; init; }

    /// <summary>null — определить не удалось.</summary>
    public bool? HagsSupported { get; init; }

    public bool Satisfies(string condition) => condition switch
    {
        "desktop" => !HasBattery,
        "laptop" => HasBattery,
        "noPrinter" => !HasPrinter,
        "noBluetooth" => !HasBluetooth,
        "ssd" => SystemDisk == DiskKind.Ssd,
        "hdd" => SystemDisk == DiskKind.Hdd,
        "hagsSupported" => HagsSupported == true,
        _ => false,
    };
}
