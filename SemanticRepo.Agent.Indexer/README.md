# Code indexing agent

`SemanticRepo.Agent.Indexer` reads source files, splits them into bounded chunks,
generates local embeddings with Ollama, and stores vectors and source payloads in
Qdrant. Other agents invoke `ICodeIndexAgent` over StreamJsonRpc TCP on port 5056.
No chat model or paid embedding API is required.

## Run

Install .NET 9, Docker, and Ollama. From the solution directory:

```sh
docker compose -f SemanticRepo.Agent.Indexer/compose.yaml up -d
ollama pull nomic-embed-text
dotnet run --project SemanticRepo.Agent.Indexer -- --Indexer:RepositoryPath="/absolute/path/to/repository"
```

Ollama must be running (`ollama serve` if the desktop service is not running).
The model is explicitly downloaded by the operator, never automatically by the
agent. Qdrant persists data in the `code-vectors` Docker volume.

The agent scans immediately and again every 60 seconds after the previous scan
completes. Omit `Indexer:RepositoryPath` to use on-demand indexing only. Paths are
on the server's filesystem; use Windows paths when running the worker on Windows.

The Aspire AppHost also starts a persistent Qdrant container and this worker. It
uses a local Ollama service. Set `Indexer:RepositoryPath` in AppHost configuration
to enable automatic indexing there. Use either Compose or Aspire for Qdrant.

## Call from another agent

Reference `SemanticRepo.Contracts` and `StreamJsonRpc`:

```csharp
using System.Net.Sockets;
using SemanticRepo.Contracts.Indexing;
using StreamJsonRpc;

using var connection = new TcpClient();
await connection.ConnectAsync("127.0.0.1", 5056);
using var stream = connection.GetStream();
using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter()));
var agent = rpc.Attach<ICodeIndexAgent>();
rpc.StartListening();

var repository = @"C:\repos\MyApplication";
var indexed = await agent.IndexRepositoryAsync(new IndexRepositoryRequest(repository));
var found = await agent.SearchCodeAsync(new SearchCodeRequest(repository, "Where are services registered for dependency injection?"));
foreach (var match in found.Matches)
    Console.WriteLine($"{match.FilePath}:{match.StartLine}-{match.EndLine} ({match.Score:F3})\n{match.Content}");
var status = await agent.GetStatusAsync();
```

RPC uses UTF-8 JSON with Content-Length framing. Method names include `Async`;
request objects are positional parameters. Cancellation is supported. The RPC
endpoint has no authentication and binds to loopback by default.

## Index behavior

- C# chunks prefer declaration boundaries using Roslyn; all supported languages
  have bounded text chunks, file paths, and inclusive one-based line ranges.
  C# symbol labels are syntax-derived, not compilation-resolved symbols.
- Unchanged files are skipped using content hashes stored in Qdrant. Changes
  replace old chunks only after all embeddings and the upsert succeed. Deleted
  or newly excluded files are removed after a successful complete scan.
- A scan with unreadable, oversized, or invalid-text files reports errors and
  skips stale-file pruning. Indexing is serialized within one worker; run only
  one writer per repository/collection. Search remains available while indexing.
- Each file is updated independently. An interrupted scan can leave mixed
  revisions; the next successful scan reconciles them. There is no repository-wide
  atomic snapshot. Partial/inconsistent chunk counts cause reindexing.
- Repository IDs derive from canonical absolute paths. Moving a repository uses
  a new ID; old repository data remains until explicitly removed in Qdrant.
- The actual collection has a suffix derived from model name, vector size,
  prefixes, chunk size, and schema version. Configuration changes create a separate
  collection, preventing incompatible embeddings from being searched together.
  Pin model versions for reproducibility; if a model's weights change under the
  same name, change `Qdrant:Collection` and reindex. Old collections are retained.
- Hidden directories/files, symlinks, build outputs, dependency directories, and
  non-allowlisted extensions are skipped. `.gitignore` patterns are not evaluated;
  configure `Indexer:ExcludedDirectories` and `Indexer:IncludedExtensions` for the
  intended source scope. JSON, environment files, and binary files are not included
  by default. UTF-8 and BOM-marked text are accepted.
- Empty files produce no vectors. The scanner is polling-based, so changes appear
  on the next scan. Search scores are cosine similarities, not confidence values.
- `GetStatusAsync` reports this worker's activity and last result; dependency
  failures surface during indexing/search. It is not a database health probe.

## Configuration

All settings are in `appsettings.json`; environment variables use double
underscores, e.g. `Indexer__RepositoryPath`, `Qdrant__Endpoint`, `Qdrant__ApiKey`,
and `Embeddings__Model`. Configure credentials outside source-controlled files.

The default `nomic-embed-text` model uses 768 dimensions and separate document and
query prefixes. For another Ollama embedding model, configure the dimensions and
both prefixes to match its requirements. Requests set `truncate=false` so oversized
inputs fail visibly; lower the chunk size if the model's token limit is exceeded.

`Qdrant:Collection` is a base name (letters, digits, underscores, and hyphens).
The Qdrant adapter uses its HTTP API, including the query API available since
Qdrant 1.10. The Compose setup pins Qdrant 1.14.1.
