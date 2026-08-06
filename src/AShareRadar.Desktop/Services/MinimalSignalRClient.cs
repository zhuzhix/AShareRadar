using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AShareRadar.Desktop.Services;

public sealed class MinimalSignalRClient : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    private readonly Uri _hubUri;
    private readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _runCancellation;

    public MinimalSignalRClient(string hubUrl)
    {
        _hubUri = new Uri(hubUrl);
    }

    public event EventHandler? MessageReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runCancellation is not null)
        {
            return;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _runCancellation.Token;

        _webSocket = new ClientWebSocket();
        var negotiateUri = BuildNegotiateUri();
        var negotiateJson = await _httpClient.PostAsync(negotiateUri, content: null, token);
        negotiateJson.EnsureSuccessStatusCode();

        using var negotiateStream = await negotiateJson.Content.ReadAsStreamAsync(token);
        using var negotiateDoc = await JsonDocument.ParseAsync(negotiateStream, cancellationToken: token);
        var connectionToken = negotiateDoc.RootElement.GetProperty("connectionToken").GetString()
            ?? negotiateDoc.RootElement.GetProperty("connectionId").GetString()
            ?? throw new InvalidOperationException("SignalR negotiate response did not contain a connection token.");

        var connectUri = BuildConnectUri(connectionToken);
        await _webSocket.ConnectAsync(connectUri, token);
        await SendHandshakeAsync(_webSocket, token);

        _ = Task.Run(() => ReceiveLoopAsync(_webSocket, token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _runCancellation?.Cancel();

        if (_webSocket is { State: WebSocketState.Open })
        {
            await _webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client stopped.",
                CancellationToken.None);
        }

        _webSocket?.Dispose();
        _webSocket = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    public async ValueTask DisposeAsync()
    {
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
        await webSocket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
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
                if (IsHubInvocation(frame))
                {
                    MessageReceived?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private static bool IsHubInvocation(string frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.GetInt32() == 1;
        }
        catch
        {
            return false;
        }
    }
}
