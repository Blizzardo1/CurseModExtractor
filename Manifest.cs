using Newtonsoft.Json;

namespace CurseModExtractor;

public class Manifest {
    [JsonProperty("minecraft")]
    public MinecraftData? Minecraft;
    [JsonProperty("manifestType")]
    public string? ManifestType;
    [JsonProperty("manifestVersion")]
    public string? ManifestVersion;
    [JsonProperty("name")]
    public string? Name;
    [JsonProperty("version")]
    public string? Version;
    [JsonProperty("author")]
    public string? Author;
    [JsonProperty("projectID")]
    public int ProjectId;
    [JsonProperty("files")]
    public List< FileData >? Files;
    [JsonProperty("overrides")]
    public string? Overrides;
    
    [JsonConstructor]
    public Manifest() {
        
    }

    public string[] GetForgeVersion() {
        List< string > versions = [];
        if (Minecraft?.ModLoaders == null) return ["N/A"];
        
        versions.AddRange(Minecraft?.ModLoaders!.Select(loader => loader.Id) ?? []);

        return versions.ToArray()!;
    }

    public class MinecraftData {

        [JsonProperty("version")] public string? Version;
        [JsonProperty("modLoaders")] public List< ModLoader >? ModLoaders;
        [JsonProperty("recommendedRam")] public int RecommendedRam;
    }

    public class ModLoader {
        [JsonProperty("id")] public string? Id;
        [JsonProperty("primary")] public bool Primary;
    }

    public class FileData {

        [JsonProperty("projectID")] public int ProjectId;
        [JsonProperty("fileID")] public int FileId;
        [JsonProperty("required")] public bool Required;

        public override string ToString() {
            return ProjectId + "/" + FileId;
        }

    }
}
