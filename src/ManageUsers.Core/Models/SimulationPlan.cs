using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManageUsers.Models;

/// <summary>One thing a simulation found it would remove.</summary>
public sealed class PlanItem
{
    public const string Account = "account";
    public const string Orphan = "orphan";
    public const string Profile = "profile";

    /// <summary><see cref="Account"/>, <see cref="Orphan"/> (an account with no profile) or <see cref="Profile"/> (a profile with no account).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = Account;

    /// <summary>The account name, or the profile folder name. This is what <c>--only</c> takes.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>
/// What the last <c>manageusers --simulate</c> would have removed. The CLI writes it to
/// <see cref="AppConstants.SimulationPlanFile"/>, in its locked data folder, and the
/// Managed Users Cleanup app reads it to show the decision list and to build the
/// confirmation for a live cleanup.
/// </summary>
public sealed class SimulationPlan
{
    [JsonPropertyName("generated")]
    public DateTimeOffset Generated { get; set; }

    [JsonPropertyName("items")]
    public List<PlanItem> Items { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, PlanJsonContext.Default.SimulationPlan);

    public static SimulationPlan? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PlanJsonContext.Default.SimulationPlan);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The plan on disk, or null when there is none or it cannot be read.</summary>
    public static SimulationPlan? Load(string? path = null)
    {
        try
        {
            return FromJson(File.ReadAllText(path ?? AppConstants.SimulationPlanFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SimulationPlan))]
internal sealed partial class PlanJsonContext : JsonSerializerContext;
