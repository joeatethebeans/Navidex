namespace Melodex.Contracts.Plugins.Index;

/// <summary>
/// Thrown by an index plugin when its source can't answer right now: overloaded, rate-limiting, down or unreachable.
/// The core treats it as "try again later". It leaves what it was doing pending for the next scan rather than failing
/// it, and doesn't count it as "not found".
/// </summary>
public sealed class IndexUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
