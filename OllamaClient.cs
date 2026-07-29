using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OllaMascot
{
    public class OllamaClient
    {
        // 2s was too tight: /api/ps routinely misses it while the GPU is saturated by an inference,
        // which read as "Ollama is offline" even though the server was healthy
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public class ModelDetails
        {
            [JsonPropertyName("parameter_size")]
            public string ParameterSize { get; set; } = "";

            [JsonPropertyName("quantization_level")]
            public string QuantizationLevel { get; set; } = "";

            [JsonPropertyName("family")]
            public string Family { get; set; } = "";

            [JsonPropertyName("format")]
            public string Format { get; set; } = "";
        }

        public class ActiveModel
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("size_vram")]
            public long SizeVram { get; set; }

            [JsonPropertyName("details")]
            public ModelDetails Details { get; set; } = new ModelDetails();

            [JsonPropertyName("expires_at")]
            public DateTimeOffset? ExpiresAt { get; set; }

            [JsonPropertyName("context_length")]
            public long? ContextLength { get; set; }

            // Computed helper properties for UI Binding
            public double VramPercentage => Size > 0 ? Math.Clamp((double)SizeVram * 100.0 / Size, 0.0, 100.0) : 0.0;
            
            public string FormattedSize => FormatBytes(Size);
            
            public string FormattedVramInfo => $"{FormatBytes(SizeVram)} / {FormatBytes(Size)} ({VramPercentage:F0}% VRAM)";

            private static string FormatBytes(long bytes)
            {
                string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
                int counter = 0;
                decimal number = bytes;
                while (Math.Round(number / 1024) >= 1)
                {
                    number /= 1024;
                    counter++;
                }
                return $"{number:N1} {suffixes[counter]}";
            }
        }

        public class PsResponse
        {
            [JsonPropertyName("models")]
            public List<ActiveModel> Models { get; set; } = new List<ActiveModel>();
        }

        public class VersionResponse
        {
            [JsonPropertyName("version")]
            public string Version { get; set; } = "";
        }

        public class ShowResponse
        {
            [JsonPropertyName("details")]
            public ModelDetails Details { get; set; } = new ModelDetails();

            // Modelfile parameters as newline-separated "key value" pairs
            [JsonPropertyName("parameters")]
            public string Parameters { get; set; } = "";

            [JsonPropertyName("capabilities")]
            public List<string> Capabilities { get; set; } = new List<string>();

            // Keys are prefixed with the architecture (e.g. "llama.context_length")
            [JsonPropertyName("model_info")]
            public Dictionary<string, JsonElement> ModelInfo { get; set; } = new Dictionary<string, JsonElement>();

            public long? GetModelInfoNumber(string keySuffix)
            {
                foreach (var kv in ModelInfo)
                {
                    if (kv.Key.EndsWith(keySuffix, StringComparison.OrdinalIgnoreCase)
                        && kv.Value.ValueKind == JsonValueKind.Number
                        && kv.Value.TryGetInt64(out long value))
                    {
                        return value;
                    }
                }
                return null;
            }
        }

        public static async Task<(bool Success, List<ActiveModel> Models)> TryGetActiveModelsAsync(string baseUrl)
        {
            try
            {
                string url = baseUrl.TrimEnd('/') + "/api/ps";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<PsResponse>(content);
                    return (true, data?.Models ?? new List<ActiveModel>());
                }
            }
            catch
            {
                // Silently catch exception to keep dashboard clean during offline states
            }
            return (false, new List<ActiveModel>());
        }

        public static async Task<ShowResponse?> GetModelInfoAsync(string baseUrl, string modelName)
        {
            try
            {
                string url = baseUrl.TrimEnd('/') + "/api/show";
                using var body = new StringContent(
                    JsonSerializer.Serialize(new { model = modelName }),
                    System.Text.Encoding.UTF8,
                    "application/json");
                var response = await _httpClient.PostAsync(url, body);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<ShowResponse>(content);
                }
            }
            catch
            {
                // Silently catch exception to keep dashboard clean during offline states
            }
            return null;
        }

        public static async Task<string?> GetVersionAsync(string baseUrl)
        {
            try
            {
                string url = baseUrl.TrimEnd('/') + "/api/version";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<VersionResponse>(content);
                    if (!string.IsNullOrWhiteSpace(data?.Version))
                        return data.Version;
                }
            }
            catch
            {
                // Silently catch exception to keep dashboard clean during offline states
            }
            return null;
        }
    }
}
