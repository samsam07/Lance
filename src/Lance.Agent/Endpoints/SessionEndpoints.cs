using System.Net;
using Lance.Agent.Sessions;
using Lance.Shared.Dtos;
using Lance.Shared.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lance.Agent.Endpoints;

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/sessions", CreateSession);
        app.MapGet("/sessions", GetSessions);
        app.MapGet("/sessions/{id}", GetSession);
        app.MapDelete("/sessions/{id}", DeleteSession);
    }

    private static Ok<SessionsListResponse> GetSessions(ISessionOrchestrator orchestrator)
    {
        return TypedResults.Ok(orchestrator.GetAllSessions());
    }

    private static Results<Ok<SessionResponse>, NotFound<ErrorResponse>> GetSession(string id, ISessionOrchestrator orchestrator)
    {
        SessionResponse? session = orchestrator.GetSession(id);
        if (session is null)
        {
            return TypedResults.NotFound(new ErrorResponse { Error = "session_not_found", Message = $"Session '{id}' is not active." });
        }

        return TypedResults.Ok(session);
    }

    // Clean-disconnect ping — the fast path. Idempotent: an unknown or already-ended
    // session is a no-op. Probe-watch backstops it if it never arrives. Teardown runs
    // detached from the request token: the client fires this ping as it exits, so tying
    // teardown to the request would cut off the hooks (e.g. audio restore) mid-way.
    private static async Task<Ok> DeleteSession(string id, ISessionOrchestrator orchestrator)
    {
        await orchestrator.EndSessionAsync(id, "ping", CancellationToken.None);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok<SessionResponse>, JsonHttpResult<ErrorResponse>>> CreateSession(
        CreateSessionRequest request, HttpContext http, ISessionOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        string clientIp = NormalizeIp(http.Connection.RemoteIpAddress);
        string agentIp = NormalizeIp(http.Connection.LocalIpAddress);

        SessionCreationResult result = await orchestrator.CreateSessionAsync(request.SessionId, request.Count, clientIp, agentIp, cancellationToken);
        if (!result.IsSuccess)
        {
            return TypedResults.Json(
                new ErrorResponse { Error = result.ErrorCode!, Message = result.ErrorMessage! },
                LanceSharedJsonContext.Default.ErrorResponse,
                statusCode: result.HttpStatus);
        }

        return TypedResults.Ok(new SessionResponse { SessionId = request.SessionId, Slots = [.. result.Slots] });
    }

    private static string NormalizeIp(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }
}
