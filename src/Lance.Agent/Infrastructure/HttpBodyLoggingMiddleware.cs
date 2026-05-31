using System.Text;

namespace Lance.Agent.Infrastructure;

internal sealed class HttpBodyLoggingMiddleware : IMiddleware
{
    private readonly ILogger<HttpBodyLoggingMiddleware> _logger;

    public HttpBodyLoggingMiddleware(ILogger<HttpBodyLoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Request.EnableBuffering();
        string requestBody = await ReadStreamAsync(context.Request.Body);
        context.Request.Body.Position = 0;
        if (requestBody.Length > 0)
        {
            _logger.LogDebug("Request body: {Body}", requestBody);
        }

        Stream originalBody = context.Response.Body;
        using MemoryStream capture = new();
        context.Response.Body = capture;
        try
        {
            await next(context);
        }
        finally
        {
            capture.Position = 0;
            string responseBody = await ReadStreamAsync(capture);
            if (responseBody.Length > 0)
            {
                _logger.LogDebug("Response body: {Body}", responseBody);
            }
            capture.Position = 0;
            await capture.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> ReadStreamAsync(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
