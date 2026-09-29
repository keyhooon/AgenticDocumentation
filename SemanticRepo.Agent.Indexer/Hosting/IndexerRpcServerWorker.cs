using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Indexer.Infrastructure;
using SemanticRepo.Contracts.Indexing;
using StreamJsonRpc;

namespace SemanticRepo.Agent.Indexer.Hosting;

public sealed class IndexerRpcServerWorker(
    ICodeIndexAgent agent,
    IOptions<IndexerRpcOptions> options,
    ILogger<IndexerRpcServerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var listener = new TcpListener(IPAddress.Parse(settings.Address), settings.Port);
        using var slots = new SemaphoreSlim(settings.MaxConnections);
        using var connectionsCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var connections = new HashSet<Task>();
        listener.Start();
        logger.LogInformation("Code index RPC listening on {Endpoint}", listener.LocalEndpoint);
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
                connections.Add(HandleAsync(client, slots, connectionsCancellation.Token));
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

    private async Task HandleAsync(TcpClient client, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter()))
                {
                    CancelLocallyInvokedMethodsWhenConnectionIsClosed = true
                };
                rpc.AddLocalRpcTarget<ICodeIndexAgent>(agent, new JsonRpcTargetOptions
                {
                    DisposeOnDisconnect = false,
                    NotifyClientOfEvents = false
                });
                using var shutdown = ct.Register(rpc.Dispose);
                ct.ThrowIfCancellationRequested();
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
        catch (Exception exception)
        {
            if (!ct.IsCancellationRequested)
                logger.LogWarning(exception, "Code index RPC client disconnected with an error.");
        }
        finally
        {
            slots.Release();
        }
    }
}
