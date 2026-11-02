using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace SuperMOOO.Infrastructure.Scheduling;

public static class QuartzSetup
{
    /// <summary>
    /// Đăng ký mọi class `IJob` có [ScheduledJob("&lt;cron Quartz&gt;")] cùng hosted service của Quartz. Cron theo cú pháp Quartz (có trường giây).
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, params Assembly[] assemblies)
    {
        var jobs = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IJob)) && t.IsDefined(typeof(ScheduledJobAttribute)))
            .ToList();

        services.AddQuartz(q =>
        {
            foreach (var type in jobs)
            {
                var key = new JobKey(type.FullName!);
                q.AddJob(type, j => j.WithIdentity(key));
                q.AddTrigger(t => t.ForJob(key).WithIdentity(type.FullName + ".trigger")
                    .WithCronSchedule(type.GetCustomAttribute<ScheduledJobAttribute>()!.Cron));
            }
        });
        return services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    }
}
