namespace AikoPdf.Pdf;

/// <summary>A PDF needs a password to open, or the one given is wrong. The reader is asked again when this happens.</summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class PdfPasswordException : Exception
{
    /// <summary>Creates the exception with its standard message.</summary>
    public PdfPasswordException()
        : base("The PDF needs a password, or the password given is wrong.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public PdfPasswordException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure behind it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PdfPasswordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
