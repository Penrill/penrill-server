namespace Domain.Entities;

/// <summary>
/// A board file shared as a template with everyone (<see cref="Folder"/> empty) or with one folder.
/// <see cref="Updated"/> (Unix milliseconds) grows with every edit, so copies on devices can follow their author's changes.
/// </summary>
public class Template
{
    public string Id { get; set; } = "";

    public string Folder { get; set; } = "";

    public string Author { get; set; } = "";

    public string Name { get; set; } = "";

    public byte[] Data { get; set; } = [];

    public long Updated { get; set; }
}
