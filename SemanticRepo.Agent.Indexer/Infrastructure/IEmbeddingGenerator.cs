namespace SemanticRepo.Agent.Indexer.Infrastructure;

public interface IEmbeddingGenerator
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, bool isQuery, CancellationToken ct = default);
}
