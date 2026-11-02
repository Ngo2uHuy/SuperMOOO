using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SuperMOOO.Infrastructure.Conventions;

public static class ServiceCollectionExtensions
{
    // Các interface mà mọi implementation đều được gom lại thành IEnumerable<T>, không cần [Service].
    private static readonly Type[] CollectedInterfaces = [typeof(IJobHandler), typeof(IPipelineStep), typeof(ICapabilityReporter), typeof(IPipelineCompletionObserver)];

    /// <summary>
    /// Đăng ký theo convention: [Service], các interface gom nhóm, và class có [Screen]/[SettingsSection]/[Operation].
    /// Phase sau chỉ cần thêm class, không sửa Program.cs.
    /// </summary>
    public static IServiceCollection AddConventionServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            var service = type.GetCustomAttribute<ServiceAttribute>();
            var contracts = (service?.As ?? []).Concat(CollectedInterfaces.Where(i => i.IsAssignableFrom(type))).Distinct().ToList();

            if (service is null && contracts.Count == 0)
            {
                if (type.IsDefined(typeof(ScreenAttribute)) || type.IsDefined(typeof(SettingsSectionAttribute)) || type.IsDefined(typeof(OperationAttribute)))
                    services.AddTransient(type);
                continue;
            }

            var lifetime = service?.Lifetime ?? ServiceLifetime.Singleton;
            services.Add(new ServiceDescriptor(type, type, lifetime));
            foreach (var contract in contracts)
                services.Add(new ServiceDescriptor(contract, sp => sp.GetRequiredService(type), lifetime));
        }

        return services;
    }
}
