using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Sharing.Queries.GetCommunity;
using Penrill.Application.Templates.Commands.DeleteTemplate;
using Penrill.Application.Templates.Commands.PublishTemplate;
using Penrill.Application.Templates.Commands.UpdateTemplate;
using Penrill.Application.Templates.Queries.GetTemplate;
using Penrill.Domain.Constants;

namespace Penrill.Web.Endpoints;

/// <summary>Board files shared as templates. The body is the board file itself; name and folder come in the query.</summary>
public class Templates : IEndpointGroup
{
    public static string? RoutePrefix => "/api/templates";

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapPost(PublishTemplate);
        groupBuilder.MapPut(UpdateTemplate, "/{id}");
        groupBuilder.MapDelete(DeleteTemplate, "/{id}");
        groupBuilder.MapGet(GetTemplate, "/{id}");
    }

    [EndpointSummary("Share a template")]
    public static async Task<Ok<TemplateDto>> PublishTemplate(ISender sender, string name, string? folder, HttpRequest request, CancellationToken cancellationToken)
    {
        var data = await Bodies.ReadAsync(request, BoardLimits.MaxBoardBytes, cancellationToken);
        return TypedResults.Ok(await sender.Send(new PublishTemplateCommand(name, folder, data), cancellationToken));
    }

    [EndpointSummary("Edit a template in place")]
    public static async Task<Ok<TemplateDto>> UpdateTemplate(ISender sender, string id, string name, HttpRequest request, CancellationToken cancellationToken)
    {
        var data = await Bodies.ReadAsync(request, BoardLimits.MaxBoardBytes, cancellationToken);
        return TypedResults.Ok(await sender.Send(new UpdateTemplateCommand(id, name, data), cancellationToken));
    }

    [EndpointSummary("Stop sharing a template")]
    public static async Task<NoContent> DeleteTemplate(ISender sender, string id)
    {
        await sender.Send(new DeleteTemplateCommand(id));
        return TypedResults.NoContent();
    }

    [EndpointSummary("A template's board file")]
    public static async Task<FileContentHttpResult> GetTemplate(ISender sender, string id) =>
        TypedResults.Bytes(await sender.Send(new GetTemplateQuery(id)), "application/json");
}
