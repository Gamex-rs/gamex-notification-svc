using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Infrastructure.Messaging;
using Gamex.Notifications.Infrastructure.Persistence;
using Gamex.Notifications.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gamex.Notifications.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration["DB_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("DB_CONNECTION_STRING is not configured.");

        services.AddDbContext<AppDbContext>(options => options.UseMySQL(connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

        var key = JwtValidationOptions.LoadKey(configuration["JWT_PUBLIC_KEY"]);
        if (key is null && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("JWT_PUBLIC_KEY is not configured.");
        }

        services.AddSingleton(new JwtValidationOptions { Key = key, Issuer = configuration["JWT_ISSUER"], Audience = configuration["JWT_AUDIENCE"] });

        var messaging = new MessagingOptions { ActivityEventsQueueUrl = configuration["SQS_ACTIVITY_EVENTS_URL"] };
        services.AddSingleton(messaging);
        services.AddSingleton<IAmazonSQS>(_ =>
        {
            var region = configuration["AWS_DEFAULT_REGION"] ?? "eu-central-1";
            var config = new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
            if (configuration["AWS_SERVICE_URL"] is { Length: > 0 } serviceUrl)
            {
                // LocalStack: the SDK cannot infer the region from a custom host and would sign as us-east-1.
                config.ServiceURL = serviceUrl;
                config.AuthenticationRegion = region;
                return new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
            }

            return new AmazonSQSClient(config);
        });

        if (!string.IsNullOrWhiteSpace(messaging.ActivityEventsQueueUrl))
        {
            services.AddHostedService<ActivityEventsConsumer>();
        }

        return services;
    }
}
