using Lance.Agent.Configuration;
using Lance.Agent.Services;
using Lance.Shared.Dtos;
using Lance.Shared.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lance.Agent.Endpoints;

internal static class SlotEndpoints
{
    public static void MapSlotEndpoints(this WebApplication app)
    {
        app.MapGet("/slots", GetSlots);
        app.MapGet("/slots/{id:int}", GetSlot);
        app.MapPost("/slots", AllocateSlots);
        app.MapDelete("/slots/{id:int}", DeallocateSlot);
        app.MapPost("/slots/{id:int}/start", StartSlot);
        app.MapPost("/slots/{id:int}/stop", StopSlot);
        app.MapPost("/slots/{id:int}/force-deallocate", ForceDeallocateSlot);
        app.MapGet("/slots/{id:int}/config", GetSlotConfig);
    }

    private static Ok<SlotsResponse> GetSlots(ISlotScanner scanner)
    {
        IReadOnlyList<SlotDto> slots = scanner.Scan();
        return TypedResults.Ok(new SlotsResponse { Slots = [.. slots] });
    }

    private static Results<Ok<SlotDto>, NotFound<ErrorResponse>> GetSlot(int id, ISlotScanner scanner)
    {
        IReadOnlyList<SlotDto> slots = scanner.Scan();
        SlotDto? slot = null;

        foreach (SlotDto s in slots)
        {
            if (s.Id == id)
            {
                slot = s;
                break;
            }
        }

        if (slot is null)
        {
            return TypedResults.NotFound(new ErrorResponse
            {
                Error = "slot_not_found",
                Message = $"Slot {id} does not exist."
            });
        }

        return TypedResults.Ok(slot);
    }

    private static Results<Ok<SlotsResponse>, BadRequest<ErrorResponse>, JsonHttpResult<ErrorResponse>> AllocateSlots(
        AllocateRequest request, ISlotAllocator allocator)
    {
        AllocateResult result = allocator.Allocate(request.Count);

        if (!result.IsSuccess)
        {
            ErrorResponse error = new() { Error = result.ErrorCode!, Message = result.ErrorMessage! };

            if (result.ErrorCode is "template_missing" or "io_error")
            {
                return TypedResults.Json(error, LanceSharedJsonContext.Default.ErrorResponse, statusCode: 500);
            }

            return TypedResults.BadRequest(error);
        }

        return TypedResults.Ok(new SlotsResponse { Slots = [.. result.Slots] });
    }

    private static Results<Ok, Conflict<ErrorResponse>> DeallocateSlot(int id, ISlotAllocator allocator)
    {
        DeallocateResult result = allocator.Deallocate(id);

        if (!result.IsSuccess)
        {
            return TypedResults.Conflict(new ErrorResponse
            {
                Error = result.ErrorCode!,
                Message = result.ErrorMessage!
            });
        }

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, JsonHttpResult<ErrorResponse>>> StartSlot(
        int id, ISlotLifecycle lifecycle, CancellationToken cancellationToken)
    {
        LifecycleResult result = await lifecycle.StartAsync(id, cancellationToken);

        if (!result.IsSuccess)
        {
            return TypedResults.Json(
                new ErrorResponse { Error = result.ErrorCode!, Message = result.ErrorMessage! },
                LanceSharedJsonContext.Default.ErrorResponse,
                statusCode: result.HttpStatus);
        }

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, JsonHttpResult<ErrorResponse>>> StopSlot(
        int id, ISlotLifecycle lifecycle, CancellationToken cancellationToken)
    {
        LifecycleResult result = await lifecycle.StopAsync(id, cancellationToken);

        if (!result.IsSuccess)
        {
            return TypedResults.Json(
                new ErrorResponse { Error = result.ErrorCode!, Message = result.ErrorMessage! },
                LanceSharedJsonContext.Default.ErrorResponse,
                statusCode: result.HttpStatus);
        }

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>>> ForceDeallocateSlot(
        int id, ISlotLifecycle lifecycle, ISlotAllocator allocator, CancellationToken cancellationToken)
    {
        LifecycleResult stopResult = await lifecycle.StopAsync(id, cancellationToken);
        if (!stopResult.IsSuccess)
        {
            return TypedResults.Json(
                new ErrorResponse { Error = stopResult.ErrorCode!, Message = stopResult.ErrorMessage! },
                LanceSharedJsonContext.Default.ErrorResponse,
                statusCode: stopResult.HttpStatus);
        }

        DeallocateResult deallocResult = allocator.Deallocate(id);
        if (!deallocResult.IsSuccess)
        {
            return TypedResults.Conflict(new ErrorResponse
            {
                Error = deallocResult.ErrorCode!,
                Message = deallocResult.ErrorMessage!
            });
        }

        return TypedResults.Ok();
    }

    private static Results<Ok<ConfigUrlResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, RedirectHttpResult> GetSlotConfig(
        int id, string? redirect, ISlotScanner scanner)
    {
        IReadOnlyList<SlotDto> slots = scanner.Scan();
        SlotDto? slot = null;

        foreach (SlotDto s in slots)
        {
            if (s.Id == id)
            {
                slot = s;
                break;
            }
        }

        if (slot is null)
        {
            return TypedResults.NotFound(new ErrorResponse
            {
                Error = "slot_not_found",
                Message = $"Slot {id} does not exist."
            });
        }

        if (slot.Status != "Running")
        {
            return TypedResults.Conflict(new ErrorResponse
            {
                Error = "slot_not_running",
                Message = $"Slot {id} is not running."
            });
        }

        string url = $"https://{slot.Host}:{slot.Port + 1}";

        if (string.Equals(redirect, "1", StringComparison.Ordinal))
        {
            return TypedResults.Redirect(url);
        }

        return TypedResults.Ok(new ConfigUrlResponse { Url = url });
    }
}
