using BeaconCore.Events;
using BeaconCore.Hooks;
using BeaconCore.Sessions;

namespace BeaconCore.Server;

public static class Endpoints
{
    public static void MapBeaconEndpoints(this WebApplication app, EventBus bus)
    {
        var normalizer = app.Services.GetRequiredService<HookNormalizer>();
        var transcriptWatcher = app.Services.GetRequiredService<CopilotTranscriptWatcher>();
        var orchestrator = app.Services.GetRequiredService<SessionOrchestrator>();
        var registry = app.Services.GetRequiredService<SessionRegistry>();

        app.MapGet("/events", (HttpContext ctx) => SseHandler.HandleSseConnection(ctx, bus));

        app.MapGet("/health", () => Results.Json(new { ok = true, version = "2.0.0" }));

        app.MapGet(
            "/state",
            (HttpContext ctx) =>
            {
                var sessionId = ctx.Request.Query["sessionId"].FirstOrDefault();
                if (sessionId is not null)
                {
                    var session = registry.TryGetSession(sessionId);
                    if (session is null)
                        return Results.NotFound(new { error = "Session not found" });

                    return Results.Json(
                        new
                        {
                            sessionId = session.SessionId,
                            windowHandle = $"0x{session.WindowHandle:X}",
                            source = session.Source.ToString(),
                            internalState = session.InternalState.ToString().ToLowerInvariant(),
                            publishedState = session.PublishedState.ToString().ToLowerInvariant(),
                        }
                    );
                }

                var sessions = registry
                    .GetAllSessions()
                    .Select(s => new
                    {
                        sessionId = s.SessionId,
                        windowHandle = $"0x{s.WindowHandle:X}",
                        source = s.Source.ToString(),
                        internalState = s.InternalState.ToString().ToLowerInvariant(),
                        publishedState = s.PublishedState.ToString().ToLowerInvariant(),
                    });

                return Results.Json(new { sessions });
            }
        );

        app.MapPost(
            "/hook",
            async (HttpContext ctx) =>
            {
                var logger = ctx.RequestServices.GetRequiredService<ILogger<HookNormalizer>>();

                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body);
                var rawBody = await reader.ReadToEndAsync();
                logger.LogTrace("Raw hook payload: {RawBody}", rawBody);
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

                var sessionId = payload.ResolvedSessionId;

                var result = normalizer.Normalize(payload);
                if (result is null)
                    return Results.Ok(new { accepted = true, mapped = false });

                if (result.TranscriptPath is not null)
                    transcriptWatcher.SetTranscriptPath(sessionId, result.TranscriptPath);

                if (result.Action == HookAction.WatchTranscript)
                    return Results.Ok(
                        new
                        {
                            accepted = true,
                            mapped = true,
                            action = result.Action.ToString(),
                            sessionId,
                        }
                    );

                orchestrator.HandleStateChange(
                    sessionId,
                    result.Source,
                    result.Action,
                    result.HookEvent,
                    result.Reason
                );

                return Results.Ok(
                    new
                    {
                        accepted = true,
                        mapped = true,
                        action = result.Action.ToString(),
                        sessionId,
                    }
                );
            }
        );
    }
}
