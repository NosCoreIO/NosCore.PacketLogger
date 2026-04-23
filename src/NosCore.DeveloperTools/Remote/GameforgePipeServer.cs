using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NosCore.DeveloperTools.Remote;

/// <summary>
/// Stands up a named pipe server at <c>\\.\pipe\GameforgeClientJSONRPC</c>
/// and speaks the JSON-RPC 2.0 handshake the NosTale client expects when
/// launched by a Gameforge-style launcher. The client connects right
/// after start-up (triggered by the <c>_TNT_SESSION_ID</c> env var) and
/// calls four methods:
/// <list type="bullet">
/// <item><c>ClientLibrary.isClientRunning</c></item>
/// <item><c>ClientLibrary.initSession</c> (echoes back the caller's sessionId)</item>
/// <item><c>ClientLibrary.queryAuthorizationCode</c> (→ the auth-code GUID)</item>
/// <item><c>ClientLibrary.queryGameAccountName</c></item>
/// </list>
/// The <see cref="LineReceived"/> event surfaces every byte crossing the
/// pipe for the UI to log, and <see cref="SendLine"/> lets the UI push
/// arbitrary content onto the pipe (debugging / probing).
/// </summary>
public sealed class GameforgePipeServer : IAsyncDisposable
{
    public const string PipeName = "GameforgeClientJSONRPC";

    private readonly string _sessionId;
    private readonly string _authCode;
    private readonly string _accountName;
    private readonly string _accountId;
    private readonly string _region;   // "EN" etc — matches NosCore's RegionType code
    private readonly string _locale;   // "en_US" etc
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;
    private NamedPipeServerStream? _pipe;
    private Task? _loop;

    public GameforgePipeServer(
        string sessionId, string authCode, string accountName, string accountId,
        string region, string locale, Action<string> log)
    {
        _sessionId = sessionId;
        _authCode = authCode;
        _accountName = accountName;
        _accountId = accountId;
        _region = region;
        _locale = locale;
        _log = log;
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public void Start()
    {
        _pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool SendLine(string line)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            _log($"OUT raw: {line}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"OUT failed: {ex.Message}");
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _log($"pipe: waiting for client on \\\\.\\pipe\\{PipeName}");
            await _pipe!.WaitForConnectionAsync(ct);
            _log("pipe: client connected");

            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                var read = await _pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;
                var message = Encoding.UTF8.GetString(buffer, 0, read);
                _log($"IN : {message}");

                var response = TryBuildResponse(message);
                if (response is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await _pipe.WriteAsync(bytes.AsMemory(), ct);
                    await _pipe.FlushAsync(ct);
                    _log($"OUT: {response}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _log($"pipe error: {ex.Message}");
        }
    }

    private string? TryBuildResponse(string raw)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
        if (request is null) return null;

        var method = request["method"]?.GetValue<string>();
        var id = request["id"];
        var jsonrpc = request["jsonrpc"]?.GetValue<string>() ?? "2.0";

        // The Gameforge DLL (v3.9.x) asks for ten methods; older launchers only
        // answered four and silently broke on the rest, which is what triggered
        // "gf init failed" for us. Full list pulled from the DLL's string table.
        var result = method switch
        {
            "ClientLibrary.isClientRunning" => "true",
            "ClientLibrary.initSession" => request["params"]?["sessionId"]?.GetValue<string>() ?? _sessionId,
            "ClientLibrary.queryAuthorizationCode" => _authCode,
            "ClientLibrary.queryGameAccountName" => _accountName,
            "ClientLibrary.queryGameAccountId" => _accountId,
            "ClientLibrary.queryGameBranch" => "live",
            "ClientLibrary.queryGameRegion" => _region,
            "ClientLibrary.queryGameLocale" => _locale,
            "ClientLibrary.queryGameDisplayLocale" => _locale,
            "ClientLibrary.queryClientLocale" => _locale,
            _ => null,
        };
        if (result is null)
        {
            _log($"unknown method (not answered): {method}");
            return null;
        }

        var reply = new JsonObject
        {
            ["id"] = id?.DeepClone(),
            ["jsonrpc"] = jsonrpc,
            ["result"] = result,
        };
        return reply.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_loop is not null) await _loop;
        }
        catch
        {
            // best-effort shutdown
        }
        _pipe?.Dispose();
        _cts.Dispose();
    }
}
