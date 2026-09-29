# Roslyn tool server

Run `dotnet run --project SemanticRepo.Agent.Roslyn` from the solution directory.
The worker exposes `IRoslynToolHub` over TCP at `127.0.0.1:5055` by default.
Configure `RoslynRpc:Address`, `RoslynRpc:Port`, and `RoslynRpc:MaxConnections` in
appsettings or with environment variables such as `RoslynRpc__Port=5056`.
The endpoint has no authentication; keep it on loopback or a trusted private network.

## Client

Use the shared contracts and StreamJsonRpc with UTF-8 JSON and Content-Length
headers (`HeaderDelimitedMessageHandler`). Method names retain their `Async`
suffix. Requests are ordinary positional arguments; cancellation tokens are
handled by StreamJsonRpc's cancellation protocol.

```csharp
using System.Net.Sockets;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Hub;
using SemanticRepo.Contracts.Roslyn.Syntax;
using StreamJsonRpc;

using var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 5055);
using var stream = client.GetStream();
using var handler = new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter());
using var rpc = new JsonRpc(handler);
var tools = rpc.Attach<IRoslynToolHub>();
rpc.StartListening();
var scope = new CodeScopeDto { ProjectPath = @"C:\repos\Example\Example.csproj" };
var skeleton = await tools.GetProjectSkeletonAsync(new GetProjectSkeletonRequest(scope));
```

## Scope and result conventions

- Paths refer to the server filesystem. Relative paths resolve against
  `WorkingDirectory`, or the worker's current directory when omitted.
- A solution loads through MSBuild; a project can narrow a solution scope.
  `FilePaths` further restricts returned documents when combined with either.
  Project references remain available for semantic resolution.
- File-only scopes use an ad-hoc C# project and runtime assembly references.
  Use project scopes for accurate package references, compiler options, and DI analysis.
- Project/solution loading requires an installed .NET SDK and restored project
  dependencies. Workspace load diagnostics are logged. Incomplete code can still
  produce partial semantic results.
- Up to four scope snapshots are cached. Changes to source files, project inputs,
  and restore assets invalidate a snapshot on the next request. Compilations and
  semantic models are cached within each snapshot. Active requests retain their
  snapshot across reloads and cache eviction.
- Source lines and columns are one-based; end positions are exclusive.
  Type names are qualified without `global::`; generic and nested metadata names
  are also accepted for type lookup. Ambiguous types require a narrower scope.
- Symbol lookup accepts qualified type/member names, method signatures such as
  `Example.Service.Run(int)`, and Roslyn declaration IDs such as
  `M:Example.Service.Run(System.Int32)`. A method name without a signature searches
  all its overloads. Results are limited by `MaxResults`.
- Method overload indices are zero-based, ordered by source path and declaration
  position. Body-less methods and missing declarations return null.
  `IncludeMethodBodies` cannot embed bodies in `TypeDeclarationDto` because the
  contract has no body field; use `GetMethodBodyAsync` for implementations.
- Hierarchies include transitive interfaces and derived types. Interface
  implementations include concrete and abstract classes within the scope.
- DI analysis recognizes semantic `Add*`, `TryAdd*`, and `AddKeyed*` calls for
  Singleton, Scoped, and Transient lifetimes, including generic, `typeof`, instance,
  and statically inferable factory registrations. Unresolved factory implementations
  are reported as `<unknown>`. `IncludeThirdParty` additionally permits extension
  methods with these names whose first parameter is `IServiceCollection`;
  arbitrary container-specific registration DSLs are not interpreted.
- Attribute lookup accepts a qualified name with or without the `Attribute`
  suffix. Inherited matches respect `AttributeUsage(Inherited = false)`.
- Invalid requests fault the RPC call. Missing collections are empty. Warmup
  returns false on loading/compilation creation failure, propagates cancellation,
  and does not require error-free source. Status reports readiness, workspace
  loading state, cached project count, and available RPC methods.
