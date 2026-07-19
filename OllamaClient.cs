using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OllaMonitor
{
    public class OllamaClient
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        public class ModelDetails
        {
            [JsonPropertyName("parameter_size")]
            public string ParameterSize { get; set; } = "";

            [JsonPropertyName("quantization_level")]
            public string QuantizationLevel { get; set; } = "";

            [JsonPropertyName("family")]
            public string Family { get; set; } = "";
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

        public static async Task<List<ActiveModel>> GetActiveModelsAsync(string baseUrl)
        {
            try
            {
                string url = baseUrl.TrimEnd('/') + "/api/ps";
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<PsResponse>(content);
                    return data?.Models ?? new List<ActiveModel>();
                }
            }
            catch
            {
                // Silently catch exception to keep dashboard clean during offline states
            }
            return new List<ActiveModel>();
        }
    }
}
