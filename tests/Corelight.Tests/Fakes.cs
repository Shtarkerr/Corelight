using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Backup;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

internal sealed class FakeRegistry : IRegistryAccess
{
    public Dictionary<(string Key, string Name), RegValue> Values { get; } = new(new KeyComparer());
    public bool DenyWrites { get; set; }

    public RegValue? GetValue(string keyPath, string name) => Values.GetValueOrDefault((keyPath, name));

    public void SetValue(string keyPath, string name, RegValue value)
    {
        if (DenyWrites) throw new UnauthorizedAccessException("Attempted to perform an unauthorized operation.");
        Values[(keyPath, name)] = value;
    }

    public void DeleteValue(string keyPath, string name) => Values.Remove((keyPath, name));

    public IReadOnlyList<string> GetValueNames(string keyPath) =>
        Values.Keys.Where(k => k.Key.Equals(keyPath, StringComparison.OrdinalIgnoreCase)).Select(k => k.Name).ToList();

    public IReadOnlyList<string> GetSubKeyNames(string keyPath) =>
        Values.Keys
            .Where(k => k.Key.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Key[(keyPath.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class KeyComparer : IEqualityComparer<(string Key, string Name)>
    {
        public bool Equals((string Key, string Name) x, (string Key, string Name) y) =>
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Name) obj) =>
            HashCode.Combine(obj.Key.ToUpperInvariant(), obj.Name.ToUpperInvariant());
    }
}

internal sealed class FakeServices : IServiceControl
{
    public Dictionary<string, ServiceStartType> StartTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CannotStop { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CannotStart { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceStartType? GetStartType(string serviceName) =>
        StartTypes.TryGetValue(serviceName, out var t) ? t : null;

    public Task SetStartTypeAsync(string serviceName, ServiceStartType startType, CancellationToken ct = default)
    {
        if (Failing.Contains(serviceName))
            throw new InvalidOperationException($"Отказано в доступе: {serviceName}");
        StartTypes[serviceName] = startType;
        return Task.CompletedTask;
    }

    public bool IsRunning(string serviceName) => Running.Contains(serviceName);
    public bool TryStop(string serviceName)
    {
        if (CannotStop.Contains(serviceName) && Running.Contains(serviceName)) return false;
        Running.Remove(serviceName);
        return true;
    }

    public bool TryStart(string serviceName) { if (CannotStart.Contains(serviceName)) return false; Running.Add(serviceName); return true; }
}

internal sealed class FakeTasks : IScheduledTaskControl
{
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? IsEnabled(string taskPath) => Tasks.TryGetValue(taskPath, out var e) ? e : null;

    public void SetEnabled(string taskPath, bool enabled)
    {
        if (Protected.Contains(taskPath))
            throw new UnauthorizedAccessException("Отказано в доступе");
        Tasks[taskPath] = enabled;
    }
}

internal sealed class FakeApps : IAppxControl
{
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> StoreInstalls { get; } = [];
    public Dictionary<string, string> StorePackages { get; } = [];

    public void Refresh() { }

    public bool IsInstalled(string packageName) => Installed.Contains(packageName);

    public Task RemoveAsync(string packageName, CancellationToken ct = default)
    {
        Installed.Remove(packageName);
        return Task.CompletedTask;
    }

    public Task InstallFromStoreAsync(string storeId, CancellationToken ct = default)
    {
        StoreInstalls.Add(storeId);
        if (StorePackages.TryGetValue(storeId, out var package)) Installed.Add(package);
        return Task.CompletedTask;
    }
}

internal sealed class FakePower : IPowerControl
{
    public List<Guid> Schemes { get; } = [new("381b4222-f694-41f0-9685-ff5bb260df2e")];
    public Guid Active { get; set; } = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default) => Task.FromResult(Active);

    public Task<IReadOnlyList<Guid>> ListSchemesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(Schemes.ToList());

    public Task SetActiveSchemeAsync(Guid scheme, CancellationToken ct = default)
    {
        if (!Schemes.Contains(scheme))
            throw new InvalidOperationException("Схема не найдена");
        Active = scheme;
        return Task.CompletedTask;
    }

    public Task DuplicateSchemeAsync(Guid source, Guid destination, CancellationToken ct = default)
    {
        Schemes.Add(destination);
        return Task.CompletedTask;
    }
}

internal sealed class FakeOneDrive : IOneDriveControl
{
    public OneDriveScope Scope { get; set; }
    public OneDriveScope GetInstalledScope() => Scope;
    public Task UninstallAsync(CancellationToken ct = default) { Scope = OneDriveScope.None; return Task.CompletedTask; }
    public Task RestoreAsync(OneDriveScope scope, CancellationToken ct = default) { Scope = scope; return Task.CompletedTask; }
}

internal sealed class MemoryBackupStore : IBackupStore
{
    private readonly Dictionary<string, BackupRecord> _records = [];

    public BackupRecord? Get(string tweakId) => _records.GetValueOrDefault(tweakId);

    public IReadOnlyList<BackupRecord> GetAll() => _records.Values.ToList();

    public void Save(BackupRecord record) => _records[record.TweakId] = record;

    public void Remove(string tweakId) => _records.Remove(tweakId);
}

internal sealed class FakeComponents : IComponentPresence
{
    public HashSet<WindowsComponent> Present { get; } = [];
    public bool Fail { get; set; }
    public void Refresh() { }
    public Task<bool> IsPresentAsync(WindowsComponent component, CancellationToken ct) =>
        Fail ? throw new InvalidOperationException("Component probe failed") : Task.FromResult(Present.Contains(component));
}

/// <summary>Собранное тестовое окружение: фейковая система + движок.</summary>
internal sealed class TestSystem
{
    public FakeRegistry Registry { get; } = new();
    public FakeServices Services { get; } = new();
    public FakeTasks Tasks { get; } = new();
    public FakeApps Apps { get; } = new();
    public FakePower Power { get; } = new();
    public FakeOneDrive OneDrive { get; } = new();
    public FakeComponents Components { get; } = new();
    public MemoryBackupStore Backups { get; } = new();

    public PlatformServices Platform => new(Registry, Services, Tasks, Apps, Power, OneDrive, Components);

    public TweakEngine CreateEngine() => new(Platform, Backups, NullLogger<TweakEngine>.Instance);

    public static TweakDefinition Tweak(params TweakAction[] actions) => new()
    {
        Id = "test.tweak",
        Title = "Тестовый твик",
        Description = "Описание",
        Actions = actions,
    };
}
