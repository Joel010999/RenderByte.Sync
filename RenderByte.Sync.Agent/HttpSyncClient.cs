using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RenderByte.Sync.Contracts;

namespace RenderByte.Sync.Agent;

public sealed class HttpSyncClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;

    public HttpSyncClient(string apiUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        
        _httpClient = handler != null ? new HttpClient(handler) : new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<SyncBatchResponse?> SendBatchAsync(SyncBatchRequest request, CancellationToken ct)
    {
        var url = $"{_apiUrl}/v1/sync/movements";

        var response = await _httpClient.PostAsJsonAsync(url, request, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<SyncBatchResponse>(cancellationToken: ct);
            return result;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct);
        SyncErrorResponse? errorData = null;
        try 
        {
            errorData = JsonSerializer.Deserialize<SyncErrorResponse>(errorBody);
        }
        catch { }

        var exMsg = errorData?.Message ?? errorBody;
        var exErrorCode = errorData?.Error ?? response.StatusCode.ToString();
        
        throw new SyncApiException(response.StatusCode, exErrorCode, exMsg);
    }

    public async Task<ProductSyncResponse?> SendProductsBatchAsync(ProductSyncRequest request, CancellationToken ct)
    {
        var url = $"{_apiUrl}/v1/sync/products";

        var response = await _httpClient.PostAsJsonAsync(url, request, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<ProductSyncResponse>(cancellationToken: ct);
            return result;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct);
        SyncErrorResponse? errorData = null;
        try 
        {
            errorData = JsonSerializer.Deserialize<SyncErrorResponse>(errorBody);
        }
        catch { }

        var exMsg = errorData?.Message ?? errorBody;
        var exErrorCode = errorData?.Error ?? response.StatusCode.ToString();
        
        throw new SyncApiException(response.StatusCode, exErrorCode, exMsg);
    }

    public async Task<SyncStockBatchResponse?> SendStocksBatchAsync(SyncStockBatchRequest request, CancellationToken ct)
    {
        var url = $"{_apiUrl}/v1/sync/stocks";

        var response = await _httpClient.PostAsJsonAsync(url, request, ct);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<SyncStockBatchResponse>(cancellationToken: ct);
            return result;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct);
        SyncErrorResponse? errorData = null;
        try 
        {
            errorData = JsonSerializer.Deserialize<SyncErrorResponse>(errorBody);
        }
        catch { }

        var exMsg = errorData?.Message ?? errorBody;
        var exErrorCode = errorData?.Error ?? response.StatusCode.ToString();
        
        throw new SyncApiException(response.StatusCode, exErrorCode, exMsg);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public class SyncApiException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }

    public SyncApiException(System.Net.HttpStatusCode statusCode, string errorCode, string message) 
        : base($"[{errorCode}] {message} (HTTP {(int)statusCode})")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
