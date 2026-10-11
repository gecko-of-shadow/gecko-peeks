namespace GeckoPeeks.Routing;

/// <summary>
/// One traceroute hop. A hop that did not answer has <see cref="Ip"/> of "*"
/// and <see cref="Responded"/> false so the path shape can still be logged.
/// </summary>
public sealed record HopObservation(
    int Number,
    string Ip,
    string Hostname,
    long? RttMs,
    bool Responded,
    bool ReachedTarget);
