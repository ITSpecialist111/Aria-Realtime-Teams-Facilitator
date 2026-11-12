namespace RtBot.Controllers;

/// <summary>Authorisation policy names.</summary>
public static class AuthorizationPolicies
{
    /// <summary>Permits the operator API: joining meetings, listing calls, ending calls.</summary>
    public const string Operator = "RtBot.Operator";

    /// <summary>App role a service principal must hold to satisfy <see cref="Operator"/>.</summary>
    public const string OperatorRole = "RtBot.Operate";

    /// <summary>Delegated scope a signed-in user must hold to satisfy <see cref="Operator"/>.</summary>
    public const string OperatorScope = "Meetings.Join";
}
