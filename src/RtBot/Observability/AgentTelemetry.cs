using System.Diagnostics;

namespace RtBot.Observability;

/// <summary>
/// Named spans for the agent's own operations.
/// </summary>
/// <remarks>
/// The Microsoft OpenTelemetry distro auto-instruments HTTP, ASP.NET Core and the Azure SDKs,
/// but Agent 365 also expects semantic agent spans — invoke_agent, inference, execute_tool —
/// and those have to be opened explicitly around the work they describe.
/// </remarks>
public static class AgentTelemetry
{
    public const string SourceName = "RtBot.Agent";

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartMeetingSpan(string callId, string? threadId)
    {
        Activity? activity = Source.StartActivity("invoke_agent", ActivityKind.Server);
        activity?.SetTag("agent.operation", "meeting_session");
        activity?.SetTag("teams.call_id", callId);

        if (threadId is not null)
        {
            activity?.SetTag("teams.thread_id", threadId);
        }

        return activity;
    }

    public static Activity? StartToolSpan(string toolName)
    {
        Activity? activity = Source.StartActivity("execute_tool", ActivityKind.Client);
        activity?.SetTag("gen_ai.tool.name", toolName);
        activity?.SetTag("agent.operation", "execute_tool");
        return activity;
    }

    public static Activity? StartInferenceSpan(string model)
    {
        Activity? activity = Source.StartActivity("inference", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "azure.ai.voicelive");
        activity?.SetTag("gen_ai.request.model", model);
        return activity;
    }
}
