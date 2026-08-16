using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tard.Skills;

namespace Tard.Tests.Skills;

public class ShellSkillTests
{
    private readonly ShellSkill _skill = new(NullLogger<ShellSkill>.Instance);

    [Fact]
    public void HasCorrectMetadata()
    {
        Assert.Equal("run_shell_command", _skill.Name);
        Assert.NotEmpty(_skill.Description);
    }

    [Fact]
    public async Task Execute_EchoCommand_ReturnsOutput()
    {
        var args = JsonDocument.Parse("""{"command": "echo hello"}""").RootElement;
        var context = new SkillContext("user1");
        var result = await _skill.ExecuteAsync(args, context);

        var json = JsonDocument.Parse(result);
        Assert.Equal(0, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("hello", json.RootElement.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Execute_InvalidCommand_ReturnsNonZeroExit()
    {
        // Use a command that will fail
        var args = JsonDocument.Parse("""{"command": "exit 1"}""").RootElement;
        var context = new SkillContext("user1");
        var result = await _skill.ExecuteAsync(args, context);

        var json = JsonDocument.Parse(result);
        // exit 1 should return exitCode 1
        Assert.NotEqual(0, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Execute_MissingCommand_Throws()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        var context = new SkillContext("user1");

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _skill.ExecuteAsync(args, context));
    }

    [Fact]
    public void ParameterSchema_HasCommandProperty()
    {
        var schema = _skill.ParameterSchema;
        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("command", out _));
    }

    private async Task<JsonElement> RunAsync(string command)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { command }));
        var result = await _skill.ExecuteAsync(args, new SkillContext("user1"));
        return JsonDocument.Parse(result).RootElement.Clone();
    }

    [Fact]
    public async Task Execute_CommandContainingBackslashes_IsNotMangled()
    {
        // The old implementation spliced the command into a quoted -c string and escaped only
        // double quotes, so a backslash was consumed before bash ever saw the command.
        var json = await RunAsync(@"printf '%s' 'a\b\c'");

        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains(@"a\b\c", json.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Execute_CommandContainingQuotes_IsNotMangled()
    {
        var json = await RunAsync("""echo "she said \"hi\"" """);

        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("she said", json.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Execute_LargeStderrOutput_DoesNotDeadlock()
    {
        // Reading stdout to EOF before stderr deadlocks once stderr exceeds one pipe buffer
        // (~64 KiB on Linux): the child blocks writing fd 2 while the parent blocks reading fd 1.
        var command = "for i in $(seq 1 4000); do echo 'stderr padding line for the pipe buffer' >&2; done; echo done";

        var completed = await Task.WhenAny(RunAsync(command), Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(completed is Task<JsonElement>, "shell command deadlocked on the stderr pipe");
        var json = await (Task<JsonElement>)completed;
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("stderr", json.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Execute_CapturesBothStdoutAndStderr()
    {
        var json = await RunAsync("echo out; echo err >&2");

        var output = json.GetProperty("output").GetString()!;
        Assert.Contains("out", output);
        Assert.Contains("err", output);
    }

    [Fact]
    public async Task Execute_HonoursCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var args = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { command = "sleep 30" }));

        var task = _skill.ExecuteAsync(args, new SkillContext("user1"), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
