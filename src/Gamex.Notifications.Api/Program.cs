using Gamex.Notifications.Api.Auth;
using Gamex.Notifications.Api.Endpoints;
using Gamex.Notifications.Api.Hubs;
using Gamex.Notifications.Application;
using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Infrastructure;
using Gamex.Notifications.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddGamexAuth();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapNotificationEndpoints();
app.MapHub<NotificationsHub>(AuthSetup.HubPath);

if (app.Configuration.GetValue("DB_MIGRATE_ON_STARTUP", app.Environment.IsDevelopment()))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
