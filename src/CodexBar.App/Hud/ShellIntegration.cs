using System.IO;
using System.Text;
using CodexBar.Core;

namespace CodexBar.App.Hud;

/// <summary>
/// Terminal commands in the notch without typing anything: a small script dot-sourced from the
/// user's PowerShell profile tells TheAI (over the live activity pipe) when a command starts
/// and how it ended. The notch shows a command only once it has run 10 seconds, so quick ones
/// never flicker by. Installing adds one marked line to the profile (a backup is kept); removing
/// takes it out again. The prompt keeps <c>$?</c> and <c>$LASTEXITCODE</c> for prompt themes.
/// </summary>
internal static class ShellIntegration
{
    private const string Marker = "# TheAI: terminal commands in the notch";

    public static string ScriptPath => Path.Combine(
        CodexBar.Providers.AppPaths.DataRoot, "shell", "theai.ps1");

    /// <summary>Windows PowerShell 5.1 always; PowerShell 7 when it is installed.</summary>
    private static IEnumerable<string> Profiles()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
        var seven = Path.Combine(documents, "PowerShell");
        if (Directory.Exists(seven) || PwshInstalled())
        {
            yield return Path.Combine(seven, "Microsoft.PowerShell_profile.ps1");
        }
    }

    private static bool PwshInstalled() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Any(folder => folder.Length > 0 && File.Exists(Path.Combine(folder.Trim(), "pwsh.exe")));

    private static string ProfileLine =>
        $"if (Test-Path \"$env:LOCALAPPDATA\\TheAI\\shell\\theai.ps1\") {{ . \"$env:LOCALAPPDATA\\TheAI\\shell\\theai.ps1\" }}  {Marker}";

    public static bool IsInstalled()
    {
        try
        {
            return Profiles().Any(path => File.Exists(path) && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Install()
    {
        WriteScript();
        foreach (var profile in Profiles())
        {
            var text = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
            if (text.Contains(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            Backup(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            // Last in the profile, so it wraps whatever prompt the profile set up (oh-my-posh, starship…).
            var separator = text.Length == 0 || text.EndsWith('\n') ? string.Empty : Environment.NewLine;
            File.WriteAllText(profile, text + separator + ProfileLine + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    public static void Uninstall()
    {
        foreach (var profile in Profiles().Where(File.Exists))
        {
            var lines = File.ReadAllLines(profile);
            if (!lines.Any(line => line.Contains(Marker, StringComparison.Ordinal)))
            {
                continue;
            }

            Backup(profile);
            File.WriteAllLines(profile, lines.Where(line => !line.Contains(Marker, StringComparison.Ordinal)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    /// <summary>Keeps the script current with this version of TheAI (called at startup when installed).</summary>
    public static void Refresh()
    {
        try
        {
            if (IsInstalled())
            {
                WriteScript();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("Shell", exception.Message);
        }
    }

    private static void WriteScript()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath)!);
        // With a BOM: Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page.
        File.WriteAllText(ScriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void Backup(string profile)
    {
        var backup = profile + ".codexbar-backup";
        if (File.Exists(profile) && !File.Exists(backup))
        {
            File.Copy(profile, backup);
        }
    }

    internal const string Script = """
        # TheAI: terminal commands in the notch. Written by TheAI; turn it off in
        # TheAI's Settings (Notch) to take it out of your profile. Nothing leaves this PC:
        # the command line, its folder and how it ended go to TheAI's local pipe.
        if ($global:__CodexBarShell) { return }
        $global:__CodexBarShell = @{
            Pipe    = 'theai-' + $env:USERNAME.ToLowerInvariant()
            Id      = "sh-$PID"
            Last    = (Get-History -Count 1).Id
            Pending = $false
            Prompt  = $function:prompt
        }

        function global:__CodexBarSend([hashtable] $Message) {
            try {
                $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $global:__CodexBarShell.Pipe, [System.IO.Pipes.PipeDirection]::Out)
                $pipe.Connect(60)   # TheAI not running: give up at once
                $bytes = [System.Text.Encoding]::UTF8.GetBytes(($Message | ConvertTo-Json -Compress) + "`n")
                $pipe.Write($bytes, 0, $bytes.Length)
                $pipe.Dispose()
            } catch { }
        }

        # Enter: the command starts (shown only if it is still running 10 seconds later).
        if (Get-Module PSReadLine) {
            Set-PSReadLineKeyHandler -Key Enter -BriefDescription TheAIAcceptLine -Description 'Accept the line (TheAI shows long commands in the notch)' -ScriptBlock {
                $line = $null; $cursor = $null
                [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref] $line, [ref] $cursor)
                if ($line.Trim().Length -gt 0) {
                    $global:__CodexBarShell.Pending = $true
                    __CodexBarSend @{ id = $global:__CodexBarShell.Id; title = $line.Trim(); after = 10; folder = $PWD.ProviderPath }
                }
                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
        }

        # The prompt: the command ended. Quick ones are taken back; long ones end with a check or a cross.
        function global:prompt {
            $ok = $?
            $code = $global:LASTEXITCODE
            $shell = $global:__CodexBarShell
            $pending = $shell.Pending
            $shell.Pending = $false
            $last = Get-History -Count 1
            if ($last -and $last.Id -ne $shell.Last) {
                $shell.Last = $last.Id
                $seconds = ($last.EndExecutionTime - $last.StartExecutionTime).TotalSeconds
                if ($seconds -lt 10) {
                    __CodexBarSend @{ id = $shell.Id; remove = $true }
                } elseif ($ok) {
                    __CodexBarSend @{ id = $shell.Id; title = $last.CommandLine; done = $true; folder = $PWD.ProviderPath }
                } else {
                    $why = if ($code) { "exit $code" } else { 'error' }
                    __CodexBarSend @{ id = $shell.Id; title = $last.CommandLine; failed = $why; folder = $PWD.ProviderPath }
                }
            } elseif ($pending) {
                __CodexBarSend @{ id = $shell.Id; remove = $true }   # didn't run (a parse error)
            }

            # Hand the original prompt the same $? and $LASTEXITCODE it would have seen.
            $global:LASTEXITCODE = $code
            if (-not $ok) { Write-Error -Message '' -ErrorAction Ignore }
            & $shell.Prompt
        }
        """;
}
