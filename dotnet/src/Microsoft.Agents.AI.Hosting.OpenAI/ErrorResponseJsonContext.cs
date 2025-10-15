// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.AI.Hosting.OpenAI;

/// <summary>
/// Provides a JSON serialization context for error responses to support AOT and trimming.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class ErrorResponseJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Represents an error response from the API.
/// </summary>
internal sealed class ErrorResponse
{
    /// <summary>
    /// Gets or sets the error details.
    /// </summary>
    [JsonPropertyName("error")]
    public required ErrorDetails Error { get; set; }
}

/// <summary>
/// Represents the details of an error.
/// </summary>
internal sealed class ErrorDetails
{
    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    /// <summary>
    /// Gets or sets the error type.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}
