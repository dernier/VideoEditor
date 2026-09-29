using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Core.Ipc;

/// <summary>
/// Canale locale (named pipe, solo utente corrente) tra la riga di comando e l'editor aperto:
/// permette a Claude di sapere cosa sta guardando l'utente (<c>vedit app status</c>) e di
/// comandare l'anteprima (<c>vedit app seek 12.5</c>). Protocollo: una riga JSON per richiesta e risposta.
/// </summary>
public static class AppPipe
{
    public const string PipeName = "vedit-app";

    public static async Task<JsonNode?> SendAsync(string command, JsonObject? args = null, int timeoutMs = 3000, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeoutMs, ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("L'editor non è aperto (nessuna risposta sul canale vedit-app).");
        }
        var request = new JsonObject { ["cmd"] = command, ["args"] = args ?? new JsonObject() };
        var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(request.ToJsonString());
        using var reader = new StreamReader(client, Encoding.UTF8);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(timeoutMs, 10_000));
        var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("Nessuna risposta dall'editor.");
        var response = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Risposta non valida dall'editor.");
        if (response["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? "Errore dell'editor.");
        return response["data"];
    }

    /// <summary>Server lato editor: gestisce una richiesta per connessione finché <paramref name="ct"/> non viene annullato.</summary>
    public static async Task ServeAsync(Func<string, JsonObject, Task<JsonNode?>> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException)
            {
                // Un'altra istanza dell'editor possiede già il canale: si riprova più tardi.
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => HandleAsync(server, handler, ct), ct);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, Func<string, JsonObject, Task<JsonNode?>> handler, CancellationToken ct)
    {
        await using var _ = server;
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;
            JsonObject response;
            try
            {
                var req = JsonNode.Parse(line)!.AsObject();
                var cmd = req["cmd"]?.GetValue<string>() ?? "";
                var args = req["args"] as JsonObject ?? new JsonObject();
                var data = await handler(cmd, args);
                response = new JsonObject { ["ok"] = true, ["data"] = data };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
            await writer.WriteLineAsync(response.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }
}
