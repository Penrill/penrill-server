using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Linking.Commands.RevokeDevice;
using Penrill.Application.Linking.Queries.GetDevices;

namespace Penrill.Web.Endpoints;

/// <summary>The editors linked to the signed-in account.</summary>
public class Devices : IEndpointGroup
{
    public static string? RoutePrefix => "/api/devices";

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapGet(GetDevices);
        groupBuilder.MapPost(RevokeDevice, "/{id}/revoke");
    }

    [EndpointSummary("List linked devices")]
    public static async Task<Ok<IReadOnlyList<DeviceItemDto>>> GetDevices(ISender sender) => TypedResults.Ok(await sender.Send(new GetDevicesQuery()));

    [EndpointSummary("Unlink a device")]
    public static async Task<NoContent> RevokeDevice(ISender sender, string id)
    {
        await sender.Send(new RevokeDeviceCommand(id));
        return TypedResults.NoContent();
    }
}
