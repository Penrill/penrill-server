using Application.Sharing.Queries.GetCommunity;
using Application.Templates.Commands.DeleteTemplate;
using Application.Templates.Commands.PublishTemplate;
using Application.Templates.Commands.UpdateTemplate;
using Application.Templates.Queries.GetTemplate;
using Microsoft.AspNetCore.Authorization;

namespace Web.Controllers.Api;

[Authorize]
[Route("api/templates")]
public class TemplatesController : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<TemplateDto>> PublishTemplate([FromQuery] string name, [FromQuery] string? folder, CancellationToken ct)
    {
        var data = await Bodies.ReadAsync(Request, BoardLimitPolicy.SelfHostedDefault.MaxBoardBytes, ct);
        return Ok(await Mediator.Send(new PublishTemplateCommand(name, folder, data), ct));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<TemplateDto>> UpdateTemplate(string id, [FromQuery] string name, CancellationToken ct)
    {
        var data = await Bodies.ReadAsync(Request, BoardLimitPolicy.SelfHostedDefault.MaxBoardBytes, ct);
        return Ok(await Mediator.Send(new UpdateTemplateCommand(id, name, data), ct));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTemplate(string id, CancellationToken ct)
    {
        await Mediator.Send(new DeleteTemplateCommand(id), ct);
        return NoContent();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTemplate(string id, CancellationToken ct) =>
        File(await Mediator.Send(new GetTemplateQuery(id), ct), "application/json");
}
