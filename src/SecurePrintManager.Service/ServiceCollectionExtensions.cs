using Microsoft.Extensions.DependencyInjection;
using SecurePrintManager.Service.Ipc;

namespace SecurePrintManager.Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSecurePrintManager(this IServiceCollection services)
    {
        services.AddSingleton<PrintManagerPipeServer>();
        services.AddHostedService<Worker>();
        return services;
    }
}
