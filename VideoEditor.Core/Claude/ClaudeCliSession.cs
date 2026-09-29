using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace VideoEditor.Core.Claude;

/// <summary>Evento prodotto da Claude Code in modalità <c>--output-format stream-json</c>.</summary>
/// <param name="Kind">text, tool, tool_result, result, error, system, log</param>
public sealed record ClaudeEvent(string Kind, string Text, JsonNode? Raw = null);

/// <summary>
/// Conversazione con Claude Code dalla riga di comando (<c>claude -p</c>), nella cartella del progetto.
/// La sessione continua tra un messaggio e l'altro con <c>--resume</c>. Claude modifica il progetto
/// con il comando <c>vedit</c>; l'editor si accorge delle modifiche e si aggiorna da solo.
/// </summary>
public sealed class ClaudeCliSession(string workingDirectory)
{
    public string WorkingDirectory { get; set; } = workingDirectory;
    public string? SessionId { get; set; }
    public string? ClaudePath { get; set; }
    public string? Model { get; set; }
    public string PermissionMode { get; set; } = "acceptEdits";

    public string[] AllowedTools { get; set; } =
        ["Bash(vedit:*)", "Bash(ffprobe:*)", "Read", "Edit", "Write", "Glob", "Grep"];

    /// <summary>Cartelle da mettere in testa al PATH (quella di vedit.exe).</summary>
    public List<string> ExtraPath { get; } = [AppContext.BaseDirectory];

    public bool IsRunning => _process is { HasExited: false };

    private Process? _process;

    public void Cancel()
    {
        try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public async IAsyncEnumerable<ClaudeEvent> SendAsync(string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var exe = ClaudePath is { Length: > 0 } p && File.Exists(p) ? p : Locate();
        if (exe is null)
        {
            yield return new ClaudeEvent("error",
                "Claude Code non trovato. Installa con 'irm https://claude.ai/install.ps1 | iex' (o 'npm i -g @anthropic-ai/claude-code') " +
                "oppure indica il percorso di claude.exe nelle impostazioni.");
            yield break;
        }

        List<string> args = ["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", PermissionMode];
        if (AllowedTools.Length > 0) args.AddRange(["--allowedTools", string.Join(",", AllowedTools)]);
        if (!string.IsNullOrWhiteSpace(Model)) args.AddRange(["--model", Model]);
        if (!string.IsNullOrWhiteSpace(SessionId)) args.AddRange(["--resume", SessionId]);

        var psi = BuildStartInfo(exe, args);
        psi.WorkingDirectory = WorkingDirectory;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, ExtraPath.Where(Directory.Exists).Append(path));
        psi.Environment["VEDIT_PROJECT"] = WorkingDirectory;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare Claude Code.");
        var process = _process;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        // Il messaggio passa da stdin: niente problemi di escape sulla riga di comando.
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var reg = ct.Register(Cancel);
        var sawResult = false;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (System.Text.Json.JsonException) { node = null; }
            if (node is null)
            {
                yield return new ClaudeEvent("log", line);
                continue;
            }
            foreach (var ev in Interpret(node))
            {
                if (ev.Kind == "result") sawResult = true;
                yield return ev;
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        if (!sawResult)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            yield return new ClaudeEvent("error", ct.IsCancellationRequested
                ? "Interrotto."
                : $"Claude Code è terminato (codice {process.ExitCode}). {err}");
        }
    }

    private IEnumerable<ClaudeEvent> Interpret(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>();
        if (node["session_id"]?.GetValue<string>() is { Length: > 0 } sid) SessionId = sid;
        switch (type)
        {
            case "system":
                if (node["subtype"]?.GetValue<string>() == "init")
                    yield return new ClaudeEvent("system", $"Sessione {SessionId} · modello {node["model"]?.GetValue<string>()}", node);
                break;
            case "assistant":
                foreach (var block in node["message"]?["content"]?.AsArray() ?? [])
                {
                    switch (block?["type"]?.GetValue<string>())
                    {
                        case "text":
                            yield return new ClaudeEvent("text", block["text"]?.GetValue<string>() ?? "", block);
                            break;
                        case "tool_use":
                            yield return new ClaudeEvent("tool", DescribeTool(block), block);
                            break;
                    }
                }
                break;
            case "user":
                foreach (var block in node["message"]?["content"]?.AsArray() ?? [])
                {
                    if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                    var content = block["content"];
                    var text = content is JsonArray arr
                        ? string.Join("\n", arr.Select(c => c?["text"]?.GetValue<string>()).Where(t => t is not null))
                        : content?.ToString() ?? "";
                    var isError = block["is_error"]?.GetValue<bool>() == true;
                    yield return new ClaudeEvent(isError ? "tool_error" : "tool_result", Truncate(text, 2000), block);
                }
                break;
            case "result":
                var cost = node["total_cost_usd"]?.GetValue<double>();
                var summary = node["is_error"]?.GetValue<bool>() == true
                    ? "Errore: " + (node["result"]?.ToString() ?? node["subtype"]?.GetValue<string>())
                    : cost is double c ? $"Fatto · costo ${c:0.000}" : "Fatto";
                yield return new ClaudeEvent("result", summary, node);
                break;
        }
    }

    private static string DescribeTool(JsonNode block)
    {
        var name = block["name"]?.GetValue<string>() ?? "?";
        var input = block["input"];
        var detail = name switch
        {
            "Bash" => input?["command"]?.GetValue<string>(),
            "Read" or "Edit" or "Write" => input?["file_path"]?.GetValue<string>(),
            "Glob" or "Grep" => input?["pattern"]?.GetValue<string>(),
            _ => input?.ToJsonString(),
        };
        return $"{name}: {Truncate(detail ?? "", 400)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static ProcessStartInfo BuildStartInfo(string exe, List<string> args)
    {
        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // Installazione npm: meglio avviare direttamente node con cli.js che passare da cmd.exe.
            var cli = Path.Combine(Path.GetDirectoryName(exe)!, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            var node = Media.Ffmpeg.FindOnPath("node.exe");
            if (File.Exists(cli) && node is not null)
            {
                psi = new ProcessStartInfo(node);
                psi.ArgumentList.Add(cli);
            }
            else
            {
                psi = new ProcessStartInfo("cmd.exe");
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(exe);
            }
        }
        else
        {
            psi = new ProcessStartInfo(exe);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Cerca l'eseguibile di Claude Code (installazione nativa o npm).</summary>
    public static string? Locate()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        foreach (var n in names)
            if (Media.Ffmpeg.FindOnPath(n) is { } found) return found;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude"),
            Path.Combine(home, ".claude", "local", OperatingSystem.IsWindows() ? "claude.exe" : "claude"),
            Path.Combine(appData, "npm", "claude.cmd"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
