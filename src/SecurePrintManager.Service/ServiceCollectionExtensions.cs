using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecurePrintManager.Core;
using SecurePrintManager.Database;

namespace SecurePrintManager.Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSecurePrintServices(this IServiceCollection services)
    {
        // Register DatabaseContext
        services.AddDbContext<DatabaseContext>(options =>
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecurePrintManager",
                "secureprint.db"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            options.UseSqlite($"Data Source={dbPath}");
        });

        // Register Core services
        services.AddSingleton<FileEncryptionService>();
        services.AddSingleton<AuditLogger>();
        services.AddSingleton<QuotaManager>();
        services.AddSingleton<PrintMonitor>();

        return services;
    }
}
