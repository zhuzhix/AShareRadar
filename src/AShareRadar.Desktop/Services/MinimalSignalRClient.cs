using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AShareRadar.Contracts.MarketData;
using NLog;

namespace AShareRadar.Desktop.Services;

public sealed class MinimalSignalRClient : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Logger MappingLogger = LogManager.GetLogger("AShareRadar.Mapping.SignalRClient");
    private const char RecordSeparator = '\u001e';

    private readonly Uri _hubUri;
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingInvocations = [];
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _runCancellation;
    private Task? _receiveTask;
    private int _invocationId;

    public MinimalSignalRClient(string hubUrl)
    {
        _hubUri = new Uri(hubUrl);
    }

    public event EventHandler? MessageReceived;

    public async Task<MarketMappingSyncResult> UploadMarketMappingsAsync(
        MarketMappingSyncRequest request,
        CancellationToken cancellationToken)
    {
        using var traceScope = ScopeContext.PushProperty("TraceId", request.Version);
        if (_webSocket is not { State: WebSocketState.Open })
        {
            MappingLogger.Error("Mapping upload rejected because SignalR is not connected. State={State}", _webSocket?.State);
            throw new InvalidOperationException("SignalR 尚未连接。");
        }

        var invocationId = Interlocked.Increment(ref _invocationId).ToString();
        var stopwatch = Stopwatch.StartNew();
        MappingLogger.Info(
            "Mapping upload started. InvocationId={InvocationId} Version={Version} SectorRows={SectorRows} ConceptRows={ConceptRows}",
            invocationId,
            request.Version,
            request.SectorMappings.Count,
            request.ConceptMappings.Count);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingInvocations)
        {
            _pendingInvocations[invocationId] = completion;
        }

        try
        {
            var frame = JsonSerializer.Serialize(new
            {
                type = 1,
                invocationId,
                target = "UploadMarketMappings",
                arguments = new[] { request }
            }) + RecordSeparator;
            var frameBytes = Encoding.UTF8.GetBytes(frame);
            MappingLogger.Info("Sending mapping invocation. InvocationId={InvocationId} PayloadBytes={PayloadBytes}", invocationId, frameBytes.Length);
            await _webSocket.SendAsync(frameBytes, WebSocketMessageType.Text, true, cancellationToken);

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var json = await completion.Task;
            var result = JsonSerializer.Deserialize<MarketMappingSyncResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("服务端没有返回映射同步结果。");

            MappingLogger.Info(
                "Mapping upload completed. InvocationId={InvocationId} Success={Success} SectorRows={SectorRows} ConceptRows={ConceptRows} ElapsedMs={ElapsedMs}",
                invocationId,
                result.Success,
                result.SectorRows,
                result.ConceptRows,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            MappingLogger.Warn(ex, "Mapping upload canceled or timed out. InvocationId={InvocationId} ElapsedMs={ElapsedMs}", invocationId, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            MappingLogger.Error(ex, "Mapping upload failed. InvocationId={InvocationId} ElapsedMs={ElapsedMs}", invocationId, stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            lock (_pendingInvocations)
            {
                _pendingInvocations.Remove(invocationId);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runCancellation is not null)
        {
            Logger.Debug("SignalR start ignored because the client is already running. Hub={Hub}", _hubUri);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCancellation.Token;
        _webSocket = new ClientWebSocket();

        try
        {
            var negotiateUri = BuildNegotiateUri();
            Logger.Info("SignalR negotiate started. Hub={Hub} NegotiateUri={NegotiateUri}", _hubUri, negotiateUri);
            using var negotiateResponse = await _httpClient.PostAsync(negotiateUri, content: null, token);
            Logger.Info(
                "SignalR negotiate completed. StatusCode={StatusCode} ContentType={ContentType} ElapsedMs={ElapsedMs}",
                (int)negotiateResponse.StatusCode,
                negotiateResponse.Content.Headers.ContentType?.MediaType,
                stopwatch.ElapsedMilliseconds);
            negotiateResponse.EnsureSuccessStatusCode();

            using var negotiateStream = await negotiateResponse.Content.ReadAsStreamAsync(token);
            using var negotiateDoc = await JsonDocument.ParseAsync(negotiateStream, cancellationToken: token);
            var connectionToken = negotiateDoc.RootElement.GetProperty("connectionToken").GetString()
                ?? negotiateDoc.RootElement.GetProperty("connectionId").GetString()
                ?? throw new InvalidOperationException("SignalR negotiate response did not contain a connection token.");

            var connectUri = BuildConnectUri(connectionToken);
            Logger.Info("SignalR WebSocket connect started. Endpoint={Endpoint}", connectUri.GetLeftPart(UriPartial.Path));
            await _webSocket.ConnectAsync(connectUri, token);
            await SendHandshakeAsync(_webSocket, token);

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_webSocket, token), CancellationToken.None);
            Logger.Info("SignalR connected. Hub={Hub} ElapsedMs={ElapsedMs}", _hubUri, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SignalR connection failed. Hub={Hub} ElapsedMs={ElapsedMs}", _hubUri, stopwatch.ElapsedMilliseconds);
            await ResetConnectionAsync(waitForReceiver: false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        Logger.Info("SignalR stopping. Hub={Hub} State={State} PendingInvocations={PendingInvocations}", _hubUri, _webSocket?.State, PendingInvocationCount());
        _runCancellation?.Cancel();

        if (_webSocket is { State: WebSocketState.Open } webSocket)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopped.", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "SignalR WebSocket close failed. Hub={Hub}", _hubUri);
            }
        }

        await ResetConnectionAsync(waitForReceiver: true);
        Logger.Info("SignalR stopped. Hub={Hub}", _hubUri);
    }

    public async ValueTask DisposeAsync()
    {
        Logger.Debug("SignalR client disposing. Hub={Hub}", _hubUri);
        await StopAsync();
        _httpClient.Dispose();
    }

    private Uri BuildNegotiateUri()
    {
        var builder = new UriBuilder(_hubUri)
        {
            Scheme = _hubUri.Scheme == "https" ? "https" : "http",
            Path = _hubUri.AbsolutePath.TrimEnd('/') + "/negotiate",
            Query = "negotiateVersion=1"
        };
        return builder.Uri;
    }

    private Uri BuildConnectUri(string connectionToken)
    {
        var builder = new UriBuilder(_hubUri)
        {
            Scheme = _hubUri.Scheme == "https" ? "wss" : "ws",
            Query = "id=" + Uri.EscapeDataString(connectionToken)
        };
        return builder.Uri;
    }

    private static async Task SendHandshakeAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + RecordSeparator);
        await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        Logger.Debug("SignalR receive loop started. Hub={Hub}", _hubUri);

        try
        {
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Warn("SignalR server requested close. Status={Status} Description={Description}", result.CloseStatus, result.CloseStatusDescription);
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var raw = builder.ToString();
                builder.Clear();
                foreach (var frame in raw.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    ProcessFrame(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.Debug("SignalR receive loop canceled. Hub={Hub}", _hubUri);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SignalR receive loop failed. Hub={Hub} State={State}", _hubUri, webSocket.State);
            FailPendingInvocations(ex);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                FailPendingInvocations(new IOException("SignalR connection closed before the invocation completed."));
            }
            Logger.Info("SignalR receive loop ended. Hub={Hub} State={State}", _hubUri, webSocket.State);
        }
    }

    private void ProcessFrame(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            if (!document.RootElement.TryGetProperty("type", out var frameType))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any())
                {
                    Logger.Debug("SignalR handshake acknowledgement received.");
                    return;
                }
                Logger.Warn("SignalR frame without type received. Length={Length}", frame.Length);
                return;
            }

            if (frameType.GetInt32() == 3 && document.RootElement.TryGetProperty("invocationId", out var idElement))
            {
                var id = idElement.GetString();
                TaskCompletionSource<string>? pending = null;
                if (id is not null)
                {
                    lock (_pendingInvocations)
                    {
                        _pendingInvocations.Remove(id, out pending);
                    }
                }

                if (pending is null)
                {
                    Logger.Warn("SignalR completion has no pending invocation. InvocationId={InvocationId}", id);
                    return;
                }

                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    var errorMessage = error.GetString() ?? "Unknown SignalR invocation error.";
                    MappingLogger.Error("SignalR invocation returned an error. InvocationId={InvocationId} Error={Error}", id, errorMessage);
                    pending.TrySetException(new InvalidOperationException(errorMessage));
                }
                else if (document.RootElement.TryGetProperty("result", out var result))
                {
                    MappingLogger.Info("SignalR invocation result received. InvocationId={InvocationId} ResultBytes={ResultBytes}", id, Encoding.UTF8.GetByteCount(result.GetRawText()));
                    pending.TrySetResult(result.GetRawText());
                }
                else
                {
                    pending.TrySetException(new InvalidOperationException("SignalR completion did not contain a result."));
                }
                return;
            }

            if (frameType.GetInt32() == 1)
            {
                Logger.Debug("SignalR hub notification received. Length={Length}", frame.Length);
                MessageReceived?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SignalR frame processing failed. Length={Length}", frame.Length);
        }
    }

    private async Task ResetConnectionAsync(bool waitForReceiver)
    {
        _runCancellation?.Cancel();
        if (waitForReceiver && _receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "SignalR receive task ended with an error during shutdown.");
            }
        }

        FailPendingInvocations(new IOException("SignalR connection stopped."));
        _webSocket?.Dispose();
        _webSocket = null;
        _receiveTask = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    private int PendingInvocationCount()
    {
        lock (_pendingInvocations)
        {
            return _pendingInvocations.Count;
        }
    }

    private void FailPendingInvocations(Exception exception)
    {
        TaskCompletionSource<string>[] pending;
        lock (_pendingInvocations)
        {
            pending = _pendingInvocations.Values.ToArray();
            _pendingInvocations.Clear();
        }

        foreach (var invocation in pending)
        {
            invocation.TrySetException(exception);
        }
    }
}
