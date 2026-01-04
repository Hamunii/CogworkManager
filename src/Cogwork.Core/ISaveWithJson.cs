using System.Text.Json;

namespace Cogwork.Core;

public interface ISaveWithJson
{
    public string FileLocation { get; }
}

public interface ISaveWithJson<T>
{
    public string FileLocation { get; }
}

public static class ISaveWithJsonExtensions
{
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    extension<T>(T self)
        where T : ISaveWithJson
    {
        public void Save()
        {
            var serialized = JsonSerializer.Serialize(self, options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(self.FileLocation)!);
            File.WriteAllText(self.FileLocation, serialized);
        }
    }

    extension<T1, T2>(T1 self)
        where T1 : ISaveWithJson<T2>
    {
        public void Save(T2 data)
        {
            var serialized = JsonSerializer.Serialize(data, options);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(self.FileLocation)!);
            File.WriteAllText(self.FileLocation, serialized);
        }
    }
}
