using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecurePrintManager.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SecurePrintManager");
builder.Services.AddSecurePrintManager();

await builder.Build().RunAsync();
