using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lance.Shared.Dtos;
using Lance.Shared.Serialization;
using Serilog;

namespace Lance.Client.Http;

internal sealed class AgentClient : IDisposable
{
    private readonly HttpClient _http;

    public AgentClient(string agentUrl, int timeoutSeconds)
    {
        HttpClientHandler handler = new()
        {
            // Phase 1: TLS enforcement is deferred; accept any certificate.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(agentUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    public async Task<AgentResult<SlotsResponse>> GetSlotsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync("slots", LanceSharedJsonContext.Default.SlotsResponse, cancellationToken);
    }

    public async Task<AgentResult<ConfigUrlResponse>> GetSlotConfigUrlAsync(int slotId, CancellationToken cancellationToken = default)
    {
        return await GetAsync($"slots/{slotId}/config", LanceSharedJsonContext.Default.ConfigUrlResponse, cancellationToken);
    }

    public async Task<AgentResult<SlotsResponse>> AllocateSlotsAsync(int count, CancellationToken cancellationToken = default)
    {
        AllocateRequest body = new() { Count = count };
        return await PostAsync(
            "slots",
            body, LanceSharedJsonContext.Default.AllocateRequest,
            LanceSharedJsonContext.Default.SlotsResponse,
            cancellationToken);
    }

    public async Task<AgentResult<bool>> StartSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        return await PostNoBodyAsync($"slots/{slotId}/start", cancellationToken);
    }

    public async Task<AgentResult<bool>> StopSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        return await PostNoBodyAsync($"slots/{slotId}/stop", cancellationToken);
    }

    public async Task<AgentResult<bool>> DeallocateSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        return await DeleteAsync($"slots/{slotId}", cancellationToken);
    }

    public async Task<AgentResult<bool>> ForceDeallocateSlotAsync(int slotId, CancellationToken cancellationToken = default)
    {
        return await PostNoBodyAsync($"slots/{slotId}/force-deallocate", cancellationToken);
    }

    private async Task<AgentResult<T>> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        Log.Debug("GET {Path}", path);
        try
        {
            HttpResponseMessage response = await _http.GetAsync(path, cancellationToken);
            Log.Debug("Response {StatusCode} from {Path}", (int)response.StatusCode, path);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Debug("Response body: {Body}", body);

            if (!response.IsSuccessStatusCode)
                return ParseError<T>(body);

            T value = JsonSerializer.Deserialize(body, typeInfo)
                ?? throw new InvalidOperationException($"Null response body for {path}");
            return new AgentResult<T> { IsSuccess = true, Value = value };
        }
        catch (HttpRequestException ex)
        {
            Log.Debug("Agent unreachable: {Reason}", ex.Message);
            return new AgentResult<T> { IsUnreachable = true };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Request to {Path} timed out", path);
            return new AgentResult<T> { IsUnreachable = true };
        }
    }

    private async Task<AgentResult<TResult>> PostAsync<TBody, TResult>(
        string path,
        TBody body, JsonTypeInfo<TBody> bodyTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken cancellationToken)
    {
        Log.Debug("POST {Path}", path);
        try
        {
            string bodyJson = JsonSerializer.Serialize(body, bodyTypeInfo);
            Log.Debug("Request body: {Body}", bodyJson);
            using StringContent content = new(bodyJson, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _http.PostAsync(path, content, cancellationToken);
            Log.Debug("Response {StatusCode} from {Path}", (int)response.StatusCode, path);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Debug("Response body: {Body}", responseBody);

            if (!response.IsSuccessStatusCode)
                return ParseError<TResult>(responseBody);

            TResult value = JsonSerializer.Deserialize(responseBody, resultTypeInfo)
                ?? throw new InvalidOperationException($"Null response body for {path}");
            return new AgentResult<TResult> { IsSuccess = true, Value = value };
        }
        catch (HttpRequestException ex)
        {
            Log.Debug("Agent unreachable: {Reason}", ex.Message);
            return new AgentResult<TResult> { IsUnreachable = true };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Request to {Path} timed out", path);
            return new AgentResult<TResult> { IsUnreachable = true };
        }
    }

    private async Task<AgentResult<bool>> PostNoBodyAsync(string path, CancellationToken cancellationToken)
    {
        Log.Debug("POST {Path}", path);
        try
        {
            HttpResponseMessage response = await _http.PostAsync(path, content: null, cancellationToken);
            Log.Debug("Response {StatusCode} from {Path}", (int)response.StatusCode, path);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Debug("Response body: {Body}", responseBody);

            if (!response.IsSuccessStatusCode)
            {
                return ParseError<bool>(responseBody);
            }

            return new AgentResult<bool> { IsSuccess = true, Value = true };
        }
        catch (HttpRequestException ex)
        {
            Log.Debug("Agent unreachable: {Reason}", ex.Message);
            return new AgentResult<bool> { IsUnreachable = true };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Request to {Path} timed out", path);
            return new AgentResult<bool> { IsUnreachable = true };
        }
    }

    private async Task<AgentResult<bool>> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        Log.Debug("DELETE {Path}", path);
        try
        {
            HttpResponseMessage response = await _http.DeleteAsync(path, cancellationToken);
            Log.Debug("Response {StatusCode} from {Path}", (int)response.StatusCode, path);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Debug("Response body: {Body}", responseBody);

            if (!response.IsSuccessStatusCode)
            {
                return ParseError<bool>(responseBody);
            }

            return new AgentResult<bool> { IsSuccess = true, Value = true };
        }
        catch (HttpRequestException ex)
        {
            Log.Debug("Agent unreachable: {Reason}", ex.Message);
            return new AgentResult<bool> { IsUnreachable = true };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Request to {Path} timed out", path);
            return new AgentResult<bool> { IsUnreachable = true };
        }
    }

    private static AgentResult<T> ParseError<T>(string body)
    {
        ErrorResponse? error = JsonSerializer.Deserialize(body, LanceSharedJsonContext.Default.ErrorResponse);
        return new AgentResult<T>
        {
            ErrorCode = error?.Error,
            ErrorMessage = error?.Message
        };
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
