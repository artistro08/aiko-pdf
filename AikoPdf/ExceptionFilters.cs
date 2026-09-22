namespace AikoPdf;

/// <summary>
/// The one exception filter used wherever a general catch is allowed: event handlers, fire-and-forget tasks and
/// render loops, where one failure must not take down more than itself.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions
/// </remarks>
public static class ExceptionFilters
{
    /// <summary>
    /// True for exceptions an app can log and carry on from. Process-fatal failures (out of memory, stack overflow,
    /// access violations) pass through so they crash loudly instead of leaving corrupted state behind.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <returns>True when the exception is safe to swallow at a boundary.</returns>
    public static bool IsRecoverable(Exception ex)
        => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
