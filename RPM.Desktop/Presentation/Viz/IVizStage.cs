namespace RPM.Desktop.Presentation.Viz;

/// <summary>
/// What the full-screen host needs of a visualizer, whichever way it is drawn: real geometry
/// (<see cref="RibbonStage"/>) or a full-screen fragment shader (<see cref="ButterflyStage"/>). Both are
/// <c>GLCanvasElement</c>s, so the host holds one as a UIElement for the tree and as this for control.
/// </summary>
internal interface IVizStage
{
    /// <summary>Runs the render loop only while the visualizer is on screen.</summary>
    void SetActive(bool on);

    /// <summary>The GL renderer string once the context is up, else null.</summary>
    string? RendererInfo { get; }

    /// <summary>Why the stage is not drawing (init or shader compile), or null when it is fine.</summary>
    string? LastError { get; }

    /// <summary>Raised on the UI thread when <see cref="LastError"/> or <see cref="RendererInfo"/>
    /// changes.</summary>
    event Action? CompileStateChanged;
}
