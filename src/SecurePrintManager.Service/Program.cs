using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.Service;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((hostContext, services) =>
            {
                // Register DatabaseContext
                services.AddDbContext<DatabaseContext>();

                // Register Core services
                services.AddSingleton<FileEncryptionService>();
                services.AddSingleton<AuditLogger>();
                services.AddSingleton<QuotaManager>();
                services.AddSingleton<PrintMonitor>();

                // Register Worker
                services.AddHostedService<Worker>();
            });
}
