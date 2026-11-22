using System.Text.RegularExpressions;

namespace Corelight.Core.Platform;

public interface IPowerControl
{
    Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> ListSchemesAsync(CancellationToken ct = default);

    Task SetActiveSchemeAsync(Guid scheme, CancellationToken ct = default);

    /// <summary>Создаёт копию штатной схемы Windows с заданным GUID.</summary>
    Task DuplicateSchemeAsync(Guid source, Guid destination, CancellationToken ct = default);
}

/// <summary>Работа со схемами питания через powercfg. Из вывода берутся только GUID, поэтому язык системы не важен.</summary>
public sealed partial class WindowsPowerControl(IProcessRunner runner) : IPowerControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<Guid> GetActiveSchemeAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "/getactivescheme");
        var match = GuidPattern().Match(result.Output);
        return match.Success ? Guid.Parse(match.Value) : throw new InvalidOperationException("Не удалось определить активную схему питания.");
    }

    public async Task<IReadOnlyList<Guid>> ListSchemesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, "/list");
        return GuidPattern().Matches(result.Output).Select(m => Guid.Parse(m.Value)).Distinct().ToList();
    }

    public Task SetActiveSchemeAsync(Guid scheme, CancellationToken ct = default) =>
        RunAsync(ct, "/setactive", scheme.ToString());

    public Task DuplicateSchemeAsync(Guid source, Guid destination, CancellationToken ct = default) =>
        RunAsync(ct, "/duplicatescheme", source.ToString(), destination.ToString());

    private async Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args)
    {
        var result = await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args, Timeout, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException($"powercfg {string.Join(' ', args)} завершился с кодом {result.ExitCode}. {result.Output.Trim()}");
        return result;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
