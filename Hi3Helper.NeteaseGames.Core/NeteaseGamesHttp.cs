using System;
using System.Net.Http;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Creates the HTTP clients of the plugin.
///     <para>
///         Every client is created here so that the TLS configuration cannot drift apart between the
///         different call sites. Certificate validation is <b>never</b> disabled: every NetEase endpoint the
///         plugin talks to is a regular HTTPS host with a valid certificate, and the plugin feeds remote
///         input (host names and addresses from a manifest) into these requests.
///     </para>
/// </summary>
public static class NeteaseGamesHttp
{
    /// <summary>The user agent every request carries.</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CollapsePlugin/1.0";

    /// <summary>
    ///     Creates a client for the manifest and content endpoints.
    /// </summary>
    public static HttpClient CreateClient()
        => CreateClient(TimeSpan.FromSeconds(100));

    /// <summary>
    ///     Creates a client with an explicit timeout.
    /// </summary>
    /// <param name="timeout">The request timeout.</param>
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        HttpClient client = new(handler)
        {
            Timeout = timeout
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }
}
