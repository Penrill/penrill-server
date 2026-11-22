namespace Penrill.Domain.Enums;

/// <summary>
/// <c>Guest</c> is the account a draw link makes for someone who just wants to draw on one board: it can work on boards
/// it was let into, but cannot create boards, folders or shared templates. Negative so that role-ordered lists put guests last.
/// </summary>
public enum Role { Guest = -1, Member = 0, Admin = 1, Owner = 2 }
