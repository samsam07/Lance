using Lance.Agent.Configuration;

namespace Lance.Agent.Infrastructure;

// A lightweight middleware is used here instead of the ASP.NET Core authentication
// stack (AddAuthentication + AuthenticationHandler<T>) because the only requirement
// is a single static token check. The full auth stack adds claims transformation,
// result caching, and multi-scheme support that Lance does not need.
internal sealed class BearerTokenMiddleware : IMiddleware
{
    private readonly AgentConfig _config;

    public BearerTokenMiddleware(AgentConfig config)
    {
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrEmpty(_config.Auth?.Token))
        {
            await next(context);
            return;
        }

        string? authHeader = context.Request.Headers.Authorization;
        bool valid = authHeader is not null
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && authHeader["Bearer ".Length..].Trim() == _config.Auth!.Token;

        if (!valid)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync(
                "{\"error\":\"invalid_token\",\"message\":\"A valid bearer token is required.\"}");
            return;
        }

        await next(context);
    }
}
