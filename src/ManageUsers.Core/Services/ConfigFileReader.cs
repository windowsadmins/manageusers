using ManageUsers.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManageUsers.Services;

/// <summary>
/// Reads Config.yaml the way a run does: missing, untrusted or unparsable all mean "no
/// file", with a message saying which. Shared so the app shows exactly what a run uses.
/// </summary>
public static class ConfigFileReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public sealed record Result(ConfigFile? File, string? Info, string? Warning);

    public static Result Read(FileTrust trust, string? path = null)
    {
        path ??= AppConstants.ConfigYamlPath;
        if (!System.IO.File.Exists(path))
            return new Result(null, $"Config file not found: {path}", null);

        var reason = trust.WhyUntrusted(path);
        if (reason != null)
            return new Result(null, null,
                $"Ignoring Config.yaml because a non-administrator could have written it: {reason}. " +
                "Replace it as an administrator, or reinstall ManageUsers to reset the folder's permissions.");

        try
        {
            return new Result(Deserializer.Deserialize<ConfigFile>(System.IO.File.ReadAllText(path)), null, null);
        }
        catch (Exception ex)
        {
            return new Result(null, null, $"Failed to parse Config.yaml: {ex.Message} — ignoring it");
        }
    }
}
