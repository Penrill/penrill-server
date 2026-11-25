using Microsoft.EntityFrameworkCore.Design;

namespace Infrastructure.Data;

/// <summary>Lets <c>dotnet ef</c> build the model for PostgreSQL migrations without starting the host.</summary>
public sealed class DesignTimeApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=penrill", npgsql => npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.GetName().Name))
        .Options);
}
