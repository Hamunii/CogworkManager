using System.Text.Json;

namespace Cogwork.Core;

public interface ISaveWithJson;

public interface ISaveWithJson<T>;

public static class ISaveWithJsonExtensions
{
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    extension<T>(T self)
        where T : ISaveWithJson
    {
        public void Save(string fileLocation)
        {
            var serialized = JsonSerializer.Serialize(self, options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }
    }

    extension<T1, T2>(T1 self)
        where T1 : ISaveWithJson<T2>
    {
        public void Save(T2 data, string fileLocation)
        {
            var serialized = JsonSerializer.Serialize(data, options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fileLocation)!);
            File.WriteAllText(fileLocation, serialized);
        }
    }
}
