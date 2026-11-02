using Microsoft.Extensions.DependencyInjection;

namespace SuperMOOO.Core.Conventions;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ServiceAttribute(ServiceLifetime lifetime = ServiceLifetime.Singleton) : Attribute
{
    public ServiceLifetime Lifetime => lifetime;
    public Type[] As { get; init; } = [];
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ScreenAttribute(string id, string title, string category, int order) : Attribute
{
    public string Id => id;
    public string Title => title;
    public string Category => category;
    public int Order => order;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsSectionAttribute(string id, string title, int order) : Attribute
{
    public string Id => id;
    public string Title => title;
    public int Order => order;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ScheduledJobAttribute(string cron) : Attribute
{
    public string Cron => cron;
}
