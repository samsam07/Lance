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
