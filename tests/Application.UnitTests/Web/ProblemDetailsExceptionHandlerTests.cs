using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Penrill.Application.Common.Exceptions;
using Penrill.Web.Infrastructure;
using Shouldly;

namespace Penrill.Application.UnitTests.Web;

/// <summary>Editors read the status and, for some refusals, a <c>reason</c> word from the JSON body.</summary>
public class ProblemDetailsExceptionHandlerTests
{
    private static async Task<(int Status, JsonObject Body)> Handle(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        (await new ProblemDetailsExceptionHandler().TryHandleAsync(context, exception, default)).ShouldBeTrue();
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, JsonNode.Parse(context.Response.Body)!.AsObject());
    }

    [TestCase(typeof(ConflictException), 409)]
    [TestCase(typeof(GoneException), 410)]
    [TestCase(typeof(PayloadTooLargeException), 413)]
    [TestCase(typeof(UnsupportedMediaTypeException), 415)]
    [TestCase(typeof(ServiceUnavailableException), 503)]
    public async Task MapsRefusalsToTheirStatus(Type type, int status)
    {
        var (actual, _) = await Handle((Exception)Activator.CreateInstance(type, [null])!);
        actual.ShouldBe(status);
    }

    [Test]
    public async Task CarriesTheReason()
    {
        var (status, body) = await Handle(new BadRequestException("hash-mismatch"));
        status.ShouldBe(400);
        ((string)body["reason"]!).ShouldBe("hash-mismatch");
    }

    [Test]
    public async Task AccessRefusalsAreUnauthorizedOrForbidden()
    {
        (await Handle(new UnauthorizedAccessException())).Status.ShouldBe(401);
        (await Handle(new ForbiddenAccessException())).Status.ShouldBe(403);
        (await Handle(new Ardalis.GuardClauses.NotFoundException("b", "Board"))).Status.ShouldBe(404);
    }
}
