using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tard.Skills;

public class ShellSkill : ISkill
{
    private readonly ILogger<ShellSkill> _logger;
    private const int TimeoutMs = 30_000;
    private const int MaxOutputLength = 4000;

    public ShellSkill(ILogger<ShellSkill> logger)
    {
        _logger = logger;
    }

    public string Name => "run_shell_command";

    public string Description =>
        "Execute a shell command and return its output. " +
        "Use this for system tasks, checking files, running scripts, etc. " +
        "Commands time out after 30 seconds. Output is truncated to 4000 characters.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "The shell command to execute"
                }
            },
            "required": ["command"]
        }
        """).RootElement.Clone();

    public async Task<string> ExecuteAsync(JsonElement arguments, SkillContext context, CancellationToken cancellationToken = default)
    {
        var command = arguments.GetProperty("command").GetString()
            ?? throw new ArgumentException("command is required");

        _logger.LogInformation("Executing shell command for user {UserId}: {Command}", context.UserId, command);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeoutMs);

            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Pass the command as its own argv entry instead of splicing it into a quoted string.
            // The old `-c "{command.Replace("\"", "\\\"")}"` escaped double quotes but not
            // backslashes, so any command containing one was mangled before bash ever saw it.
            if (isWindows)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start process");

            try
            {
                // Read both pipes concurrently. Draining stdout to EOF before touching stderr
                // deadlocks as soon as a command writes more than one pipe buffer to stderr:
                // the child blocks writing fd 2 while we block reading fd 1.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(cts.Token);

                var output = string.IsNullOrEmpty(stderrTask.Result)
                    ? stdoutTask.Result
                    : $"{stdoutTask.Result}\n[stderr]: {stderrTask.Result}";

                if (output.Length > MaxOutputLength)
                    output = output[..MaxOutputLength] + "\n...[truncated]";

                return JsonSerializer.Serialize(new
                {
                    exitCode = process.ExitCode,
                    output
                });
            }
            catch (OperationCanceledException)
            {
                // The timeout only cancelled our reads — the child is still running. Disposing the
                // Process releases the handle without terminating it, so a `sleep 99999` or a
                // runaway loop survived every "timed out" reply and accumulated indefinitely.
                KillProcessTree(process);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller is shutting down — propagate rather than reporting a timeout that
            // did not happen.
            throw;
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                exitCode = -1,
                output = $"Command timed out after {TimeoutMs / 1000} seconds and was terminated."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                exitCode = -1,
                output = $"Error: {ex.Message}"
            });
        }
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate timed-out shell command");
        }
    }
}
