using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticRepo.Agent.Roslyn.Hosting;
using SemanticRepo.Agent.Roslyn.Hub;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Agent.Roslyn.Services;
using SemanticRepo.Contracts.Roslyn.Common;
using SemanticRepo.Contracts.Roslyn.Hierarchy;
using SemanticRepo.Contracts.Roslyn.Hub;
using SemanticRepo.Contracts.Roslyn.Metadata;
using SemanticRepo.Contracts.Roslyn.Symbols;
using SemanticRepo.Contracts.Roslyn.Syntax;
using StreamJsonRpc;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
    Console.WriteLine("PASS: " + description);
    checks++;
}

var fixture = Path.GetFullPath(".roslyn-verification/Fixture");
var source = Path.Combine(fixture, "Sample.cs");
var files = new CodeScopeDto { FilePaths = [source] };
var project = new CodeScopeDto { ProjectPath = Path.Combine(fixture, "Fixture.csproj") };
using var manager = new RoslynWorkspaceManager(NullLogger<RoslynWorkspaceManager>.Instance);
var syntax = new SyntaxTools(manager);
var hierarchy = new HierarchyTools(manager);
var symbols = new SymbolTools(manager);
var metadata = new CompilerMetadataTools(manager);
var hub = new RoslynToolHub(manager, syntax, hierarchy, symbols, metadata, NullLogger<RoslynToolHub>.Instance);
var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
using var worker = new RoslynRpcServerWorker(hub, Options.Create(new RoslynRpcOptions { Port = port }),
    NullLogger<RoslynRpcServerWorker>.Instance);
