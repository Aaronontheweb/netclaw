namespace Netclaw.Actors.Protocol;

/// <summary>
/// Marker interface for messages routable to session actors.
/// Used by <see cref="Routing.SessionMessageExtractor"/> to extract entity IDs.
/// </summary>
public interface IWithSessionId
{
    string SessionId { get; }
}
