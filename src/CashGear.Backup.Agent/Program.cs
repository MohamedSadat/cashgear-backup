using CashGear.Backup.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = AgentInfo.ServiceName);

// Phase 1 adds the gRPC server (Kestrel on a named pipe only, no TCP), Quartz, state.db and the engine.
var host = builder.Build();
await host.RunAsync();
