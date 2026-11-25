namespace Domain.Entities;

/// <summary>A small persistent host setting, such as the server's display name (keys in <see cref="Constants.SettingKeys"/>).</summary>
public class Setting
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}
