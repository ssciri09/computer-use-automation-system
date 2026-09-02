using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cua.Core.Artifacts;

/// <summary>Canonical JSON conventions for artifacts, results, logs and evidence.</summary>
public static class CuaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null, // dictionary keys (output names, step ids) are data, not schema
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"null when deserializing {typeof(T).Name}");
}

/// <summary>File-based capability catalog: one JSON file per capability version.</summary>
public sealed class ArtifactStore(string directory)
{
    public string Directory { get; } = directory;

    public string Save(CapabilityArtifact artifact)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(artifact.CapabilityId, artifact.CapabilityVersion);
        File.WriteAllText(path, CuaJson.Serialize(artifact));
        return path;
    }

    public CapabilityArtifact Load(string path) =>
        CuaJson.Deserialize<CapabilityArtifact>(File.ReadAllText(path));

    public int NextVersion(string capabilityId)
    {
        var versions = List()
            .Where(a => a.Artifact.CapabilityId == capabilityId)
            .Select(a => a.Artifact.CapabilityVersion);
        return versions.DefaultIfEmpty(0).Max() + 1;
    }

    public IReadOnlyList<(string Path, CapabilityArtifact Artifact)> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var result = new List<(string, CapabilityArtifact)>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try { result.Add((f, Load(f))); }
            catch { /* not an artifact — skip */ }
        }
        return result;
    }

    public string PathFor(string capabilityId, int version) =>
        Path.Combine(Directory, $"{capabilityId}.v{version}.json");
}
