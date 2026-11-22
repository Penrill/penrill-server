using Moq;
using NUnit.Framework;
using Penrill.Application.Common.Behaviours;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;
using Penrill.Application.Sharing.Commands.CreateFolder;
using Shouldly;

namespace Penrill.Application.UnitTests.Common.Behaviours;

public class AuthorizationBehaviourTests
{
    private static Task<string> Run(IUser user, IIdentityService identity) =>
        new AuthorizationBehaviour<CreateFolderCommand, string>(user, identity)
            .Handle(new CreateFolderCommand("7-B"), _ => Task.FromResult("ok"), default);

    [Test]
    public async Task AnonymousRequestsAreRefused()
    {
        var user = new Mock<IUser>();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => Run(user.Object, Mock.Of<IIdentityService>()));
    }

    [Test]
    public async Task GuestsCannotMakeFolders()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Id).Returns("kid-1f2e");
        var identity = new Mock<IIdentityService>();
        identity.Setup(i => i.AuthorizeAsync("kid-1f2e", Policies.NoGuests)).ReturnsAsync(false);
        await Should.ThrowAsync<ForbiddenAccessException>(() => Run(user.Object, identity.Object));
    }

    [Test]
    public async Task MembersCan()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Id).Returns("ann");
        var identity = new Mock<IIdentityService>();
        identity.Setup(i => i.AuthorizeAsync("ann", Policies.NoGuests)).ReturnsAsync(true);
        (await Run(user.Object, identity.Object)).ShouldBe("ok");
    }
}
