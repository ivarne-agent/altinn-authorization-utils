using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Altinn.Authorization.ServiceDefaults.Tests;

/// <summary>
/// Accepts telemetry exports over HTTP on a free local port and records the path of each request.
/// Serves as both an OTLP/HTTP collector and an Azure Monitor ingestion endpoint.
/// </summary>
internal sealed class FakeTelemetryCollector
    : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentQueue<string> _paths = new();

    private FakeTelemetryCollector(HttpListener listener, string endpoint)
    {
        _listener = listener;
        Endpoint = endpoint;
    }

    /// <summary>
    /// Gets the base URL of the collector, without a trailing slash.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Gets the paths of the requests received so far.
    /// </summary>
    public IReadOnlyCollection<string> Paths
        => _paths.ToArray();

    public static FakeTelemetryCollector Start()
    {
        using var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();

        var endpoint = $"http://127.0.0.1:{port}";
        var listener = new HttpListener();
        listener.Prefixes.Add(endpoint + "/");
        listener.Start();

        var collector = new FakeTelemetryCollector(listener, endpoint);
        _ = collector.ReceiveAsync();
        return collector;
    }

    public void Dispose()
        => _listener.Close();

    private async Task ReceiveAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            await context.Request.InputStream.CopyToAsync(Stream.Null);
            _paths.Enqueue(context.Request.Url!.AbsolutePath);

            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}
