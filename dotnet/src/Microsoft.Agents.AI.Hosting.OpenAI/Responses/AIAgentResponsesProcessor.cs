// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Buffers;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Model;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Utils;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses;

/// <summary>
/// OpenAI Responses processor associated with a specific <see cref="AIAgent"/>.
/// </summary>
internal sealed class AIAgentResponsesProcessor
{
    private readonly AIAgent _agent;

    /// <summary>
    /// Cached JsonSerializerOptions for serializing workflow event data with reflection support.
    /// </summary>
    private static readonly JsonSerializerOptions s_workflowDataSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AIAgentResponsesProcessor(AIAgent agent)
    {
        this._agent = agent ?? throw new ArgumentNullException(nameof(agent));
    }

    public async Task<IResult> CreateModelResponseAsync(ResponseCreationOptions responseCreationOptions, CancellationToken cancellationToken)
    {
        var options = new OpenAIResponsesRunOptions();
        AgentThread? agentThread = null; // not supported to resolve from conversationId

        var inputItems = responseCreationOptions.GetInput();
        var chatMessages = inputItems.AsChatMessages();

        if (responseCreationOptions.GetStream())
        {
            return new OpenAIStreamingResponsesResult(this._agent, chatMessages);
        }

        var agentResponse = await this._agent.RunAsync(chatMessages, agentThread, options, cancellationToken).ConfigureAwait(false);
        return new OpenAIResponseResult(agentResponse);
    }

    private sealed class OpenAIResponseResult(AgentRunResponse agentResponse) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // note: OpenAI SDK types provide their own serialization implementation
            // so we cant simply return IResult wrap for the typed-object.
            // instead writing to the response body can be done.

            var cancellationToken = httpContext.RequestAborted;
            var response = httpContext.Response;

            var chatResponse = agentResponse.AsChatResponse();
            var openAIResponse = chatResponse.AsOpenAIResponse();
            var openAIResponseJsonModel = openAIResponse as IJsonModel<OpenAIResponse>;
            Debug.Assert(openAIResponseJsonModel is not null);

            var writer = new Utf8JsonWriter(response.BodyWriter, new JsonWriterOptions { SkipValidation = false });
            openAIResponseJsonModel.Write(writer, ModelReaderWriterOptions.Json);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OpenAIStreamingResponsesResult(AIAgent agent, IEnumerable<ChatMessage> chatMessages) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var cancellationToken = httpContext.RequestAborted;
            var response = httpContext.Response;

            // Set SSE headers
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache,no-store";
            response.Headers.Connection = "keep-alive";
            response.Headers.ContentEncoding = "identity";
            httpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();

