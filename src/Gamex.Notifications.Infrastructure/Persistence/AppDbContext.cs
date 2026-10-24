using Gamex.Notifications.Application.Abstractions;
using Gamex.Notifications.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;

namespace Gamex.Notifications.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    private const int MySqlDuplicateEntry = 1062;

    public DbSet<Notification> Notifications => Set<Notification>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.BeginTransactionAsync(cancellationToken);

    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: MySqlDuplicateEntry };

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplySnakeCaseNames();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Guid>().HaveColumnType("char(36)");
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime(6)").HaveConversion<UtcDateTimeConverter>();
    }
}
