using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace Corelight.Core.Platform;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    // Консольные утилиты Windows (sc, powercfg) пишут в OEM-кодировке — для русской системы это 866, а не UTF-8.
    private static readonly Encoding OemEncoding = CreateOemEncoding();
    internal static string WindowsPowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(fileName))
            throw new ArgumentException("Нужен полный путь к исполняемому файлу.", nameof(fileName));

        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, error);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} не завершился за {timeout.TotalSeconds:0} с.");
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static Encoding CreateOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            sink.Append(buffer, 0, read);
    }
}
