using Corelight.Core.SystemInfo;

namespace Corelight.Core.Platform;

public enum WindowsComponent
{
    Edge,
    Recall,
    HagsEnabled,
}

/// <summary>Наличие компонентов, которые нельзя определять по ключу их политики.</summary>
public interface IComponentPresence
{
    void Refresh();
    Task<bool> IsPresentAsync(WindowsComponent component, CancellationToken ct);
}

public sealed class WindowsComponentPresence(IRegistryAccess registry, IProcessRunner runner) : IComponentPresence
{
    private readonly object _gate = new();
    private Task<bool>? _recall;
    internal Func<string, bool> FileExists { get; init; } = File.Exists;

    public void Refresh() { lock (_gate) _recall = null; }

    public Task<bool> IsPresentAsync(WindowsComponent component, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (component)
        {
            case WindowsComponent.Edge:
                return Task.FromResult(EdgePaths().Any(FileExists));
            case WindowsComponent.Recall:
                lock (_gate) return _recall ??= RecallEnabledAsync(ct);
            case WindowsComponent.HagsEnabled:
                return Task.FromResult(HagsProbe.Read()?.Enabled
                    ?? throw new InvalidOperationException("Не удалось определить текущее состояние HAGS."));
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }
    }

    private IEnumerable<string> EdgePaths()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Environment.GetFolderPath(root);
            if (!string.IsNullOrEmpty(path)) yield return Path.Combine(path, "Microsoft", "Edge", "Application", "msedge.exe");
        }
        foreach (var key in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe" })
        {
            var path = registry.GetValue(key, "")?.Data;
            if (string.IsNullOrWhiteSpace(path)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (Path.IsPathFullyQualified(expanded) && !expanded.StartsWith(@"\\", StringComparison.Ordinal)
                && Path.GetFileName(expanded).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
                yield return expanded;
        }
    }

    private async Task<bool> RecallEnabledAsync(CancellationToken ct)
    {
        // Только чтение. Вывод enum не зависит от языка Windows; номер сборки не доказывает наличие Recall.
        var result = await runner.RunAsync(ProcessRunner.WindowsPowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; (Get-WindowsOptionalFeature -Online -FeatureName Recall -ErrorAction Stop).State.ToString()"],
            TimeSpan.FromSeconds(45), ct);
        if (!result.Succeeded)
            throw new InvalidOperationException("Не удалось проверить компонент Recall: " + result.Error);
        return ParseRecallState(result.Output);
    }

    internal static bool ParseRecallState(string output) => output.Trim() switch
    {
        "Enabled" => true,
        "Disabled" or "DisabledWithPayloadRemoved" or "NotPresent" => false,
        _ => throw new InvalidOperationException("Состояние Recall неизвестно или ожидает перезагрузки."),
    };
}
