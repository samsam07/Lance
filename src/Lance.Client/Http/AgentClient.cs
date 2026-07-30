using System.Net;
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

    public AgentClient(string agentUrl, int timeoutSeconds, string? token = null)
    {
        HttpClientHandler handler = new()
        {
            // TLS cert validation unconditionally disabled in Phase 2 (dev cert).
            // Will be configurable when PEM pinning / CA trust is added.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(agentUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<AgentResult<HealthResponse>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync("health", LanceSharedJsonContext.Default.HealthResponse, cancellationToken);
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

    public async Task<AgentResult<SessionResponse>> CreateSessionAsync(string sessionId, int count, CancellationToken cancellationToken = default)
    {
        CreateSessionRequest body = new() { SessionId = sessionId, Count = count };
        return await PostAsync(
            "sessions",
            body, LanceSharedJsonContext.Default.CreateSessionRequest,
            LanceSharedJsonContext.Default.SessionResponse,
            cancellationToken);
    }

    public async Task<AgentResult<SessionResponse>> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await GetAsync($"sessions/{sessionId}", LanceSharedJsonContext.Default.SessionResponse, cancellationToken);
    }

    public async Task<AgentResult<SessionsListResponse>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync("sessions", LanceSharedJsonContext.Default.SessionsListResponse, cancellationToken);
    }

    public async Task<AgentResult<bool>> DeleteSessionAsync(string sessionId, bool keepRunning, CancellationToken cancellationToken = default)
    {
        // keepRunning=true tells the agent to leave the session's Apollo running for a
        // fast reconnect; the default ends and stops the slots (tears down the displays).
        string path = keepRunning ? $"sessions/{sessionId}?keepRunning=true" : $"sessions/{sessionId}";
        return await DeleteAsync(path, cancellationToken);
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
                return ParseError<T>(body, response.StatusCode);

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
                return ParseError<TResult>(responseBody, response.StatusCode);

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
                return ParseError<bool>(responseBody, response.StatusCode);
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
                return ParseError<bool>(responseBody, response.StatusCode);
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

    // A non-success response usually carries a JSON ErrorResponse, but some failures
    // (400 body-binding, 401 from a proxy, 500) come back empty or non-JSON. Fall back
    // to the HTTP status so the caller always gets an actionable code, never a crash.
    private static AgentResult<T> ParseError<T>(string body, HttpStatusCode statusCode)
    {
        ErrorResponse? error = TryParseError(body);
        return new AgentResult<T>
        {
            ErrorCode = error?.Error ?? $"http_{(int)statusCode}",
            ErrorMessage = error?.Message ?? DescribeStatus(statusCode)
        };
    }

    private static ErrorResponse? TryParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, LanceSharedJsonContext.Default.ErrorResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 => "The agent rejected the request as malformed and returned no detail.",
        401 => "The agent rejected the request as unauthorized — check the auth token.",
        403 => "The agent refused the request (forbidden).",
        404 => "The agent does not recognize this request (not found).",
        >= 500 => "The agent hit an internal error. Check the agent log for details.",
        _ => "The agent returned an error with no detail."
    };

    public void Dispose()
    {
        _http.Dispose();
    }
}
