using RPM.Core.Audio;
using Sharpcaster;

namespace RPM.Desktop.Services.Cast;

/// <summary>
/// Finds Google Cast devices on the network. This is the only class in the desktop app that uses Zeroconf.
/// Everything after the connection uses <see cref="ICastReceiver"/>. The connection itself is in <see
/// cref="CastConnection"/>, which the phone head also uses.
/// </summary>
public sealed class CastService
{
    /// <summary>
    /// Browses for <c>_googlecast._tcp</c> and returns the devices that answer.
    ///
    /// <para>On macOS, the first call shows the Local Network permission prompt (in System Settings under
    /// Privacy &amp; Security, Local Network). Until the user allows it, the call returns an empty list.
    /// That looks the same as "no speakers", so the UI says so instead of spinning.</para>
    /// </summary>
    public async Task<IReadOnlyList<CastDevice>> DiscoverAsync(TimeSpan? timeout = null)
    {
        var sweep = timeout ?? TimeSpan.FromSeconds(3);
        var byId = new Dictionary<string, CastDevice>(StringComparer.Ordinal);

        // Run two sweeps and merge the results. mDNS can miss a device on any one round, so a single sweep
        // often returns too few speakers.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var locator = new ChromecastLocator(null);
                foreach (var receiver in await locator.FindReceiversAsync(fullTimeout: sweep))
                {
                    if (CastDevice.From(receiver) is { } device)
                        byId[device.Id] = device;
                }
            }
            catch
            {
                // No network, no Local Network permission or no speakers: all give the same result.
            }
        }

        return byId.Values
                   .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                   .ToList();
    }

    /// <summary>Connects to the device and starts the media receiver. Throws if it refuses.</summary>
    public async Task<ICastReceiver> ConnectAsync(CastDevice device) => await CastConnection.ConnectAsync(device);
}
