using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Data;

public static class InitialiserExtensions
{
    /// <summary>Brings the database up to date and makes sure the roles exist.</summary>
    public static async Task InitialiseDatabaseAsync(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>();
        await initialiser.InitialiseAsync();
        await initialiser.SeedAsync();
    }

    /// <summary>Makes sure the roles exist, for hosts whose deployment applies migrations itself.</summary>
    public static async Task SeedDatabaseAsync(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>().SeedAsync();
    }
}

public partial class ApplicationDbContextInitialiser(
    ILogger<ApplicationDbContextInitialiser> logger, ApplicationDbContext context, RoleManager<IdentityRole> roleManager)
{
    public async Task InitialiseAsync()
    {
        try
        {
            if (context.Database.GetMigrations().Any())
                await context.Database.MigrateAsync();
            else
                await context.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, "migrating");
            throw;
        }
    }

    public async Task SeedAsync()
    {
        try
        {
            foreach (var role in Enum.GetNames<Role>())
                if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole(role));
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, "seeding");
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An error occurred while {Step} the database.")]
    private static partial void LogFailed(ILogger logger, Exception error, string step);
}
