using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StarCitizenOverLay
{
    internal sealed class OverlaySearchApiService
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public string? ApiBaseUrl { get; } = OverlayApiConfiguration.LoadApiBaseUrl();

        public bool HasConfiguration => !string.IsNullOrWhiteSpace(ApiBaseUrl);

        public async Task<OverlaySearchResponse> SearchAsync(string query)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/items/search?q={Uri.EscapeDataString(query)}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"搜索请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlaySearchResponse>(responseStream, JsonOptions)
                   ?? new OverlaySearchResponse();
        }

        public async Task<OverlayItemDetailResponse> GetDetailAsync(string categoryKey, int id)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/items/detail?categoryKey={Uri.EscapeDataString(categoryKey)}&id={id}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"详情请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlayItemDetailResponse>(responseStream, JsonOptions)
                   ?? new OverlayItemDetailResponse();
        }

        public async Task<OverlayItemPriceResponse> GetPriceAsync(string categoryKey, int id)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/items/price?categoryKey={Uri.EscapeDataString(categoryKey)}&id={id}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"价格请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlayItemPriceResponse>(responseStream, JsonOptions)
                   ?? new OverlayItemPriceResponse();
        }

        private string EnsureApiBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            {
                throw new InvalidOperationException("缺少 .env 中的 API_BASE_URL。");
            }

            return ApiBaseUrl;
        }
    }
}
