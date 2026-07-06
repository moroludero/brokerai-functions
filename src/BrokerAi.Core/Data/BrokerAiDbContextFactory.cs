using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BrokerAi.Core.Data;

/// <summary>
/// Lets `dotnet ef migrations add` construct the DbContext directly instead of
/// booting the Functions host (which calls host.Run() and blocks forever).
/// Only used by design-time tooling — never referenced at runtime.
/// </summary>
public sealed class BrokerAiDbContextFactory : IDesignTimeDbContextFactory<BrokerAiDbContext>
{
    public BrokerAiDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BROKERAI_MIGRATION_CONNECTION")
            ?? "Server=(localdb)\\mssqllocaldb;Database=BrokerAiDb;Trusted_Connection=True;MultipleActiveResultSets=true";

        var optionsBuilder = new DbContextOptionsBuilder<BrokerAiDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new BrokerAiDbContext(optionsBuilder.Options);
    }
}
