namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// The enqueue request itself is malformed, so no job is ever created. Surfaces
/// as HTTP 400 - distinct from a job that runs and fails, which is a 202
/// followed by a Failed status.
/// </summary>
public sealed class InvalidJobRequestException(string message) : Exception(message);
