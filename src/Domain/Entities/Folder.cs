namespace Domain.Entities;

/// <summary>A group of people and the boards shared with them, such as a class.</summary>
public class Folder
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Owner { get; set; } = "";
}
