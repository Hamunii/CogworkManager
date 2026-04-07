using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cogwork.Core;

public interface ISaveWithJson;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true
)]
[JsonSerializable(typeof(Game.GameConfig))]
[JsonSerializable(typeof(Game.GlobalConfig))]
[JsonSerializable(typeof(PackageSourceCache))]
[JsonSerializable(typeof(ModListData))]
[JsonSerializable(typeof(ModListLockFile))]
[JsonSerializable(typeof(PackageVersionNumber))]
[JsonSerializable(typeof(InstalledPackages))]
[JsonSerializable(typeof(List<Package>))]
[JsonSerializable(typeof(string[]))]
public partial class JsonGen : JsonSerializerContext { }

public static class ISaveWithJsonExtensions
{
    extension<T>(T self)
        where T : ISaveWithJson, new()
    {
        public void Save(string fileLocation)
        {
            var typeInfo = JsonGen.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
            Debug.Assert(typeInfo is { });
            Save(self, fileLocation, typeInfo);
        }

        public void Save(string fileLocation, JsonTypeInfo<T> typeInfo)
        {
            var serialized = JsonSerializer.Serialize(self, typeInfo);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }

        // I don't care and this doesn't even apply here
#pragma warning disable CA1000 // Do not declare static members on generic types
        public static T LoadSavedDataOrNew(string filePath)
        {
            var typeInfo = JsonGen.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
            Debug.Assert(typeInfo is { });
            return LoadSavedDataOrNew(filePath, typeInfo, out _);
        }

        public static T LoadSavedDataOrNew(string filePath, JsonTypeInfo<T> typeInfo) =>
            LoadSavedDataOrNew(filePath, typeInfo, out _);

        public static T LoadSavedDataOrNew(
            string filePath,
            JsonTypeInfo<T> typeInfo,
            out bool existed
        ) => LoadSavedData(filePath, typeInfo, out existed) ?? new();

        public static bool TryLoadSavedData(
            string filePath,
            JsonTypeInfo<T> typeInfo,
            [MaybeNullWhen(false)] out T data
        )
        {
            data = LoadSavedData(filePath, typeInfo, out var existed);
            return existed;
        }

        public static T? LoadSavedData(string filePath)
        {
            var typeInfo = JsonGen.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
            Debug.Assert(typeInfo is { });
            return LoadSavedData(filePath, typeInfo, out _);
        }

        public static T? LoadSavedData(string filePath, JsonTypeInfo<T> typeInfo) =>
            LoadSavedData(filePath, typeInfo, out _);

        public static T? LoadSavedData(string filePath, JsonTypeInfo<T> typeInfo, out bool existed)
        {
            if (!File.Exists(filePath))
            {
                existed = false;
                return default;
            }

            using var stream = File.OpenRead(filePath);
            try
            {
                var data = JsonSerializer.Deserialize(stream, typeInfo);
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
            return default;
        }
    }
}
