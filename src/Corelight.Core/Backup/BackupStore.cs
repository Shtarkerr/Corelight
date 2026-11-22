using System.Text.Json;

namespace Corelight.Core.Backup;

/// <summary>Исходное состояние одного действия. Applicable=false — цели не было на момент применения.</summary>
public sealed record ActionBackup(bool Applicable, string? State);

public sealed class BackupRecord
{
    public required string TweakId { get; init; }

    /// <summary>Название на момент применения — нужно для истории, даже если твик потом убрали из каталога.</summary>
    public required string Title { get; init; }

    public required DateTimeOffset AppliedAt { get; init; }

    public required IReadOnlyList<ActionBackup> Actions { get; init; }

    /// <summary>Идентификаторы целей действий; отсутствуют у бэкапов ранних версий.</summary>
    public IReadOnlyList<string>? ActionTargets { get; init; }
}

public interface IBackupStore
{
    BackupRecord? Get(string tweakId);

    IReadOnlyList<BackupRecord> GetAll();

    void Save(BackupRecord record);

    void Remove(string tweakId);
}

/// <summary>Хранит бэкапы в JSON-файле. Запись идёт через временный файл, чтобы сбой не испортил данные.</summary>
public sealed class JsonBackupStore : IBackupStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BackupRecord> _records;

    public JsonBackupStore(string path)
    {
        _path = path;
        try
        {
            _records = Load(path);
        }
        catch (JsonException)
        {
            // Повреждённый файл не удаляем: откладываем в сторону, чтобы его можно было разобрать вручную.
            CorruptCopyPath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            File.Copy(path, CorruptCopyPath, overwrite: false);
            _records = [];
        }
    }

    /// <summary>Путь к копии повреждённого файла бэкапа, если при запуске его не удалось прочитать.</summary>
    public string? CorruptCopyPath { get; }

    public BackupRecord? Get(string tweakId)
    {
        lock (_gate)
            return _records.GetValueOrDefault(tweakId);
    }

    public IReadOnlyList<BackupRecord> GetAll()
    {
        lock (_gate)
            return _records.Values.OrderByDescending(r => r.AppliedAt).ToList();
    }

    public void Save(BackupRecord record)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, BackupRecord>(_records) { [record.TweakId] = record };
            Flush(next.Values);
            _records[record.TweakId] = record;
        }
    }

    public void Remove(string tweakId)
    {
        lock (_gate)
        {
            if (!_records.ContainsKey(tweakId)) return;
            Flush(_records.Values.Where(record => record.TweakId != tweakId));
            _records.Remove(tweakId);
        }
    }

    private void Flush(IEnumerable<BackupRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            {
                JsonSerializer.Serialize(stream, records, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temp);
            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, BackupRecord> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        var records = JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(path), Options)
            ?? throw new JsonException("Вместо списка бэкапов получен null.");
        var result = new Dictionary<string, BackupRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.TweakId) || string.IsNullOrWhiteSpace(record.Title)
                || record.Actions is null || record.Actions.Any(a => a is null)
                || (record.ActionTargets is not null && record.ActionTargets.Count != record.Actions.Count)
                || !result.TryAdd(record.TweakId, record))
                throw new JsonException("Некорректная или повторяющаяся запись бэкапа.");
        }
        return result;
    }
}