            return SseFormatter.WriteAsync(
                source: this.GetStreamingResponsesAsync(cancellationToken),
                destination: response.Body,
                itemFormatter: (sseItem, bufferWriter) =>
                {
                    var jsonTypeInfo = OpenAIResponsesJsonUtilities.DefaultOptions.GetTypeInfo(sseItem.Data.GetType());
                    var json = JsonSerializer.SerializeToUtf8Bytes(sseItem.Data, jsonTypeInfo);
                    bufferWriter.Write(json);
                },
                cancellationToken);
        }

        private async IAsyncEnumerable<SseItem<StreamingResponseEventBase>> GetStreamingResponsesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sequenceNumber = 1;
            var outputIndex = 0;
            AgentThread? agentThread = null;

            OpenAIResponse? lastOpenAIResponse = null;
            string? currentMessageId = null;
            var accumulatedText = new System.Text.StringBuilder();

            await foreach (var update in agent.RunStreamingAsync(chatMessages, thread: agentThread, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                // Check if this update contains a WorkflowEvent in RawRepresentation
                if (update.RawRepresentation is WorkflowEvent workflowEvent)
                {
                    // Emit workflow event
                    var workflowEventResponse = this.CreateWorkflowEventResponse(workflowEvent, sequenceNumber++, outputIndex);
                    if (workflowEventResponse != null)
                    {
                        yield return new(workflowEventResponse, workflowEventResponse.Type);
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(update.ResponseId)
                    && string.IsNullOrEmpty(update.MessageId)
                    && update.Contents is not { Count: > 0 })
                {
                    continue;
                }

                if (sequenceNumber == 1)
                {
                    lastOpenAIResponse = update.AsChatResponse().AsOpenAIResponse();

                    var responseCreated = new StreamingCreatedResponse(sequenceNumber++)
                    {
                        Response = lastOpenAIResponse
                    };
                    yield return new(responseCreated, responseCreated.Type);

                    // Initialize output index for the first message
                    outputIndex = 0;
                }

                if (update.Contents is not { Count: > 0 })
                {
                    continue;
                }

                // Extract text content from the update
                foreach (var content in update.Contents)
                {
                    if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        // If this is a new message (different ID), emit the accumulated message first
                        if (currentMessageId != null && currentMessageId != update.MessageId)
                        {
                            // Emit done event for previous message
                            var previousMessage = new ChatMessage(ChatRole.Assistant, accumulatedText.ToString())
                            {
                                MessageId = currentMessageId,
                            };

                            foreach (var openAIResponseItem in MicrosoftExtensionsAIResponsesExtensions.AsOpenAIResponseItems([previousMessage]))
                            {
                                var responseOutputDone = new StreamingOutputItemDoneResponse(sequenceNumber++)
                                {
                                    OutputIndex = outputIndex,
                                    Item = openAIResponseItem
                                };
                                yield return new(responseOutputDone, responseOutputDone.Type);
                            }

                            // Reset for new message
                            accumulatedText.Clear();
                            outputIndex++;
                        }

                        // Track current message ID
                        currentMessageId = update.MessageId;

                        // Emit text delta event for DevUI
                        var textDelta = new StreamingOutputTextDeltaResponse(sequenceNumber++)
                        {
                            OutputIndex = outputIndex,
                            ContentIndex = 0, // Always 0 for simple text content
                            Delta = textContent.Text
                        };
                        yield return new(textDelta, textDelta.Type);

                        // Accumulate text for final message
                        accumulatedText.Append(textContent.Text);
                    }
                }
            }

            // Emit the final accumulated message
            if (accumulatedText.Length > 0 && currentMessageId != null)
            {
                var finalMessage = new ChatMessage(ChatRole.Assistant, accumulatedText.ToString())
                {
                    MessageId = currentMessageId,
                };

                foreach (var openAIResponseItem in MicrosoftExtensionsAIResponsesExtensions.AsOpenAIResponseItems([finalMessage]))
                {
                    if (currentMessageId is not null)
                    {
                        openAIResponseItem.SetId(currentMessageId);
                    }

                    var responseOutputDone = new StreamingOutputItemDoneResponse(sequenceNumber++)
                    {
                        OutputIndex = outputIndex,
                        Item = openAIResponseItem
                    };
                    yield return new(responseOutputDone, responseOutputDone.Type);
                }
            }

            if (lastOpenAIResponse is not null)
            {
                // complete the whole streaming with the full response model
                var responseCompleted = new StreamingCompletedResponse(sequenceNumber++)
                {
                    Response = lastOpenAIResponse
                };
                yield return new(responseCompleted, responseCompleted.Type);
            }
        }

        /// <summary>
        /// Create a workflow event response for streaming
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Workflow event data serialization requires reflection for arbitrary types.")]
        [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Workflow event data serialization requires reflection for arbitrary types.")]
        private StreamingWorkflowEventResponse? CreateWorkflowEventResponse(WorkflowEvent workflowEvent, int sequenceNumber, int outputIndex)
        {
            // Extract executor_id if this is an ExecutorEvent
            string? executorId = null;
            if (workflowEvent is ExecutorEvent execEvent)
            {
                executorId = execEvent.ExecutorId;
            }

            // Serialize the workflow event data to JSON to avoid source generator issues
            // with arbitrary object types in the Data property
            var eventDataDict = new Dictionary<string, object?>
            {
                ["event_type"] = workflowEvent.GetType().Name,
                ["data"] = workflowEvent.Data,
                ["executor_id"] = executorId,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            // Convert to JsonElement using reflection-based serialization (not source-generated)
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(eventDataDict, s_workflowDataSerializerOptions);
            var eventData = JsonDocument.Parse(jsonBytes).RootElement;

            // Create the properly typed streaming workflow event
            return new StreamingWorkflowEventResponse(sequenceNumber)
            {
                OutputIndex = outputIndex,
                Data = eventData,
                ExecutorId = executorId,
                ItemId = $"wf_{Guid.NewGuid().ToString("N")[..8]}"
            };
        }
    }
}
