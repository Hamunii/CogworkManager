using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public class VisualPackageVersionConverter : JsonConverter<VisualPackageVersion>
{
    public override VisualPackageVersion Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var packageId = reader.GetString();

        // Let the constructor throw if property is null
        return new(packageId!);
    }

    public override void Write(
        Utf8JsonWriter writer,
        VisualPackageVersion value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.ToString());
    }

    public override VisualPackageVersion ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var property = reader.GetString();

        // Let the constructor throw if property is null
        return new(property!);
    }

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        [DisallowNull] VisualPackageVersion value,
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
