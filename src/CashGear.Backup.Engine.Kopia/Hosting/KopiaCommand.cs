using System.Diagnostics;
using System.Text;
using CashGear.Backup.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia.Hosting;

internal sealed record KopiaCommandResult(int ExitCode, string StandardOutput, IReadOnlyList<string> ErrorTail);

/// <summary>
/// One kopia CLI command against a repository config, for what the server's REST API cannot do (verification).
/// Same safeguards as the server: the pinned binary checked before it starts, a kill-on-close Job Object, every
/// inherited KOPIA_* variable removed, and the password only in the child's environment.
/// </summary>
internal static partial class KopiaCommand
{
    private const int MaxErrorLines = 50;

    public static async Task<KopiaCommandResult> RunAsync(string exePath, KopiaPin pin, string workingDirectory, IReadOnlyList<string> arguments,
        SecureSecret password, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }

        psi.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
        psi.Environment["KOPIA_PASSWORD"] = password.Reveal();

        using var job = new JobObject();
        Process process;
        try
        {
            await using (await pin.OpenVerifiedAsync(exePath, ct))
            {
                process = Process.Start(psi) ?? throw new BackupEngineException(BackupErrorKind.EngineUnavailable, "kopia did not start.");
            }
        }
        finally
        {
            psi.Environment.Remove("KOPIA_PASSWORD");
        }

        using (process)
        {
            try
            {
                job.Assign(process);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            LogStarted(logger, arguments.Count > 1 ? $"{arguments[0]} {arguments[1]}" : arguments[0], process.Id);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = ReadTailAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var result = new KopiaCommandResult(process.ExitCode, await output, await errors);
            LogFinished(logger, process.Id, result.ExitCode);
            return result;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadTailAsync(StreamReader reader)
    {
        var tail = new Queue<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > MaxErrorLines)
            {
                tail.Dequeue();
            }
        }

        return [.. tail];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kopia {Command} started as process {ProcessId}")]
    private static partial void LogStarted(ILogger logger, string command, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "kopia process {ProcessId} exited with code {ExitCode}")]
    private static partial void LogFinished(ILogger logger, int processId, int exitCode);
}
