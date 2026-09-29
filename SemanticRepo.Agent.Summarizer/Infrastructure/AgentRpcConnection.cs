using System.Net.Sockets;
using StreamJsonRpc;

namespace SemanticRepo.Agent.Summarizer.Infrastructure;

public sealed class AgentRpcConnection<T> : IDisposable where T : class
{
    private readonly TcpClient client;
    private readonly JsonRpc rpc;

    private AgentRpcConnection(TcpClient client)
    {
        this.client = client;
        var stream = client.GetStream();
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter()));
        Proxy = rpc.Attach<T>();
        rpc.StartListening();
    }

    public T Proxy { get; }

    public static async Task<AgentRpcConnection<T>> ConnectAsync(AgentEndpointOptions options, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(options.Host, options.Port, ct);
            return new AgentRpcConnection<T>(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        rpc.Dispose();
        client.Dispose();
    }
}
