using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SecurePrintManager");
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        logger.LogInformation("SecurePrintManager Service started.");
        while (!token.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), token);
    }
}
