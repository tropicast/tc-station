using System.Diagnostics;

namespace Tropicast.Station.App;

/// <summary>Opens a web page in the user's browser.</summary>
public interface IExternalBrowser
{
    void Open(Uri url);
}

internal sealed class ExternalBrowser : IExternalBrowser
{
    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        // Only web pages: never hand an arbitrary scheme from a server to the OS.
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Only web pages can be opened.", nameof(url));
        }
        using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
