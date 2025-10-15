// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI.DevUI.Models.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Workflows;
using System.Runtime.CompilerServices;

namespace Microsoft.Agents.AI.DevUI.Services;

/// <summary>
/// Unified execution service that handles both agents and workflows
/// with real execution and proper OpenAI format mapping
/// </summary>
internal sealed class ExecutionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MessageMapperService _mapperService;
    private readonly ILogger<ExecutionService> _logger;

    public ExecutionService(
        IServiceProvider serviceProvider,
        MessageMapperService mapperService,
        ILogger<ExecutionService> logger)
    {
        this._serviceProvider = serviceProvider;
        this._mapperService = mapperService;
        this._logger = logger;
    }

    /// <summary>
    /// Execute entity and return simple response (non-streaming)
    /// </summary>
    public async Task<object> ExecuteEntityAsync(string entityId, DevUIExecutionRequest request)
    {
        // Try to get the entity from DI
        var agent = this._serviceProvider.GetServices<AIAgent>()
            .FirstOrDefault(a => a.Id == entityId || $"agent_{a.Id.ToLowerInvariant()}" == entityId);

        var workflow = this._serviceProvider.GetServices<Workflow>()
            .FirstOrDefault(w => w.Name == entityId || $"workflow_{w.Name?.ToLowerInvariant()}" == entityId);

        if (agent != null)
        {
            this._logger.LogInformation("Executing agent {EntityId}", entityId);
            return await this.ExecuteAgentAsync(agent, request);
        }
        else if (workflow != null)
        {
            this._logger.LogInformation("Executing workflow {EntityId}", entityId);
            return await this.ExecuteWorkflowAsync(workflow, request);
        }
        else
        {
            throw new InvalidOperationException($"Entity '{entityId}' not found");
        }
    }

    /// <summary>
    /// Execute entity with streaming support
    /// </summary>
    public async IAsyncEnumerable<object> ExecuteEntityStreamingAsync(
        string entityId,
        DevUIExecutionRequest request,
        AgentThread? thread = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Try to get the entity from DI
        var agent = this._serviceProvider.GetServices<AIAgent>()
            .FirstOrDefault(a => a.Id == entityId || $"agent_{a.Id.ToLowerInvariant()}" == entityId);

        var workflow = this._serviceProvider.GetServices<Workflow>()
            .FirstOrDefault(w => w.Name == entityId || $"workflow_{w.Name?.ToLowerInvariant()}" == entityId);

        if (agent != null)
        {
            this._logger.LogInformation("Executing agent {EntityId} with streaming", entityId);
            await foreach (var result in this.ExecuteAgentStreamingAsync(agent, request, thread, cancellationToken))
            {
                yield return result;
            }
        }
        else if (workflow != null)
        {
            this._logger.LogInformation("Executing workflow {EntityId} with streaming", entityId);
            // Execute workflow with real streaming support
            await foreach (var result in this.ExecuteWorkflowStreamingAsync(workflow, request, cancellationToken))
            {
                yield return result;
            }
        }
        else
        {
            var errorEvent = new
            {
                type = "error",
                error = new
                {
                    message = $"Entity '{entityId}' not found",
                    type = "entity_not_found"
                }
            };
            yield return errorEvent;
        }
    }

    /// <summary>
    /// Execute agent with streaming support
    /// </summary>
    public async IAsyncEnumerable<object> ExecuteAgentStreamingAsync(
        AIAgent agent,
        DevUIExecutionRequest request,
        AgentThread? thread = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Initialize streaming result outside try-catch
        IAsyncEnumerable<AgentRunResponseUpdate>? streamingResult = null;
        Exception? startupError = null;

        // Try to start streaming (with or without thread)
        try
        {
            if (thread != null)
            {
                // When using a thread, pass only the new user input as a string
                // The thread already contains the conversation history
                var userInput = request.GetLastMessageContent();
                this._logger.LogInformation("Executing agent {AgentId} with streaming, input: {Input}, thread: true",
                    agent.Id, userInput);
                streamingResult = agent.RunStreamingAsync(userInput, thread: thread, cancellationToken: cancellationToken);
            }
            else
            {
                // Without a thread, pass the full message history
                var messages = ConvertRequestToMessages(request);
                this._logger.LogInformation("Executing agent {AgentId} with streaming, {MessageCount} messages, thread: false",
                    agent.Id, messages.Length);
                streamingResult = agent.RunStreamingAsync(messages, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            startupError = ex;
        }

        // If startup failed, yield error and exit
        if (startupError != null)
        {
            this._logger.LogError(startupError, "Error starting agent execution {AgentId}", agent.Id);
            var errorEvent = new
            {
                id = Guid.NewGuid().ToString(),
                @object = "error",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                error = new
                {
                    message = $"Agent execution failed: {startupError.Message}",
                    type = "execution_error",
                    code = "agent_execution_failed"
                }
            };
            yield return errorEvent;
            yield break;
        }

        var sessionId = Guid.NewGuid().ToString();  // Same session for all events

        // Process streaming results and convert to OpenAI Responses API events
        await foreach (var update in streamingResult!.WithCancellation(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            IEnumerable<object>? events = null;
            Exception? conversionError = null;

            try
            {
                events = await this._mapperService.ConvertEventAsync(update, request, sessionId);
            }
            catch (Exception ex)
            {
                conversionError = ex;
                this._logger.LogError(ex, "Error converting streaming update for agent {AgentId}", agent.Id);
            }

            // Yield error or events (outside catch block)
            if (conversionError != null)
            {
                yield return new
                {
                    type = "error",
                    message = $"Streaming error: {conversionError.Message}"
                };
                yield break;
            }

            // Yield all events from mapper
            if (events != null)
            {
                foreach (var evt in events)
                {
                    yield return evt;
                }
            }
        }

        // Stream completes - controller will send [DONE]
    }

    /// <summary>
    /// Execute real agent (non-streaming)
    /// </summary>
    private async Task<object> ExecuteAgentAsync(AIAgent agent, DevUIExecutionRequest request)
    {
        try
        {
            // Convert request to framework messages
            var messages = ConvertRequestToMessages(request);

            this._logger.LogInformation("Executing agent {AgentId} with {MessageCount} messages", agent.Id, messages.Length);

            // Execute the agent
            var response = await agent.RunAsync(messages);

            // Extract text from response
            var responseText = response.Text ?? "No response text";

            // Convert to OpenAI format
            return this.CreateSimpleResponse(request, responseText);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error executing agent {AgentId}", agent.Id);
            return CreateErrorResponse(request, $"Agent execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute workflow with streaming support
    /// </summary>
    public async IAsyncEnumerable<object> ExecuteWorkflowStreamingAsync(
        Workflow workflow,
        DevUIExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Convert request to appropriate input
        var inputContent = request.GetLastMessageContent();
        this._logger.LogInformation("Executing workflow {WorkflowId} with streaming input: {Input}", workflow.Name, inputContent);

        // Start workflow execution with streaming support
        StreamingRun? streamingRun = null;
        Exception? startupError = null;

        try
        {
            var messages = ConvertRequestToMessages(request);
            streamingRun = await InProcessExecution.StreamAsync(workflow, messages, runId: null, cancellationToken);
        }
        catch (Exception ex)
        {
            startupError = ex;
        }

        // If startup failed, yield error and exit
        if (startupError != null)
        {
            this._logger.LogError(startupError, "Error starting workflow execution {WorkflowId}", workflow.Name);
            var errorEvent = new
            {
                id = Guid.NewGuid().ToString(),
                @object = "error",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                error = new
                {
                    message = $"Workflow execution failed: {startupError.Message}",
                    type = "execution_error",
                    code = "workflow_execution_failed"
                }
            };
            yield return errorEvent;
            yield break;
        }

        // Process workflow events in real-time using streaming
        var sessionId = Guid.NewGuid().ToString();
        var sequenceNumber = 0;

        if (streamingRun != null)
        {
            await foreach (var evt in streamingRun.WatchStreamAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                // Convert workflow event to response.workflow_event.complete format
                var workflowEvent = this._mapperService.ConvertWorkflowEvent(evt, sessionId, ++sequenceNumber);
                yield return workflowEvent;
            }
        }

        // Stream completes - controller will send [DONE]
    }

    /// <summary>
    /// Execute real workflow
    /// </summary>
    private async Task<object> ExecuteWorkflowAsync(Workflow workflow, DevUIExecutionRequest request)
    {
        try
        {
            // Get the input type and create appropriate input
            var workflowType = workflow.GetType();
            var inputContent = request.GetLastMessageContent();

            this._logger.LogInformation("Executing workflow {WorkflowId} with input: {Input}", workflow.Name, inputContent);

            var messages = ConvertRequestToMessages(request);
            var run = await InProcessExecution.RunAsync(workflow, messages).ConfigureAwait(false);
            return await this.ConvertWorkflowRunToOpenAIAsync(request, run, workflow.Name ?? "workflow").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error executing workflow {WorkflowId}", workflow.Name);
            return CreateErrorResponse(request, $"Workflow execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Convert workflow run events to OpenAI format
    /// </summary>
    private async Task<object> ConvertWorkflowRunToOpenAIAsync(DevUIExecutionRequest request, Run run, string workflowId)
    {
        var responseBuilder = new List<string>();

        // Process all workflow events
        foreach (var evt in run.OutgoingEvents)
        {
            var eventText = ConvertWorkflowEventToText(evt);
            if (!string.IsNullOrEmpty(eventText))
            {
                responseBuilder.Add(eventText);
            }
        }

        // If no meaningful events, create a basic response
        if (responseBuilder.Count == 0)
        {
            var status = await run.GetStatusAsync();
            responseBuilder.Add($"Workflow '{workflowId}' completed with status: {status}");
        }

        var finalResponse = string.Join("\n", responseBuilder);
        return this.CreateSimpleResponse(request, finalResponse);
    }

    /// <summary>
    /// Convert individual workflow event to text representation
    /// </summary>
    private static string ConvertWorkflowEventToText(WorkflowEvent evt)
    {
        return evt switch
        {
            AgentRunResponseEvent responseEvent =>
                $"Agent Response: {responseEvent.Response.Text ?? "No content"}",

            AgentRunUpdateEvent updateEvent =>
                $"Agent Update: {updateEvent.Update.Text ?? "Update"}",

            ExecutorCompletedEvent completedEvent =>
                $"Executor completed: {completedEvent.ExecutorId}",

            WorkflowStartedEvent startedEvent =>
                $"Workflow started: {startedEvent.Data?.ToString() ?? "Processing input"}",

            WorkflowErrorEvent errorEvent =>
                $"Workflow error: {(errorEvent.Data as Exception)?.Message ?? "Unknown error"}",

            WorkflowWarningEvent warningEvent =>
                $"Workflow warning: {warningEvent.Data?.ToString() ?? "Warning occurred"}",

            ExecutorInvokedEvent invokedEvent =>
                $"Executor '{invokedEvent.ExecutorId}' invoked: {invokedEvent.Data?.ToString() ?? "Processing"}",

            SuperStepStartedEvent stepStartedEvent =>
                $"Step {stepStartedEvent.StepNumber} started",

            SuperStepCompletedEvent stepCompletedEvent =>
                $"Step {stepCompletedEvent.StepNumber} completed",

            RequestInfoEvent requestEvent =>
                $"External request: {requestEvent.Data?.ToString() ?? "User input required"}",

            _ =>
                $"{evt.GetType().Name}: {evt.Data?.ToString() ?? "No data"}"
        };
    }

    /// <summary>
    /// Convert DevUI request to framework ChatMessage array
    /// </summary>
    private static ChatMessage[] ConvertRequestToMessages(DevUIExecutionRequest request)
    {
        // Use the improved parsing from the request model
        return request.ToChatMessages();
    }

    /// <summary>
    /// Create simple OpenAI-compatible response
    /// </summary>
    private object CreateSimpleResponse(DevUIExecutionRequest request, string content)
    {
        return new
        {
            id = Guid.NewGuid().ToString(),
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = request.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content
                    },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = EstimateTokens(request.GetLastMessageContent()),
                completion_tokens = EstimateTokens(content),
                total_tokens = EstimateTokens(request.GetLastMessageContent()) + EstimateTokens(content)
            }
        };
    }

    /// <summary>
    /// Create error response
    /// </summary>
    private static object CreateErrorResponse(DevUIExecutionRequest request, string message)
    {
        return new
        {
            id = Guid.NewGuid().ToString(),
            @object = "error",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = request.Model,
            error = new
            {
                message,
                type = "execution_error",
                code = "agent_execution_failed"
            }
        };
    }

    /// <summary>
    /// Estimate token count (rough approximation)
    /// </summary>
    private static int EstimateTokens(string text)
    {
        return (text?.Length ?? 0) / 4; // Rough estimate: 4 chars per token
    }
}
