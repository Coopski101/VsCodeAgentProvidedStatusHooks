using BeaconCore.Events;
using BeaconCore.Hooks;

namespace BeaconCore.Server;

public static class Endpoints
{
    public static void MapBeaconEndpoints(this WebApplication app, EventBus bus)
    {
        var normalizer = app.Services.GetRequiredService<HookNormalizer>();
        var transcriptWatcher = app.Services.GetRequiredService<CopilotTranscriptWatcher>();
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
                var logger = ctx.RequestServices.GetRequiredService<ILogger<HookNormalizer>>();

                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body);
                var rawBody = await reader.ReadToEndAsync();
                logger.LogDebug("Raw hook payload: {RawBody}", rawBody);
                ctx.Request.Body.Position = 0;

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

                if (payload.TranscriptPath is not null)
                    transcriptWatcher.SetTranscriptPath(payload.TranscriptPath);

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
