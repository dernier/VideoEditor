using System.Text.Json.Nodes;
using VideoEditor.Core.Claude;
using VideoEditor.Core.Ipc;

namespace VideoEditor.Core.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task AppPipe_RoundTrip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = AppPipe.ServeAsync((cmd, args) => Task.FromResult<JsonNode?>(cmd switch
        {
            "status" => new JsonObject { ["time"] = 12.5 },
            "seek" => new JsonObject { ["to"] = args["time"]!.GetValue<double>() },
            _ => throw new InvalidOperationException("sconosciuto"),
        }), cts.Token);

        var status = await AppPipe.SendAsync("status", ct: cts.Token);
        Assert.Equal(12.5, status!["time"]!.GetValue<double>());
        var seek = await AppPipe.SendAsync("seek", new JsonObject { ["time"] = 3.0 }, ct: cts.Token);
        Assert.Equal(3.0, seek!["to"]!.GetValue<double>());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AppPipe.SendAsync("boh", ct: cts.Token));
        Assert.Equal("sconosciuto", ex.Message);

        cts.Cancel();
        await server;
    }

    [Fact]
    public async Task ClaudeSession_ParsesStreamJson()
    {
        if (OperatingSystem.IsWindows()) return; // lo script finto è una shell POSIX
        var dir = Path.Combine(Path.GetTempPath(), "vedit-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Finto "claude": salva argomenti e stdin, poi emette eventi stream-json.
            var fake = Path.Combine(dir, "claude");
            File.WriteAllText(fake, """
                #!/bin/sh
                echo "$@" > args.txt
                cat > prompt.txt
                echo '{"type":"system","subtype":"init","session_id":"s-123","model":"test"}'
                echo '{"type":"assistant","message":{"content":[{"type":"text","text":"Taglio le pause."},{"type":"tool_use","name":"Bash","input":{"command":"vedit cut 1 2"}}]}}'
                echo '{"type":"user","message":{"content":[{"type":"tool_result","content":"OK: tagliati","is_error":false}]}}'
                echo '{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"s-123","total_cost_usd":0.01}'
                """.Replace("\r", ""));
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var session = new ClaudeCliSession(dir) { ClaudePath = fake };
            var events = new List<ClaudeEvent>();
            await foreach (var ev in session.SendAsync("togli le pause")) events.Add(ev);

            Assert.Equal(["system", "text", "tool", "tool_result", "result"], events.Select(e => e.Kind).ToArray());
            Assert.Equal("Bash: vedit cut 1 2", events[2].Text);
            Assert.Equal("s-123", session.SessionId);
            Assert.Equal("togli le pause", File.ReadAllText(Path.Combine(dir, "prompt.txt")));
            var args = File.ReadAllText(Path.Combine(dir, "args.txt"));
            Assert.Contains("--output-format stream-json", args);
            Assert.Contains("Bash(vedit:*)", args);

            // Il messaggio successivo riprende la stessa sessione.
            await foreach (var _ in session.SendAsync("altro")) { }
            Assert.Contains("--resume s-123", File.ReadAllText(Path.Combine(dir, "args.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
