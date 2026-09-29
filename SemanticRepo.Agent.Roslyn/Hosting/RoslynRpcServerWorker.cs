using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SemanticRepo.Contracts.Roslyn.Hub;
using StreamJsonRpc;

namespace SemanticRepo.Agent.Roslyn.Hosting;

public sealed class RoslynRpcOptions
{
    public const string SectionName = "RoslynRpc";
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5055;
    public int MaxConnections { get; set; } = 32;
}

public sealed class RoslynRpcServerWorker(
    IRoslynToolHub hub,
    IOptions<RoslynRpcOptions> options,
    ILogger<RoslynRpcServerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var listener = new TcpListener(IPAddress.Parse(settings.Address), settings.Port);
        using var slots = new SemaphoreSlim(settings.MaxConnections, settings.MaxConnections);
        using var connectionsCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var connections = new HashSet<Task>();
        listener.Start();
        logger.LogInformation("Roslyn RPC listening on {Endpoint}", listener.LocalEndpoint);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    slots.Release();
                    throw;
                }
                connections.RemoveWhere(connection => connection.IsCompleted);
                connections.Add(HandleClientAsync(client, slots, connectionsCancellation.Token));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await connectionsCancellation.CancelAsync();
            await Task.WhenAll(connections);
        }
    }

    private async Task HandleClientAsync(TcpClient client, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var handler = new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter());
                using var rpc = new JsonRpc(handler)
                {
                    CancelLocallyInvokedMethodsWhenConnectionIsClosed = true
                };
                rpc.AddLocalRpcTarget<IRoslynToolHub>(hub, new JsonRpcTargetOptions
                {
                    DisposeOnDisconnect = false,
                    NotifyClientOfEvents = false
                });
                using var shutdown = stoppingToken.Register(rpc.Dispose);
                stoppingToken.ThrowIfCancellationRequested();
                rpc.StartListening();
                try
                {
                    await rpc.Completion;
                }
                finally
                {
                    rpc.Dispose();
                    await rpc.DispatchCompletion;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!stoppingToken.IsCancellationRequested)
                logger.LogWarning(exception, "Roslyn RPC client disconnected with an error.");
        }
        finally
        {
            slots.Release();
        }
    }
}
