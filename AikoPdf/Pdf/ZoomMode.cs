namespace AikoPdf.Pdf;

/// <summary>How the zoom level is chosen.</summary>
public enum ZoomMode
{
    /// <summary>The widest page fills the viewport width.</summary>
    FitWidth,

    /// <summary>The largest page fits inside the viewport in both directions.</summary>
    FitPage,

    /// <summary>A fixed scale the user picked; does not follow window resizes.</summary>
    Custom,
}
