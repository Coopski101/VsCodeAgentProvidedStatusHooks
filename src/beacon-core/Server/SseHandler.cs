using System.Text.Json;
using BeaconCore.Events;

namespace BeaconCore.Server;

public static class SseHandler
{
    public static async Task HandleSseConnection(HttpContext context, EventBus bus)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var reader = bus.Subscribe();
        var ct = context.RequestAborted;

        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                var data = JsonSerializer.Serialize(evt);
                await context.Response.WriteAsync($"event: {evt.EventType}\n", ct);
                await context.Response.WriteAsync($"data: {data}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            bus.Unsubscribe(reader);
        }
    }
}
