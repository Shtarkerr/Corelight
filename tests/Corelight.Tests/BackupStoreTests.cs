using Corelight.Core.Backup;

namespace Corelight.Tests;

public sealed class BackupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Corelight.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "backup.json");

    [Fact]
    public void Records_survive_restart()
    {
        var store = new JsonBackupStore(FilePath);
        store.Save(new BackupRecord
        {
            TweakId = "a",
            Title = "Твик",
            AppliedAt = DateTimeOffset.Now,
            Actions = [new ActionBackup(true, null), new ActionBackup(false, null), new ActionBackup(true, "{\"Kind\":4,\"Data\":\"1\"}")],
        });

        var reloaded = new JsonBackupStore(FilePath).Get("a");

        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded.Actions.Count);
        Assert.Null(reloaded.Actions[0].State);
        Assert.False(reloaded.Actions[1].Applicable);
        Assert.Equal("{\"Kind\":4,\"Data\":\"1\"}", reloaded.Actions[2].State);
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_store_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ это не json");

        var store = new JsonBackupStore(FilePath);

        Assert.Empty(store.GetAll());
        Assert.NotNull(store.CorruptCopyPath);
        Assert.Equal("{ это не json", File.ReadAllText(store.CorruptCopyPath));
    }

    [Fact]
    public void Failed_replace_preserves_history_and_removes_temporary_file()
    {
        var store = new JsonBackupStore(FilePath);
        store.Save(new BackupRecord { TweakId = "a", Title = "Исходное", AppliedAt = DateTimeOffset.Now, Actions = [] });

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.Remove("a"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.NotNull(store.Get("a"));
        Assert.NotNull(new JsonBackupStore(FilePath).Get("a"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
