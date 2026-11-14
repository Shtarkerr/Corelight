using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public sealed class RollbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_automatic_rollback_keeps_backup_for_retry(bool optional, bool cancelled)
    {
        var sys = new TestSystem();
        var first = new MutableAction { Optional = optional, RevertFails = true };
        var second = new MutableAction
        {
            ApplyFailure = cancelled ? new OperationCanceledException() : new InvalidOperationException("apply failed"),
        };
        var tweak = TestSystem.Tweak(first, second);
        var engine = sys.CreateEngine();

        if (cancelled)
            await Assert.ThrowsAsync<OperationCanceledException>(() => engine.ApplyAsync(tweak));
        else
        {
            var error = await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));
            Assert.Contains("бэкап сохранён", error.Message);
        }

        Assert.NotNull(sys.Backups.Get(tweak.Id));
        Assert.Equal(1, first.Value);
        first.RevertFails = false;
        await engine.RevertAsync(tweak);
        Assert.Equal(5, first.Value);
        Assert.Equal(5, second.Value);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_manual_rollback_keeps_backup_even_for_optional_action(bool optional)
    {
        var sys = new TestSystem();
        var action = new MutableAction { Optional = optional };
        var tweak = TestSystem.Tweak(action);
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);
        var originalBackup = sys.Backups.Get(tweak.Id);
        action.RevertFails = true;

        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(tweak));

        Assert.Same(originalBackup, sys.Backups.Get(tweak.Id));
        action.RevertFails = false;
        await engine.RevertAsync(tweak);
        Assert.Equal(5, action.Value);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Action_that_changes_state_then_throws_is_rolled_back()
    {
        var sys = new TestSystem();
        var action = new MutableAction { ApplyFailure = new InvalidOperationException("partial failure") };
        var tweak = TestSystem.Tweak(action);

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().ApplyAsync(tweak));

        Assert.Equal(5, action.Value);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Optional_partial_failure_is_rolled_back_when_later_required_action_fails()
    {
        var sys = new TestSystem();
        var optional = new MutableAction { Optional = true, ApplyFailure = new InvalidOperationException("optional failure") };
        var required = new MutableAction { ApplyFailure = new InvalidOperationException("required failure") };
        var tweak = TestSystem.Tweak(optional, required);

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().ApplyAsync(tweak));

        Assert.Equal(5, optional.Value);
        Assert.Equal(5, required.Value);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_state_comparison_does_not_prevent_restore(bool automatic)
    {
        var sys = new TestSystem();
        var action = new MutableAction
        {
            CaptureFailsAfterChange = true,
            ApplyFailure = automatic ? new InvalidOperationException("partial failure") : null,
        };
        var tweak = TestSystem.Tweak(action);
        var engine = sys.CreateEngine();

        if (automatic)
            await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));
        else
        {
            await engine.ApplyAsync(tweak);
            await engine.RevertAsync(tweak);
        }

        Assert.Equal(5, action.Value);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    private sealed class MutableAction : TweakAction
    {
        public int Value { get; private set; } = 5;
        public Exception? ApplyFailure { get; init; }
        public bool RevertFails { get; set; }
        public bool CaptureFailsAfterChange { get; init; }

        public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
            Task.FromResult(Value == 1 ? ActionState.Applied : ActionState.NotApplied);

        public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
        {
            if (CaptureFailsAfterChange && Value == 1)
                throw new InvalidOperationException("read failed");
            return Task.FromResult<string?>(Value.ToString());
        }

        public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
        {
            Value = 1;
            if (ApplyFailure is not null)
                throw ApplyFailure;
            return Task.CompletedTask;
        }

        public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
        {
            if (RevertFails)
                throw new InvalidOperationException("restore failed");
            Value = int.Parse(captured!);
            return Task.CompletedTask;
        }

        public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct) =>
            throw new InvalidOperationException("The original backup must be used.");

        public override string Describe() => "тестовое изменение";
    }
}
