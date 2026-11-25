namespace Application.Common.Security;

public static class Policies
{
    /// <summary>Guests (people who came in by a board's draw link) work on that board only: they make no boards, folders, shares or templates.</summary>
    public const string NoGuests = "NoGuests";
}
