using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Hybrid endpoints that short-circuit YARP to merge local and remote model catalogs
/// before returning them to the client.
/// </summary>
public static class OllamaEndpointsExtensions
{
    public static WebApplication MapOllamaRouterEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tags", async (IOllamaModelCatalogClient catalogClient, IOptions<OllamaRouterOptions> options) =>
        {
            var mergedModels = await catalogClient.GetMergedTagsAsync(options.Value.LocalUrl, options.Value.RemoteUrl);

            var response = new JsonObject
            {
                ["models"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        app.MapGet("/api/ps", async (IOllamaModelCatalogClient catalogClient, IOptions<OllamaRouterOptions> options) =>
        {
            var mergedModels = await catalogClient.GetMergedRunningModelsAsync(options.Value.LocalUrl, options.Value.RemoteUrl);

            var response = new JsonObject
            {
                ["models"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        app.MapGet("/v1/models", async (IOllamaModelCatalogClient catalogClient, IOptions<OllamaRouterOptions> options) =>
        {
            var mergedModels = await catalogClient.GetMergedOpenAIModelsAsync(options.Value.LocalUrl, options.Value.RemoteUrl);

            var response = new JsonObject
            {
                ["object"] = "list",
                ["data"] = mergedModels
            };

            return Results.Content(response.ToJsonString(), "application/json");
        });

        return app;
    }
}
