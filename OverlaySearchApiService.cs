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
                throw new InvalidOperationException($"搜索接口返回错误：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlaySearchResponse>(responseStream, JsonOptions)
                   ?? new OverlaySearchResponse();
        }

        public async Task<OverlayMissionSearchResponse> SearchMissionsAsync(string query)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/missions/search?q={Uri.EscapeDataString(query)}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"任务搜索接口返回错误：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlayMissionSearchResponse>(responseStream, JsonOptions)
                   ?? new OverlayMissionSearchResponse();
        }

        public async Task<OverlayItemDetailResponse> GetDetailAsync(string categoryKey, int id)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/items/detail?categoryKey={Uri.EscapeDataString(categoryKey)}&id={id}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"详情接口返回错误：{(int)response.StatusCode} {response.ReasonPhrase}");
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
                throw new InvalidOperationException($"价格接口返回错误：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlayItemPriceResponse>(responseStream, JsonOptions)
                   ?? new OverlayItemPriceResponse();
        }

        public async Task<OverlayMissionDetailResponse> GetMissionDetailAsync(string id)
        {
            var apiBaseUrl = EnsureApiBaseUrl();
            var requestUrl = $"{apiBaseUrl.TrimEnd('/')}/api/missions/detail?id={Uri.EscapeDataString(id)}";

            using var response = await HttpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"任务详情接口返回错误：{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<OverlayMissionDetailResponse>(responseStream, JsonOptions)
                   ?? new OverlayMissionDetailResponse();
        }

        private string EnsureApiBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            {
                throw new InvalidOperationException("未检测到 API_BASE_URL，请检查 .env 配置。");
            }

            return ApiBaseUrl;
        }
    }
}
