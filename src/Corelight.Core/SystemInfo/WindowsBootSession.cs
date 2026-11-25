using System.Management;

namespace Corelight.Core.SystemInfo;

internal static class WindowsBootSession
{
    private static readonly Lazy<DateTimeOffset> Current = new(ReadBootTime);

    internal static DateTimeOffset Read() => Current.Value;

    private static DateTimeOffset ReadBootTime()
    {
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        using var results = query.Get();
        foreach (ManagementBaseObject row in results)
        {
            using (row)
                if (row["LastBootUpTime"] is string bootTime && bootTime.Length > 0)
                    return ManagementDateTimeConverter.ToDateTime(bootTime).ToUniversalTime();
        }
        throw new InvalidOperationException("Windows не сообщила время загрузки.");
    }
}
