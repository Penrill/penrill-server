namespace Penrill.Domain.Entities;

/// <summary>A board's draw link: anyone with the token may join the board as a guest until the owner turns it off.</summary>
public class BoardLink
{
    public string BoardId { get; set; } = "";

    public string Token { get; set; } = "";
}
