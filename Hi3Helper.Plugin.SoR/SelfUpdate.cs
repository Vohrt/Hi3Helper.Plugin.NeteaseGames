using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.NeteaseGames.Core;
using Hi3Helper.Plugin.Core.Update;

namespace Hi3Helper.Plugin.SoR;

/// <summary>
///     Self update of the plugin itself.
/// </summary>
[GeneratedComClass]
public partial class SelfUpdate : PluginSelfUpdateBase
{
    private static readonly string[] _baseCdnUrls =
    [
        "https://cl-plugins.sakurakoi.top/SoR/"
    ];

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => _baseCdnUrls;

    private HttpClient? _updateHttpClient;

    /// <summary>
    ///     The client of the update download. Certificate validation stays enabled: the plugin downloads an
    ///     executable, so it must never accept an unverified server.
    /// </summary>
    protected override HttpClient UpdateHttpClient
        => _updateHttpClient ??= NeteaseGamesHttp.CreateClient(TimeSpan.FromSeconds(100));
}
