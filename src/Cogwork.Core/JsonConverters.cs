using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Sources;

namespace Cogwork.Core;

public class PackageReferenceConverter<T> : JsonConverter<T>
    where T : IPackageReference<T>
{
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var packageId = reader.GetString();

        if (T.TryCreateFrom(packageId!, out var reference))
            return reference;

        throw new InvalidOperationException($"Corrupt data '{packageId}'");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    public override T ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var packageId = reader.GetString();

        if (T.TryCreateFrom(packageId!, out var reference))
            return reference;

        throw new InvalidOperationException($"Corrupt data '{packageId}'");
    }

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        [DisallowNull] T value,
        JsonSerializerOptions options
    )
    {
        writer.WritePropertyName(value.ToString());
    }
}

public class PackageVersionNumberConverter : JsonConverter<PackageVersionNumber>
{
    public override PackageVersionNumber Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var version = reader.GetString();
        return new PackageVersionNumber(version ?? "0.0.0");
    }

    public override void Write(
        Utf8JsonWriter writer,
        PackageVersionNumber value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.ToString());
    }
}

public class VersionRangeConverter : JsonConverter<VersionRange>
{
    public override VersionRange Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var version = reader.GetString();
        // Cog.Warning("Reading: " + version);
        return VersionRange.ParseRange(version);
    }

    public override void Write(
        Utf8JsonWriter writer,
        VersionRange value,
        JsonSerializerOptions options
    )
    {
        // Cog.Warning($"Writing: {value}");
        writer.WriteStringValue(value.ToString());
    }
}

public class AuthorConverter : JsonConverter<Author>
{
    public override Author Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var authorName = reader.GetString();
        return new(
            authorName! // I don't care
        );
    }

    public override void Write(Utf8JsonWriter writer, Author value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Name);
    }
}

public class PackageSourceIdConverter : JsonConverter<PackageSourceId>
{
    public override PackageSourceId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => PackageSourceId.Parse(reader.GetString()!);

    public override void Write(
        Utf8JsonWriter writer,
        PackageSourceId value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.ToString());
}

public class PackageSourceConverter : JsonConverter<PackageSource>
{
    public override PackageSource Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var sourceId = PackageSourceId.Parse(reader.GetString()!);
        if (!PackageSourceIndex.TryParseSourceId(sourceId, out var packageSource))
            throw new InvalidOperationException($"Corrupt data '{sourceId}'");

        return packageSource;
    }

    public override void Write(
        Utf8JsonWriter writer,
        PackageSource value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value.ToString());
}