await worker.StartAsync(CancellationToken.None);
try
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    using var stream = client.GetStream();
    using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new JsonMessageFormatter()));
    var remote = rpc.Attach<IRoslynToolHub>();
    rpc.StartListening();
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var token = deadline.Token;
    var status = await remote.GetEngineStatusAsync(token);
    Check(status.IsReady && status.AvailableTools.Count == 10, "RPC health and advertised methods");
    Check(await remote.WarmupScopeAsync(files, token), "file-only warmup");
    var skeleton = await remote.GetProjectSkeletonAsync(new(files), token);
    Check(skeleton.Types.Count(type => type.FullName == "Sample.Split") == 1, "partial declarations deduplicated");
    Check(skeleton.Types.Single(type => type.FullName == "Sample.Split").Methods.Count == 2, "partial members merged");
    Check(skeleton.Types.Single(type => type.FullName == "Sample.Base").Fields.All(field => field.Name != "secret"), "private members filtered");
    Check(skeleton.Types.Any(type => type.Kind == TypeKindDto.Record) && skeleton.Types.Any(type => type.Kind == TypeKindDto.Delegate), "record and delegate kinds");
    var declaration = await remote.GetTypeDeclarationAsync(new(files, "Sample.Base"), token);
    Check(declaration!.Fields.Any(field => field.Name == "secret") && declaration.DeclaredAt!.StartLine > 0, "type details and one-based spans");
    Check(await remote.GetTypeDeclarationAsync(new(files, "Sample.Outer`1+Inner"), token) is not null, "nested generic metadata name");
    Check((await remote.GetMethodBodyAsync(new(files, "Sample.Base", "Calculate"), token))!.BodyText.Contains("=>"), "expression method body");
    Check((await remote.GetMethodBodyAsync(new(files, "Sample.Base", "Calculate", 1), token))!.BodyText.Contains("value.Length"), "overload body selection");
    Check(await remote.GetMethodBodyAsync(new(files, "Sample.Base", "Run"), token) is null, "abstract body absent");
    Check(await remote.GetTypeDeclarationAsync(new(files, "Missing.Type"), token) is null, "missing type");
    var inheritance = await remote.GetTypeHierarchyAsync(new(files, "Sample.Base"), token);
    Check(inheritance!.DerivedTypes.Contains("Sample.Service") && inheritance.DerivedTypes.Contains("Sample.Grandchild"), "transitive derived classes");
    var interfaces = await remote.GetInterfaceImplementationsAsync(new(files, "Sample.IService"), token);
    Check(interfaces.Implementations.Contains("Sample.Base") && interfaces.Implementations.Contains("Sample.Grandchild"), "abstract and inherited interface implementations");
    var references = await remote.FindSymbolReferencesAsync(new(files, "Sample.Base.Counter"), token);
    Check(references.References.Any(reference => reference.ReferenceKind == "Read")
        && references.References.Any(reference => reference.ReferenceKind == "Write")
        && references.References.Any(reference => reference.ReferenceKind == "ReadWrite"), "read, write, read-write classification");
    Check((await remote.FindSymbolReferencesAsync(new(files, "M:Sample.Base.Calculate(System.Int32)"), token))
        .References.Any(reference => reference.ReferenceKind == "Invoke"), "documentation ID and invocation references");
    Check((await remote.FindSymbolReferencesAsync(new(files, "Sample.Base.Counter", 1), token)).References.Count == 1, "reference limit");
    Check((await remote.GetAttributedTypesAsync(new(files, "Sample.Marker"), token)).Count == 3, "inherited attribute matches");
    Check((await remote.GetAttributedTypesAsync(new(files, "Sample.Local"), token)).Count == 1, "non-inherited attribute usage");
    Check((await remote.GetAttributedTypesAsync(new(files, "Sample.Marker", false), token)).Count == 1, "inherit false");
    Check(await remote.WarmupScopeAsync(project, token), "MSBuild project warmup");
    var registrations = await remote.GetDIRegistrationsAsync(new(project), token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(registrations));
    Check(registrations.Registrations.Count == 6, "DI forms recognized without name-only false positive");
    Check(registrations.Registrations.All(registration => registration.ServiceType == "Sample.IService"
        && registration.ImplementationType == "Sample.Service"), "DI generic, typeof, instance and factory types");
    Check(await remote.WarmupScopeAsync(new CodeScopeDto { SolutionPath = Path.Combine(fixture, "Fixture.sln") }, token), "MSBuild solution warmup");
    var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => remote.GetProjectSkeletonAsync(new(files), token)));
    Check(concurrent.All(result => result.Types.Count == skeleton.Types.Count), "concurrent requests");
    using (var first = await manager.AcquireAsync(files, token))
    using (var second = await manager.AcquireAsync(files, token))
    {
        Check(ReferenceEquals(first.Solution, second.Solution), "workspace snapshot reused");
        Check(ReferenceEquals(await first.GetCompilationAsync(first.Documents[0].Project, token),
            await second.GetCompilationAsync(second.Documents[0].Project, token)), "compilation cache reused");
        Check(ReferenceEquals(await first.GetSemanticModelAsync(first.Documents[0], token),
            await second.GetSemanticModelAsync(second.Documents[0], token)), "semantic model cache reused");
    }
    var original = await File.ReadAllTextAsync(source, token);
    try
    {
        await File.WriteAllTextAsync(source, original + "\npublic class AddedAfterLoad;\n", token);
        Check((await remote.GetProjectSkeletonAsync(new(files), token)).Types.Any(type => type.Name == "AddedAfterLoad"), "source change invalidates cache");
    }
    finally { await File.WriteAllTextAsync(source, original); }
    Check(!await remote.WarmupScopeAsync(new CodeScopeDto(), token), "invalid warmup returns false");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await remote.GetProjectSkeletonAsync(new(files), cancelled.Token);
            Check(false, "cancellation propagated");
        }
        catch (OperationCanceledException) { Check(true, "cancellation propagated"); }
    }
    using var secondClient = new TcpClient();
    await secondClient.ConnectAsync(IPAddress.Loopback, port);
    using var secondRpc = new JsonRpc(new HeaderDelimitedMessageHandler(secondClient.GetStream()));
    var secondRemote = secondRpc.Attach<IRoslynToolHub>();
    secondRpc.StartListening();
    rpc.Dispose();
    Check((await secondRemote.GetEngineStatusAsync(token)).IsReady, "one disconnect preserves shared hub");
    await worker.StopAsync(token);
    await secondRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Check(true, "shutdown closes active connections");
}
finally
{
    await worker.StopAsync(CancellationToken.None);
}
Console.WriteLine($"Verified {checks} checks.");
