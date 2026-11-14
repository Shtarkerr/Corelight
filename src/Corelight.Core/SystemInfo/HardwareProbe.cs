using System.Management;
using Microsoft.Extensions.Logging;
using Corelight.Core.Platform;

namespace Corelight.Core.SystemInfo;

/// <summary>Собирает сведения о системе через WMI и реестр. Каждый пункт опрашивается отдельно, чтобы сбой одного не ломал остальные.</summary>
public sealed class HardwareProbe(IRegistryAccess registry, ILogger<HardwareProbe> log)
{
    private const string CurrentVersionKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // Виртуальные принтеры, которые есть почти в любой системе и не означают, что принтер нужен.
    private static readonly string[] VirtualPrinterPorts = ["PORTPROMPT:", "SHRFAX:", "nul:", "XPSPort:"];
    private static readonly string[] VirtualPrinterNames = ["Microsoft Print to PDF", "Microsoft XPS", "OneNote", "Fax", "Факс"];

    public HardwareProfile Probe()
    {
        var build = ParseInt(registry.GetValue(CurrentVersionKey, "CurrentBuildNumber")?.Data);
        var edition = registry.GetValue(CurrentVersionKey, "EditionID")?.Data ?? "";
        return new HardwareProfile
        {
            CpuName = Try(() => Query("SELECT Name FROM Win32_Processor").Select(o => Convert.ToString(o["Name"])?.Trim()).FirstOrDefault(), "CPU") ?? "Неизвестно",
            Gpus = Try(() => Query("SELECT Name FROM Win32_VideoController").Select(o => Convert.ToString(o["Name"]) ?? "Неизвестно").ToList(), "GPU") ?? [],
            TotalMemoryBytes = Try(() => Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Select(o => Convert.ToUInt64(o["TotalPhysicalMemory"])).FirstOrDefault(), "RAM"),
            // ProductName в реестре Windows 11 по-прежнему пишет «Windows 10», поэтому ориентируемся на номер сборки.
            OsName = $"Windows {(build >= 22000 ? "11" : "10")} {edition}".Trim(),
            OsDisplayVersion = registry.GetValue(CurrentVersionKey, "DisplayVersion")?.Data ?? "",
            OsBuild = build,
            OsRevision = ParseInt(registry.GetValue(CurrentVersionKey, "UBR")?.Normalize().Data),
            HasBattery = Try<bool?>(() => Query("SELECT DeviceID FROM Win32_Battery").Count > 0, "батарея"),
            HasPrinter = Try<bool?>(() => HasRealPrinter(), "принтеры"),
            HasBluetooth = Try<bool?>(() => Query("SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'").Count > 0, "Bluetooth"),
            SystemDisk = Try(GetSystemDiskKind, "диск"),
            HagsSupported = Try(HagsProbe.IsSupported, "HAGS"),
        };
    }

    private static bool HasRealPrinter() =>
        Query("SELECT Name, PortName FROM Win32_Printer").Any(o =>
        {
            var name = (string?)o["Name"] ?? "";
            var port = (string?)o["PortName"] ?? "";
            return !VirtualPrinterPorts.Any(p => port.Equals(p, StringComparison.OrdinalIgnoreCase))
                && !VirtualPrinterNames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        });

    private static DiskKind GetSystemDiskKind()
    {
        const string storage = @"root\Microsoft\Windows\Storage";
        var letter = Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0];
        var diskNumber = Query($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'", storage)
            .Select(o => Convert.ToUInt32(o["DiskNumber"]))
            .FirstOrDefault(uint.MaxValue);
        if (diskNumber == uint.MaxValue)
            return DiskKind.Unknown;

        var mediaType = Query($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'", storage)
            .Select(o => Convert.ToUInt16(o["MediaType"]))
            .FirstOrDefault();
        return mediaType switch
        {
            3 => DiskKind.Hdd,
            4 => DiskKind.Ssd,
            _ => DiskKind.Unknown,
        };
    }

    private static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();
        var rows = new List<Dictionary<string, object?>>();
        foreach (ManagementBaseObject item in results)
            using (item)
                rows.Add(item.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
        return rows;
    }

    private T? Try<T>(Func<T> probe, string what)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось определить: {What}", what);
            return default;
        }
    }

    private static int ParseInt(string? value) => int.TryParse(value, out var n) ? n : 0;
}
