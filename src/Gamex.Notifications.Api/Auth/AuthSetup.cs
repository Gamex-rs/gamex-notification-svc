using System.Security.Claims;
using Gamex.Notifications.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.Notifications.Api.Auth;

internal static class AuthSetup
{
    public const string HubPath = "/hubs/notifications";

    public static IServiceCollection AddGamexAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtValidationOptions>((options, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Without a key (Development only) every token fails validation.
                    IssuerSigningKey = jwt.Key,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = !string.IsNullOrEmpty(jwt.Issuer),
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = !string.IsNullOrEmpty(jwt.Audience),
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "role",
                    NameClaimType = "name",
                };

                // WebSockets cannot send headers: SignalR passes the JWT as ?access_token= (SPEC-notification §4).
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(HubPath))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();

        return services;
    }

    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing sub claim."));
}
