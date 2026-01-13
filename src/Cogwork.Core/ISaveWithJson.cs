using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using static Cogwork.Core.ModList;
using static Cogwork.Core.ModList.ModListConfig;

namespace Cogwork.Core;

public interface ISaveWithJson;

public interface ISaveWithJson<T>;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true
)]
[JsonSerializable(typeof(ModListConfig))]
[JsonSerializable(typeof(Game.GameConfig))]
[JsonSerializable(typeof(Game.GlobalConfig))]
[JsonSerializable(typeof(PackageSourceCache))]
[JsonSerializable(typeof(string[]))]
internal partial class SourceGenerationContext : JsonSerializerContext { }

public static class ISaveWithJsonExtensions
{
    internal static JsonSerializerOptions Options { get; } =
        new JsonSerializerOptions { TypeInfoResolver = SourceGenerationContext.Default };

    extension<T>(T self)
        where T : ISaveWithJson, new()
    {
        public void Save(string fileLocation)
        {
            var serialized = JsonSerializer.Serialize(self, Options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }

        public void Save(string serialized, string fileLocation)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }

        // I don't care and this doesn't even apply here
#pragma warning disable CA1000 // Do not declare static members on generic types
        public static T LoadSavedData(string filePath) => LoadSavedData<T>(filePath, out _);

        public static T LoadSavedData(string filePath, out bool existed)
        {
            if (!File.Exists(filePath))
            {
                existed = false;
                return new();
            }

            using var stream = File.OpenRead(filePath);
            try
            {
                var data = JsonSerializer.Deserialize<T>(stream, Options);
                if (data is { })
                {
                    existed = true;
                    return data;
                }
            }
            catch (JsonException ex)
            {
                Cog.Error($"Error reading json file '{filePath}': " + ex.ToString());
            }

            existed = false;
            return new();
        }
    }
}
