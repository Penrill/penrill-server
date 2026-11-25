namespace Sync.Protocol;

/// <summary>Access level a user or guest holds on a board.</summary>
public enum BoardPermission
{
    None = 0,
    View = 1,
    Comment = 2,
    Edit = 3,
    Owner = 4
}
