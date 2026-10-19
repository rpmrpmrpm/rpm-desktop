namespace RPM.Core.Radio;

/// <summary>
/// The broadcasters this app knows carry advertising, by name and by the site they publish from.
///
/// <para><b>Why a list is used, when a list is the thing to avoid.</b> The directory has no field for this,
/// and its tags do not fill the gap. Of the Dutch stations, only a couple have a tag that the tag rule can
/// read, and many have no tags at all. The tags that do exist describe the genre: pop, dance, classical.
/// Worldwide, the tag rule marks a number of stations, and none of them is Dutch. "public radio" is a
/// French, German and American tagging habit. A listener who looks at their own country's dial sees nothing
/// marked, even though a great deal of it carries advertising.</para>
///
/// <para><b>What it is allowed to be wrong about.</b> Only in one direction. Every entry here is a
/// broadcaster whose business model is a matter of public record: a public corporation funded partly by
/// spot advertising, or a commercial group that exists to sell it. A station this list does not recognise
/// is simply not marked. That is the same as any unknown station, which is not stated either way. So a
/// short list causes a missing mark, never a false one.</para>
///
/// <para><b>Matched on the site first.</b> A broadcaster's own domain is a far better key than its name.
/// "Joe", "3FM" and "538" are words, while joe.nl, npo3fm.nl and 538.nl are addresses that only one
/// organisation can publish from. The name is the fallback for stations that have no homepage in the
/// directory. It is matched on whole words, so that "NPO" does not match "Nippon".</para>
/// </summary>
public static class RadioBroadcasters
{
    /// <summary>Whether this station belongs to a broadcaster known to carry advertising.</summary>
    public static bool CarriesAds(string? name, string? homepage)
        => KnownSite(homepage) || KnownName(name);

    /// <summary>
    /// Whether the station publishes from a site that one of these broadcasters owns.
    ///
    /// <para>This is compared on the registrable part and its subdomains. So radio1.nl, www.nporadio1.nl
    /// and nporadio1.nl are one broadcaster, however the directory spells the address.</para>
    /// </summary>
    private static bool KnownSite(string? homepage)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var page))
            return false;

        var host = page.Host.ToLowerInvariant();
        foreach (var site in Sites)
            if (host == site || host.EndsWith("." + site, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// Whether the station's name opens with a broadcaster's own name.
    ///
    /// <para>Whole words, matched from the FRONT. A broadcaster puts its name at the start of a station's
    /// name: "NPO Radio 2", "BBC Radio 6 Music", "RTL 102.5". Matched anywhere, a short name such as "RTL"
    /// or "BBC" would also match unrelated station names that contain it.</para>
    /// </summary>
    private static bool KnownName(string? name)
    {
        var folded = Fold(name ?? "");
        if (folded.Length == 0)
            return false;

        foreach (var broadcaster in Names)
            if (folded == broadcaster || folded.StartsWith(broadcaster + " ", StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>A name with case, punctuation and runs of space removed. So "Q-music" becomes "q music",
    /// which matches the list entry for it.</summary>
    private static string Fold(string text)
    {
        var folded = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                folded.Append(char.ToLowerInvariant(c));
            else if (folded.Length > 0 && folded[^1] != ' ')
                folded.Append(' ');
        }

        return folded.ToString().TrimEnd();
    }

    /// <summary>
    /// The sites, registrable part only.
    /// </summary>
    /// <remarks>
    /// Public corporations and commercial groups are listed together, because the mark is about what the
    /// listener may hear rather than who owns the transmitter. NPO carries STER, which runs advertising.
    /// The BBC's domestic services are listed as well.
    /// </remarks>
    private static readonly string[] Sites =
    [
        // Netherlands: public (NPO, which carries STER advertising) and the commercial groups
        "npo.nl", "nporadio1.nl", "nporadio2.nl", "npo3fm.nl", "nporadio4.nl", "nporadio5.nl",
        "radio1.nl", "radio2.nl", "3fm.nl", "radio4.nl", "radio5.nl", "omroep.nl", "funx.nl",
        "538.nl", "radio538.nl", "skyradio.nl", "radio10.nl", "radioveronica.nl", "talpanetwork.com",
        "qmusic.nl", "joe.nl", "joe.be", "qmusic.be", "100p.nl", "slam.nl", "sublimefm.nl",
        "arrow.nl", "grootnieuwsradio.nl", "omroepgelderland.nl",

        // Belgium, Germany, France, Italy, Spain, UK, Ireland, Nordics, Austria, Switzerland
        "vrt.be", "rtbf.be", "radio1.be", "studiobrussel.be", "nostalgie.be",
        "ard.de", "wdr.de", "ndr.de", "swr.de", "br.de", "mdr.de", "rbb-online.de", "hr.de",
        "deutschlandfunk.de", "deutschlandradio.de", "1live.de", "antenne.de", "ffh.de", "radio.de",
        "radiofrance.fr", "franceinter.fr", "franceinfo.fr", "francemusique.fr", "fip.fr",
        "nrj.fr", "rtl.fr", "europe1.fr", "rmc.fr", "skyrock.com",
        "rai.it", "radiorai.it", "rtl.it", "radiodeejay.it", "radio105.net", "virginradio.it",
        "rtve.es", "cadenaser.com", "los40.com", "cope.es", "ondacero.es",
        "bbc.co.uk", "bbc.com", "global.com", "heart.co.uk", "capitalfm.com", "classicfm.com",
        "bauermedia.co.uk", "absoluteradio.co.uk", "talksport.com",
        "rte.ie", "todayfm.com",
        "sverigesradio.se", "nrk.no", "dr.dk", "yle.fi",
        "orf.at", "hitradio-oe3.at", "srf.ch", "rsi.ch", "rts.ch",

        // North America
        "npr.org", "wnyc.org", "iheart.com", "iheartmedia.com", "audacy.com", "cumulusmedia.com",
        "cbc.ca", "radio-canada.ca",
    ];

    /// <summary>
    /// The broadcaster names, for the stations that the directory files with no homepage. There are a good
    /// many of them. The names are folded the same way the station's name is.
    /// </summary>
    private static readonly string[] Names =
    [
        "npo", "npo radio", "npo 3fm", "3fm", "radio 538", "538", "sky radio", "radio 10",
        "radio veronica", "veronica", "qmusic", "q music", "joe", "100 nl", "slam", "sublime",
        "arrow classic rock", "funx", "bnr", "kink",
        "vrt", "studio brussel", "rtbf", "mnm",
        "ard", "wdr", "ndr", "swr", "br", "mdr", "hr", "1live", "deutschlandfunk", "antenne bayern",
        "radio france", "france inter", "france info", "france musique", "france culture", "nrj",
        "europe 1", "rtl", "rmc", "skyrock",
        "rai", "radio deejay", "radio 105", "virgin radio",
        "rne", "cadena ser", "los 40", "cope", "onda cero",
        "bbc", "heart", "capital", "classic fm", "smooth", "absolute radio", "talksport", "lbc",
        "rte", "today fm",
        "sveriges radio", "nrk", "dr p", "yle",
        "orf", "hitradio o3", "srf", "rsi", "rts",
        "npr", "wnyc", "cbc", "ici radio canada",
    ];
}
