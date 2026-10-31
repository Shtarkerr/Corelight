namespace Corelight.Core;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corelight");

    public static string BackupFile => Path.Combine(DataDirectory, "backup.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
