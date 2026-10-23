using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Parsing;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Hybrid endpoints that short-circuit YARP: the unified fleet catalog endpoints
/// (GET /api/tags, /v1/models, /api/ps) are served by <see cref="IFleetAggregationService"/>
/// (parallel fan-out with per-target budget + stale-while-revalidate), and
/// POST /api/show is model-directed with a 30 s in-memory response cache.
/// </summary>
public static class OllamaEndpointsExtensions
{
    public static WebApplication MapOllamaRouterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tags", async (IFleetAggregationService aggregation, IOptions<OllamaRouterOptions> options, HttpContext context) =>
        {
            var mergedModels = await aggregation.GetTagsFleetAsync(BuildCloudTagSyntheses(options.Value), context);

            var response = new JsonObject
            {
                ["models"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        app.MapGet("/api/ps", async (IFleetAggregationService aggregation, HttpContext context) =>
        {
            var mergedModels = await aggregation.GetPsFleetAsync(context);

            var response = new JsonObject
            {
                ["models"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        app.MapGet("/v1/models", async (IFleetAggregationService aggregation, IOptions<OllamaRouterOptions> options, HttpContext context) =>
        {
            var mergedModels = await aggregation.GetOpenAIModelsFleetAsync(BuildCloudModelSyntheses(options.Value), context);

            var response = new JsonObject
            {
                ["object"] = "list",
                ["data"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        app.MapPost("/api/show", async (IFleetAggregationService aggregation, IOptions<OllamaRouterOptions> options, HttpContext context) =>
        {
            var body = await ReadBodyAsync(context.Request);
            var modelName = OllamaRequestParser.ExtractModelName(body);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                var errorBody = new JsonObject
                {
                    ["error"] = "The model name is missing from the request."
                };
                return Results.Content(errorBody.ToJsonString(), "application/json", statusCode: StatusCodes.Status400BadRequest);
            }

            RoutingTarget? pinned = null;
            if (context.Request.Headers.TryGetValue("X-OllamaRouter-Target", out var pinHeader) &&
                !string.IsNullOrWhiteSpace(pinHeader.ToString()))
            {
                var pin = pinHeader.ToString()!;
                var match = options.Value.GetEffectiveTargets()
                    .FirstOrDefault(t => string.Equals(t.Name, pin, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    pinned = (RoutingTarget)match.Name!;
                }
            }

            var result = await aggregation.GetShowFleetAsync(modelName, pinned, context);
            if (result is null)
            {
                var errorBody = new JsonObject
                {
                    ["error"] = $"No online target currently hosts '{modelName}'."
                };
                return Results.Content(errorBody.ToJsonString(), "application/json", statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Content(result.ToJsonString(), "application/json");
        });

        return app;
    }

    // Cloud model synthesis for /api/tags (shape preserved exactly as before the fleet
    // aggregation; appended only when not already present in the merged catalog).
    private static IReadOnlyList<JsonNode>? BuildCloudTagSyntheses(OllamaRouterOptions options)
    {
        var syntheses = new List<JsonNode>();
        if (options.Models != null)
        {
            foreach (var (_, modelOpts) in options.Models)
            {
                if (!string.IsNullOrWhiteSpace(modelOpts.CloudModel))
                {
                    var cloudName = modelOpts.CloudModel;
                    syntheses.Add((JsonNode)new JsonObject
                    {
                        ["name"] = cloudName,
                        ["model"] = cloudName,
                        ["modified_at"] = DateTime.UtcNow.ToString("o"),
                        ["size"] = 0,
                        ["details"] = new JsonObject
                        {
                            ["family"] = "cloud",
                            ["parameter_size"] = "cloud",
                            ["quantization_level"] = "cloud"
                        }
                    });
                }
            }
        }

        return syntheses.Count > 0 ? syntheses : null;
    }

    // Cloud model synthesis for /v1/models (OpenAI shape, preserved exactly).
    private static IReadOnlyList<JsonNode>? BuildCloudModelSyntheses(OllamaRouterOptions options)
    {
        var syntheses = new List<JsonNode>();
        if (options.Models != null)
        {
            foreach (var (_, modelOpts) in options.Models)
            {
                if (!string.IsNullOrWhiteSpace(modelOpts.CloudModel))
                {
                    var cloudName = modelOpts.CloudModel;
                    syntheses.Add((JsonNode)new JsonObject
                    {
                        ["id"] = cloudName,
                        ["object"] = "model",
                        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["owned_by"] = "cloud"
                    });
                }
            }
        }

        return syntheses.Count > 0 ? syntheses : null;
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}
