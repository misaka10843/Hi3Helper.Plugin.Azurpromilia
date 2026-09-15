using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

/// <summary>
/// Source-generated JSON context. Required for Native AOT publishing (the plugin ships as a
/// trimmed/AOT native library, where reflection-based serialisation is unavailable).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzurPromiliaResponse<LauncherConfigData>))]
[JsonSerializable(typeof(AzurPromiliaResponse<GameVersionConfigData>))]
[JsonSerializable(typeof(AzurPromiliaResponse<GameExeInfoData>))]
[JsonSerializable(typeof(AzurPromiliaResponse<NewsConfigData>))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(TokenLoginRequest))]
[JsonSerializable(typeof(PasswordLoginRequest))]
[JsonSerializable(typeof(PhoneLoginRequest))]
[JsonSerializable(typeof(AzurPromiliaLocalManifest))]
public partial class AzurPromiliaApiContext : JsonSerializerContext;
