using YamlDotNet.Serialization;

namespace ManageUsers.Models;

/// <summary>
/// Represents C:\ProgramData\Management\Inventory.yaml written by provisioning/enrollment.
/// ManageUsers only reads this file.
/// </summary>
public sealed class InventoryData
{
    [YamlMember(Alias = "serial")]
    public string Serial { get; set; } = "";

    [YamlMember(Alias = "catalog")]
    public string Catalog { get; set; } = "";

    [YamlMember(Alias = "area")]
    public string Area { get; set; } = "";

    [YamlMember(Alias = "location")]
    public string Location { get; set; } = "";

    [YamlMember(Alias = "asset")]
    public string Asset { get; set; } = "";

    [YamlMember(Alias = "usage")]
    public string Usage { get; set; } = "";

    [YamlMember(Alias = "allocation")]
    public string Allocation { get; set; } = "";

    [YamlMember(Alias = "username")]
    public string Username { get; set; } = "";

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = "";

    [YamlMember(Alias = "platform")]
    public string Platform { get; set; } = "";
}
