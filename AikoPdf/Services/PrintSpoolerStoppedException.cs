namespace AikoPdf.Services;

/// <summary>The Windows print spooler is stopped, so no printer can be reached.</summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class PrintSpoolerStoppedException : Exception
{
    /// <summary>Creates the exception with its standard message.</summary>
    public PrintSpoolerStoppedException()
        : base("The Print Spooler service is not running.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public PrintSpoolerStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure behind it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PrintSpoolerStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
