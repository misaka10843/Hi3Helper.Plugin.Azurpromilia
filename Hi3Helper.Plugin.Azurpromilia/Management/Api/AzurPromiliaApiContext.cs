using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzurPromiliaResponse<GameVersionConfigData>))]
[JsonSerializable(typeof(AzurPromiliaResponse<GameExeInfoData>))]
[JsonSerializable(typeof(AzurPromiliaResponse<NewsConfigData>))]
[JsonSerializable(typeof(AzurPromiliaLocalManifest))]
public partial class AzurPromiliaApiContext : JsonSerializerContext;
