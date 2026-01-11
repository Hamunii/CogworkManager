using System.Text.Json;

namespace Cogwork.Core;

public interface ISaveWithJson;

public interface ISaveWithJson<T>;

public static class ISaveWithJsonExtensions
{
    internal static JsonSerializerOptions Options { get; } =
        new() { WriteIndented = true, AllowTrailingCommas = true };

    extension<T>(T self)
        where T : ISaveWithJson, new()
    {
        public void Save(string fileLocation)
        {
            var serialized = JsonSerializer.Serialize(self, Options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }

        // I don't care and this doesn't even apply here
#pragma warning disable CA1000 // Do not declare static members on generic types
        public static T LoadSavedData(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new();
            }

            using var stream = File.OpenRead(filePath);
            try
            {
                var data = JsonSerializer.Deserialize<T>(stream);
                if (data is { })
                    return data;
            }
            catch (JsonException ex)
            {
                Cog.Error("Error reading cache file: " + ex.ToString());
            }

            return new();
        }
    }
}
