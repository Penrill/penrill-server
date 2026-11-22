using Microsoft.AspNetCore.Mvc.Testing;
using Penrill.Contract.Tests;

namespace Penrill.Application.FunctionalTests;

/// <summary>The editor contract on a self-hosted server.</summary>
public class ServerApiContractTests : ApiContract<Program>
{
    protected override Task<WebApplicationFactory<Program>> CreateFactoryAsync() =>
        Task.FromResult<WebApplicationFactory<Program>>(new ServerFactory(openRegistration: true));
}
