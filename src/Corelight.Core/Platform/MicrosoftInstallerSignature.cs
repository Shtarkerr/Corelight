namespace Corelight.Core.Platform;

internal static class MicrosoftInstallerSignature
{
    public static async Task<bool> IsValidAsync(IProcessRunner runner, string path, CancellationToken ct)
    {
        var script = "$s = Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; "
            + "if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '(^|,\\s*)O=Microsoft Corporation(,|$)') { exit 1 }";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await runner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromMinutes(1), ct);
        return result.Succeeded;
    }
}
