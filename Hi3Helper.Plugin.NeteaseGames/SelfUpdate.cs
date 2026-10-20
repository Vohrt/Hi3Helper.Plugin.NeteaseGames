using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.NeteaseGames;

/// <summary>
///     Self update of the plugin itself.
/// </summary>
[GeneratedComClass]
public partial class SelfUpdate : PluginSelfUpdateBase
{
    private static readonly string[] _baseCdnUrls =
    [
        "https://cl-plugins.sakurakoi.top/NeteaseGames/"
    ];

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => _baseCdnUrls;

    private HttpClient? _updateHttpClient;

    protected override HttpClient UpdateHttpClient
    {
        get
        {
            if (_updateHttpClient is not null)
            {
                return _updateHttpClient;
            }

            PluginHttpClientBuilder builder = new PluginHttpClientBuilder()
                .SetAllowedDecompression(System.Net.DecompressionMethods.All)
                .AllowRedirections()
                .AllowUntrustedCert()
                .AllowCookies();

            return _updateHttpClient = builder.Create();
        }
    }
}
