using System.Runtime.InteropServices;

namespace Corelight.Core.Platform;

public interface IScheduledTaskControl
{
    /// <returns>null, если задачи нет.</returns>
    bool? IsEnabled(string taskPath);

    void SetEnabled(string taskPath, bool enabled);
}

/// <summary>Планировщик заданий через COM (Schedule.Service): не зависит от языка системы, в отличие от вывода schtasks.</summary>
public sealed class WindowsScheduledTaskControl : IScheduledTaskControl
{
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    public bool? IsEnabled(string taskPath)
    {
        var task = GetTask(taskPath);
        return task is null ? null : (bool)task.Enabled;
    }

    public void SetEnabled(string taskPath, bool enabled)
    {
        var task = GetTask(taskPath) ?? throw new InvalidOperationException($"Задача {taskPath} не найдена.");
        task.Enabled = enabled;
    }

    private static dynamic? GetTask(string taskPath)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Служба планировщика заданий недоступна.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        var slash = taskPath.LastIndexOf('\\');
        var folderPath = slash <= 0 ? "\\" : taskPath[..slash];
        var name = taskPath[(slash + 1)..];
        try
        {
            dynamic folder = service.GetFolder(folderPath);
            return folder.GetTask(name);
        }
        // COM-привязка .NET превращает эти HRESULT в FileNotFoundException / DirectoryNotFoundException,
        // а не в COMException, поэтому проверяем сам код.
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            return null;
        }
    }
}
