namespace Domain.Entities;

/// <summary>A board shared into a folder: every member of the folder can open it.</summary>
public class FolderBoard
{
    public string FolderId { get; set; } = "";

    public string BoardId { get; set; } = "";
}
