using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gamex.Notifications.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c>; creating migrations does not connect to the database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
            ?? "Server=localhost;Port=3310;Database=notifications_db;User=root;Password=localdev;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
