using Gamex.Notifications.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Gamex.Notifications.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IActivityEventHandler, ActivityEventHandler>();
        return services;
    }
}
