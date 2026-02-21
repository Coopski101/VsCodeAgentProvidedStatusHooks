using BeaconCore.Events;
using BeaconCore.Hooks;

namespace BeaconCore.Server;

public static class Endpoints
{
    public static void MapBeaconEndpoints(this WebApplication app, EventBus bus)
    {
        var normalizer = app.Services.GetRequiredService<HookNormalizer>();
        app.MapGet("/events", (HttpContext ctx) => SseHandler.HandleSseConnection(ctx, bus));

        app.MapGet("/health", () => Results.Json(new { ok = true, version = "2.0.0" }));

        app.MapGet(
            "/state",
            () =>
                Results.Json(
                    new
                    {
                        active = bus.ActiveSignal,
                        mode = bus.CurrentMode.ToString().ToLowerInvariant(),
                        source = bus.LastSource?.ToString(),
                    }
                )
        );

        app.MapPost(
            "/hook",
            async (HttpContext ctx) =>
            {
                HookPayload? payload;
                try
                {
                    payload = await ctx.Request.ReadFromJsonAsync<HookPayload>();
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON" });
                }

                if (payload is null)
                    return Results.BadRequest(new { error = "Empty payload" });

                var evt = normalizer.Normalize(payload);
                if (evt is null)
                    return Results.Ok(new { accepted = true, mapped = false });

                bus.Publish(evt);
                return Results.Ok(
                    new
                    {
                        accepted = true,
                        mapped = true,
                        eventType = evt.EventType.ToString(),
                    }
                );
            }
        );
    }
}
