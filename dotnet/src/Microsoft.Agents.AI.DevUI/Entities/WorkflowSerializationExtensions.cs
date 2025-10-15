// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Microsoft.Agents.AI.DevUI.Entities;

/// <summary>
/// Extension methods for serializing workflows to DevUI-compatible format
/// </summary>
internal static class WorkflowSerializationExtensions
{
    /// <summary>
    /// Converts a workflow to a dictionary representation compatible with DevUI frontend.
    /// This matches the Python workflow.to_dict() format expected by the UI.
    /// </summary>
    public static Dictionary<string, object> ToDevUIDict(this Workflow workflow)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = workflow.Name ?? Guid.NewGuid().ToString(),
            ["start_executor_id"] = workflow.StartExecutorId,
            ["max_iterations"] = 100 // Default value - .NET workflows don't have max_iterations
        };

        // Add optional fields
        if (!string.IsNullOrEmpty(workflow.Name))
        {
            result["name"] = workflow.Name;
        }

        if (!string.IsNullOrEmpty(workflow.Description))
        {
            result["description"] = workflow.Description;
        }

        // Convert executors to Python-compatible format
        result["executors"] = ConvertExecutorsToDict(workflow);

        // Convert edges to edge_groups format
        result["edge_groups"] = ConvertEdgesToEdgeGroups(workflow);

        return result;
    }

    /// <summary>
    /// Converts workflow executors to a dictionary format compatible with Python
    /// </summary>
    private static Dictionary<string, object> ConvertExecutorsToDict(Workflow workflow)
    {
        var executors = new Dictionary<string, object>();

        // Extract executor IDs from edges and start executor
        // (Registrations is internal, so we infer executors from the graph structure)
        var executorIds = new HashSet<string> { workflow.StartExecutorId };

        var reflectedEdges = workflow.ReflectEdges();
        foreach (var (sourceId, edgeSet) in reflectedEdges)
        {
            executorIds.Add(sourceId);
            foreach (var edge in edgeSet)
            {
                foreach (var sinkId in edge.Connection.SinkIds)
                {
                    executorIds.Add(sinkId);
                }
            }
        }

        // Create executor entries (we can't access internal Registrations for type info)
        foreach (var executorId in executorIds)
        {
            var executorDict = new Dictionary<string, object>
            {
                ["id"] = executorId,
                ["type"] = "Executor" // Generic type since we can't access registration details
            };

            executors[executorId] = executorDict;
        }

        return executors;
    }

    /// <summary>
    /// Converts workflow edges to edge_groups format expected by the UI
    /// </summary>
    private static List<object> ConvertEdgesToEdgeGroups(Workflow workflow)
    {
        var edgeGroups = new List<object>();
        var edgeGroupId = 0;

        // Get edges using the public ReflectEdges method
        var reflectedEdges = workflow.ReflectEdges();

        foreach (var (sourceId, edgeSet) in reflectedEdges)
        {
            foreach (var edgeInfo in edgeSet)
            {
                if (edgeInfo is DirectEdgeInfo directEdge)
                {
                    // Single edge group for direct edges
                    var edges = new List<object>();

                    foreach (var source in directEdge.Connection.SourceIds)
                    {
                        foreach (var sink in directEdge.Connection.SinkIds)
                        {
                            var edge = new Dictionary<string, object>
                            {
                                ["source_id"] = source,
                                ["target_id"] = sink
                            };

                            // Add condition name if this is a conditional edge
                            if (directEdge.HasCondition)
                            {
                                edge["condition_name"] = "condition";
                            }

                            edges.Add(edge);
                        }
                    }

                    edgeGroups.Add(new Dictionary<string, object>
                    {
                        ["id"] = $"edge_group_{edgeGroupId++}",
                        ["type"] = "SingleEdgeGroup",
                        ["edges"] = edges
                    });
                }
                else if (edgeInfo is FanOutEdgeInfo fanOutEdge)
                {
                    // FanOut edge group
                    var edges = new List<object>();

                    foreach (var source in fanOutEdge.Connection.SourceIds)
                    {
                        foreach (var sink in fanOutEdge.Connection.SinkIds)
                        {
                            edges.Add(new Dictionary<string, object>
                            {
                                ["source_id"] = source,
                                ["target_id"] = sink
                            });
                        }
                    }

                    var fanOutGroup = new Dictionary<string, object>
                    {
                        ["id"] = $"edge_group_{edgeGroupId++}",
                        ["type"] = "FanOutEdgeGroup",
                        ["edges"] = edges
                    };

                    if (fanOutEdge.HasAssigner)
                    {
                        fanOutGroup["selection_func_name"] = "selector";
                    }

                    edgeGroups.Add(fanOutGroup);
                }
                else if (edgeInfo is FanInEdgeInfo fanInEdge)
                {
                    // FanIn edge group
                    var edges = new List<object>();

                    foreach (var source in fanInEdge.Connection.SourceIds)
                    {
                        foreach (var sink in fanInEdge.Connection.SinkIds)
                        {
                            edges.Add(new Dictionary<string, object>
                            {
                                ["source_id"] = source,
                                ["target_id"] = sink
                            });
                        }
                    }

                    edgeGroups.Add(new Dictionary<string, object>
                    {
                        ["id"] = $"edge_group_{edgeGroupId++}",
                        ["type"] = "FanInEdgeGroup",
                        ["edges"] = edges
                    });
                }
            }
        }

        return edgeGroups;
    }
}
