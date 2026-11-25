using Application.Accounts;
using Application.Common.Interfaces;
using ContractTests;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Application.FunctionalTests;

/// <summary>The editor contract on a self-hosted Penrill Server running on PostgreSQL.</summary>
public class ServerApiContractTests : ApiContract<Program>
{
    protected override async Task<WebApplicationFactory<Program>> CreateFactoryAsync()
    {
        await FunctionalTestSetup.DbResetter.ResetAsync();
        return new ServerFactory(openRegistration: true);
    }

    protected override async Task SeedBoardAssetBytesAsync(string boardId, long bytes)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        context.BoardAssets.Add(new BoardAsset { BoardId = boardId, Hash = new string('a', 64), ContentType = "image/png", Size = bytes });
        await context.SaveChangesAsync(default);
    }

    protected override async Task<string> CreateAdminInviteTokenAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<GrantService>();
        return (await grants.CreateInvite("system", Role.Admin, null, 1, default)).Token;
    }
}
