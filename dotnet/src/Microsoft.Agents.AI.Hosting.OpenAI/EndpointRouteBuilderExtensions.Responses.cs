// Copyright (c) Microsoft. All rights reserved.

using System;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;

namespace Microsoft.Agents.AI.Hosting.OpenAI;

/// <summary>
/// Provides extension methods for mapping OpenAI capabilities to an <see cref="AIAgent"/>.
/// </summary>
public static partial class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps OpenAI Responses API endpoints to the specified <see cref="IEndpointRouteBuilder"/> for the given <see cref="AIAgent"/>.
    /// </summary>
    /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/> to add the OpenAI Responses endpoints to.</param>
    /// <param name="agentName">The name of the AI agent service registered in the dependency injection container. This name is used to resolve the <see cref="AIAgent"/> instance from the keyed services.</param>
    /// <param name="responsesPath">Custom route path for the responses endpoint.</param>
    /// <param name="conversationsPath">Custom route path for the conversations endpoint.</param>
    public static void MapOpenAIResponses(
        this IEndpointRouteBuilder endpoints,
        string agentName,
        [StringSyntax("Route")] string? responsesPath = null,
        [StringSyntax("Route")] string? conversationsPath = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(agentName);
        if (responsesPath is null || conversationsPath is null)
        {
            ValidateAgentName(agentName);
        }

        var agent = endpoints.ServiceProvider.GetRequiredKeyedService<AIAgent>(agentName);

        responsesPath ??= $"/{agentName}/v1/responses";
        var responsesRouteGroup = endpoints.MapGroup(responsesPath);
        MapResponses(responsesRouteGroup, agent);

        // Will be included once we obtain the API to operate with thread (conversation).

        // conversationsPath ??= $"/{agentName}/v1/conversations";
        // var conversationsRouteGroup = endpoints.MapGroup(conversationsPath);
        // MapConversations(conversationsRouteGroup, agent, loggerFactory);
    }

    /// <summary>
    /// Maps a dynamic OpenAI Responses API endpoint that resolves agents based on the 'model' field in the request.
    /// This follows the Python DevUI server pattern where the model field contains the entity/agent name.
    /// </summary>
    /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/> to add the OpenAI Responses endpoints to.</param>
    /// <param name="basePath">Base path for the responses endpoint. Defaults to "/v1/responses".</param>
    /// <remarks>
    /// This implementation mirrors the Python DevUI server behavior:
    /// - Accepts requests at a single endpoint (default: /v1/responses)
    /// - Extracts agent name from the 'model' field in the request body
    /// - Dynamically resolves the agent from DI using the model name as the key
    /// - Validates that the requested agent exists before processing
    /// - Supports both streaming and non-streaming responses
    /// </remarks>
    public static void MapOpenAIResponses(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        basePath ??= "/v1/responses";
        var responsesRouteGroup = endpoints.MapGroup(basePath);
        MapDynamicResponses(responsesRouteGroup, endpoints.ServiceProvider);
    }

    private static void MapResponses(IEndpointRouteBuilder routeGroup, AIAgent agent)
    {
        var endpointAgentName = agent.DisplayName;
        var responsesProcessor = new AIAgentResponsesProcessor(agent);

        routeGroup.MapPost("/", async (HttpContext requestContext, CancellationToken cancellationToken) =>
        {
            var requestBinary = await BinaryData.FromStreamAsync(requestContext.Request.Body, cancellationToken).ConfigureAwait(false);

            var responseOptions = new ResponseCreationOptions();
            var responseOptionsJsonModel = responseOptions as IJsonModel<ResponseCreationOptions>;
            Debug.Assert(responseOptionsJsonModel is not null);

            responseOptions = responseOptionsJsonModel.Create(requestBinary, ModelReaderWriterOptions.Json);
            if (responseOptions is null)
            {
                return Results.BadRequest("Invalid request payload.");
            }

            return await responsesProcessor.CreateModelResponseAsync(responseOptions, cancellationToken).ConfigureAwait(false);
        }).WithName(endpointAgentName + "/CreateResponse");
    }

    private static void MapDynamicResponses(IEndpointRouteBuilder routeGroup, IServiceProvider serviceProvider)
    {
        routeGroup.MapPost("/", async (HttpContext requestContext, CancellationToken cancellationToken) =>
        {
            try
            {
                // Read the request body
                var requestBinary = await BinaryData.FromStreamAsync(requestContext.Request.Body, cancellationToken).ConfigureAwait(false);

                // Parse to extract the 'model' field (which contains the entity/agent name)
                // and normalize the 'input' field if it's a string
                string? entityId = null;
                BinaryData normalizedRequestBinary = requestBinary;

                try
                {
                    using var jsonDoc = JsonDocument.Parse(requestBinary);
                    if (jsonDoc.RootElement.TryGetProperty("model", out var modelProperty))
                    {
                        entityId = modelProperty.GetString();
                    }

                    // Check if 'input' is a string and needs to be converted to array format
                    // The Python DevUI accepts both string and array formats, but OpenAI .NET SDK expects array
                    // This is a HACK to to make DevUI work.
                    if (jsonDoc.RootElement.TryGetProperty("input", out var inputProperty) &&
                        inputProperty.ValueKind == JsonValueKind.String)
                    {
                        // Convert string input to OpenAI message format
                        var inputText = inputProperty.GetString() ?? string.Empty;

                        // Build a new request with normalized input
                        using var memoryStream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(memoryStream))
                        {
                            writer.WriteStartObject();

                            // Copy all existing properties except 'input'
                            foreach (var property in jsonDoc.RootElement.EnumerateObject())
                            {
                                if (property.Name != "input")
                                {
                                    writer.WritePropertyName(property.Name);
                                    property.Value.WriteTo(writer);
                                }
                            }

                            // Write normalized 'input' as array of message objects
                            writer.WritePropertyName("input");
                            writer.WriteStartArray();
                            writer.WriteStartObject();
                            writer.WriteString("type", "message");
                            writer.WriteString("role", "user");
                            writer.WritePropertyName("content");
                            writer.WriteStartArray();
                            writer.WriteStartObject();
                            writer.WriteString("type", "input_text");
                            writer.WriteString("text", inputText);
                            writer.WriteEndObject();
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                            writer.WriteEndArray();

                            writer.WriteEndObject();
                        }

                        normalizedRequestBinary = new BinaryData(memoryStream.ToArray());
                    }
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new
                    {
                        error = new
                        {
                            message = "Invalid JSON in request body.",
                            type = "invalid_request_error",
                            details = ex.Message
                        }
                    });
                }

                // Validate entity_id was provided
                if (string.IsNullOrWhiteSpace(entityId))
                {
                    return Results.BadRequest(new
                    {
                        error = new
                        {
                            message = "Missing 'model' field in request. The 'model' field must contain the agent/entity name.",
                            type = "invalid_request_error"
                        }
                    });
                }

                // Try to resolve the agent from DI using the entity_id as the key
                var agent = serviceProvider.GetKeyedService<AIAgent>(entityId);

                // Validate that the agent exists
                if (agent is null)
                {
                    return Results.NotFound(new
                    {
                        error = new
                        {
                            message = $"Entity '{entityId}' not found. Available entities must be registered as keyed services with the agent name as the key.",
                            type = "invalid_request_error"
                        }
                    });
                }

                // Parse the full request into ResponseCreationOptions
                var responseOptions = new ResponseCreationOptions();
                var responseOptionsJsonModel = responseOptions as IJsonModel<ResponseCreationOptions>;
                Debug.Assert(responseOptionsJsonModel is not null);

                try
                {
                    responseOptions = responseOptionsJsonModel.Create(normalizedRequestBinary, ModelReaderWriterOptions.Json);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        error = new
                        {
                            message = $"Failed to parse request: {ex.Message}",
                            type = "invalid_request_error"
                        }
                    });
                }

                if (responseOptions is null)
                {
                    return Results.BadRequest(new
                    {
                        error = new
                        {
                            message = "Invalid request payload.",
                            type = "invalid_request_error"
                        }
                    });
                }

                // Process the request with the resolved agent
                var responsesProcessor = new AIAgentResponsesProcessor(agent);
                return await responsesProcessor.CreateModelResponseAsync(responseOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Request was cancelled - return appropriate status
                return Results.StatusCode(499); // Client Closed Request
            }
            catch (Exception ex)
            {
                // Log the error (if you have logging configured)
                // logger?.LogError(ex, "Error processing OpenAI Responses request");

                return Results.Json(
                    new ErrorResponse
                    {
                        Error = new ErrorDetails
                        {
                            Message = $"Execution failed: {ex.Message}",
                            Type = "internal_error"
                        }
                    },
                    ErrorResponseJsonContext.Default.ErrorResponse,
                    statusCode: 500);
            }
        }).WithName("CreateResponse");
    }

#pragma warning disable IDE0051 // Remove unused private members
    private static void MapConversations(IEndpointRouteBuilder routeGroup, AIAgent agent)
#pragma warning restore IDE0051 // Remove unused private members
    {
        var endpointAgentName = agent.DisplayName;
        var conversationsProcessor = new AIAgentConversationsProcessor(agent);

        routeGroup.MapGet("/{conversation_id}", (string conversationId, CancellationToken cancellationToken)
            => conversationsProcessor.GetConversationAsync(conversationId, cancellationToken)
        ).WithName(endpointAgentName + "/RetrieveConversation");
    }

    private static void ValidateAgentName([NotNull] string agentName)
    {
        var escaped = Uri.EscapeDataString(agentName);
        if (!string.Equals(escaped, agentName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Agent name '{agentName}' contains characters invalid for URL routes.", nameof(agentName));
        }
    }
}
