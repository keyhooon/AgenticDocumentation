using System.Net;
using SemanticRepo.Agent.Roslyn.Hosting;
using SemanticRepo.Agent.Roslyn.Hub;
using SemanticRepo.Agent.Roslyn.Infrastructure;
using SemanticRepo.Agent.Roslyn.Services;
using SemanticRepo.Contracts.Roslyn.Hierarchy;
using SemanticRepo.Contracts.Roslyn.Hub;
using SemanticRepo.Contracts.Roslyn.Metadata;
using SemanticRepo.Contracts.Roslyn.Symbols;
using SemanticRepo.Contracts.Roslyn.Syntax;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<RoslynRpcOptions>()
    .Bind(builder.Configuration.GetSection(RoslynRpcOptions.SectionName))
    .Validate(options => IPAddress.TryParse(options.Address, out _), "RoslynRpc:Address must be an IP address.")
    .Validate(options => options.Port is > 0 and <= 65535, "RoslynRpc:Port must be between 1 and 65535.")
    .Validate(options => options.MaxConnections > 0, "RoslynRpc:MaxConnections must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<IRoslynWorkspaceManager, RoslynWorkspaceManager>();
builder.Services.AddSingleton<ISyntaxTools, SyntaxTools>();
builder.Services.AddSingleton<IHierarchyTools, HierarchyTools>();
builder.Services.AddSingleton<ISymbolTools, SymbolTools>();
builder.Services.AddSingleton<ICompilerMetadataTools, CompilerMetadataTools>();
builder.Services.AddSingleton<IRoslynToolHub, RoslynToolHub>();
builder.Services.AddHostedService<RoslynRpcServerWorker>();

var host = builder.Build();
host.Run();
