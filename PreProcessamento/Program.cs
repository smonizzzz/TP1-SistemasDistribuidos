using PreProcessamento.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(5100, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<PreProcessamento.Services.PreProcessadorImpl>();
app.MapGet("/", () => "Servico de Pre-Processamento gRPC na porta 5100");

Console.WriteLine("================================================");
Console.WriteLine(">>> SERVICO PRE-PROCESSAMENTO gRPC (Porta 5100)");
Console.WriteLine("================================================");

app.Run();
