# Code summarizer

The Summarizer turns Roslyn analysis or supplied evidence into structured,
source-cited documentation. `ICodeSummarizerAgent` lives in
`SemanticRepo.Contracts.Summarization` and is exposed through StreamJsonRpc TCP
at `127.0.0.1:5057`.

## Run

Use the installed .NET 9 SDK and a local Ollama service. Pull the configured model
once, then start Roslyn and the Summarizer in separate terminals:

```sh
ollama pull qwen2.5-coder:7b
dotnet run --project SemanticRepo.Agent.Roslyn
dotnet run --project SemanticRepo.Agent.Summarizer
```

The Summarizer never downloads models automatically. Change `SummaryModel:Model`
to use another installed Ollama chat model supporting structured JSON output.
The default model is a configuration choice, not a requirement. The Indexer is
optional: start it and index the repository before supplying `RepositoryPath`.
Evidence-only summarization needs neither Roslyn nor the Indexer.

## RPC contract

All requests support cancellation. TCP uses UTF-8 JSON with Content-Length
framing; RPC method names retain `Async`. Request records are positional RPC
arguments, and enums serialize as their numeric values with the default client.
Bind to loopback or a trusted private network; the endpoint has no authentication.

```csharp
using System.Net.Sockets;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Summarization;
using StreamJsonRpc;

using var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 5057);
using var stream = client.GetStream();
using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter()));
var summarizer = rpc.Attach<ICodeSummarizerAgent>();
rpc.StartListening();

var summary = await summarizer.SummarizeCodeAsync(new SummarizeCodeRequest(
    new CodeScopeDto { ProjectPath = @"C:\repos\Example\Example.csproj" },
    TypeFullName: "Example.OrderService",
    MethodName: "PlaceOrder",
    Focus: "Explain validation, dependencies, and side effects."));
Console.WriteLine(summary.SummaryMarkdown);
```

`SummarizeCodeAsync` supports these targets:

- Scope only: project/solution/file declarations and DI registrations.
- `TypeFullName`: a type's declaration and hierarchy.
- `TypeFullName` plus `MethodName`: the selected method body, its containing type,
  and hierarchy. `OverloadIndex` is zero-based and follows Roslyn's source ordering.
- Optional `RepositoryPath`: retrieve related snippets from the Indexer using
  `Focus` as the search query, with at most `MaxSearchResults` (1–100). A failed
  optional search is reported in limitations; Roslyn failures fault the request.

Paths resolve on the relevant agent's filesystem. The Summarizer forwards
`CodeScopeDto` unchanged to Roslyn, including its `WorkingDirectory`.
The repository path belongs to the Indexer's filesystem and should be absolute.
Missing types, missing methods, and methods without an available body fault the
request rather than generating a speculative implementation summary.

Other agents can bypass retrieval with `SummarizeEvidenceAsync`:

```csharp
var summary = await summarizer.SummarizeEvidenceAsync(new SummarizeEvidenceRequest(
    "Example.OrderService.PlaceOrder",
    [new SummaryEvidenceDto("E1", "MethodBody", methodBody,
        new SummarySourceDto("Services/OrderService.cs", 20, 45, "Example.OrderService.PlaceOrder"))],
    Detail: SummaryDetail.Brief));
```

`SummaryDetail` accepts `Brief`, `Standard`, or `Detailed`. `Focus` is optional
and limited to 4,000 characters. Explicit evidence accepts 1–256 items, each with
a unique ID starting with a letter and followed by letters, digits, `_`, or `-`
(64 characters maximum). Content is limited to 262,144 characters per item and
1,048,576 characters in total. Source lines are inclusive and one-based.

Every successful `CodeSummaryDto` contains:

- Subject, generated title, Markdown, and structured sections/claims.
- An evidence list containing exactly the context supplied to the model. Each
  claim has one or more IDs resolving to that list. Markdown footnotes are
  rendered by the server from these validated IDs and original source metadata.
- Limitations, the configured model identifier, and a UTC completion timestamp.

Malformed model output, missing citations, or unknown evidence IDs fault the
request; the server does not silently accept or repair unsupported references.
Citation validation establishes traceability, not factual correctness. Review
generated claims against their evidence before publishing documentation.
Source comments and retrieved text are explicitly treated as data in the prompt.

`GetStatusAsync` reports local readiness, active and successfully completed
requests, last successful completion time, and last operational error. It does
not probe Ollama or upstream agents. Client cancellation does not count as a
failure or successful completion; dependency/request timeouts surface as errors.

## Configuration and limits

Configure the following appsettings sections (environment variables use `__`):

| Section | Defaults |
| --- | --- |
| `SummarizerRpc` | Address `127.0.0.1`, port `5057`, 32 connections |
| `Roslyn` | Host `127.0.0.1`, port `5055`, 120-second timeout |
| `Indexer` | Host `127.0.0.1`, port `5056`, 60-second timeout |
| `SummaryModel` | Ollama endpoint `http://localhost:11434`, model `qwen2.5-coder:7b`, 180-second timeout, 16,384 context tokens, 2,048 output tokens |
| `Summarizer` | 24,000 evidence characters, 64 evidence items, two concurrent requests, 300-second total request timeout |

The total request timeout includes queue time. Evidence is admitted in collection
order, prioritizing the requested method body. Context beyond the character/item
budget is omitted or truncated and disclosed in limitations. Character budgets
are not tokenizer-exact: configure the context window and evidence limits for the
chosen model. `IsTruncated` flags partial evidence. Source ranges still identify
the original declaration/snippet, not a recalculated range for clipped JSON/text.

Scope summaries do not inspect every implementation. Type summaries cover
declarations and hierarchy; choose a method target for implementation analysis.
Index snippets can be stale or cover only a portion of a repository. The service
does not write documentation files, persist summaries, or reindex repositories.
