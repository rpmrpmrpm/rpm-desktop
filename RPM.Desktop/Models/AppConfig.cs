namespace RPM.Desktop.Models;

/// <summary>Bound from appsettings.json "AppConfig" section via Uno.Extensions Configuration.</summary>
public record AppConfig
{
    public string? Environment { get; init; }

    /// <summary>The URL of the deployed identification Lambda (rpm-cloud-api). For development it can
    /// be replaced with the RPM_API_URL environment variable.</summary>
    public string? IdentificationApiUrl { get; init; }

    /// <summary>
    /// The application id of RPM's own Cast receiver, as issued by the Cast Developer Console. The
    /// value is empty until the receiver is registered. An empty value still works: casting then uses
    /// Google's default receiver, which has no next button on the speaker. For a development receiver,
    /// the RPM_CAST_APP_ID environment variable can replace this value.
    /// </summary>
    public string? CastReceiverAppId { get; init; }
}
