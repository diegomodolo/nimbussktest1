using System.Text.Json.Serialization;

namespace SKNimbusDb.Configuration;

public class SemanticKernelOptions
{
    public const string SemanticKernel = "SemanticKernel";

    [JsonPropertyName("serviceId")] public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("deploymentOrModelId")]
    public string DeploymentOrModelId { get; set; } = string.Empty;

    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = string.Empty;
}