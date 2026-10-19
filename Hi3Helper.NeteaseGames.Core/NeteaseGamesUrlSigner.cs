using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     Signs the URLs of the NetEase content CDN.
///     <para>
///         The official launcher appends two query parameters to every request it sends:
///         <c>?key1=&lt;md5&gt;&amp;key2=&lt;8 hex digits&gt;</c>, where
///         <list type="bullet">
///             <item><c>key1 = md5(SECRET + path + key2)</c> and <c>path</c> is the request path (without the host),</item>
///             <item><c>key2 = hex(unix time + 43200)</c> - the current time shifted by twelve hours.</item>
///         </list>
///         The secret is a constant inside <c>PCLauncher.exe</c> (it sits right next to the <c>key1</c>/<c>key2</c>
///         literals of the DRPF module). Without the signature the content CDN answers with
///         <c>403 Forbidden</c>; with it the request is accepted.
///     </para>
/// </summary>
public static class NeteaseGamesUrlSigner
{
    /// <summary>The signing secret of the content CDN (constant inside the official launcher).</summary>
    public const string SigningSecret = "p99AkludZnNOkBNQd3ldWkLjGVfC";

    /// <summary>The time offset (in seconds) that the <c>key2</c> value is shifted by.</summary>
    public const int TimeOffsetSeconds = 43200;

    /// <summary>
    ///     Computes the <c>key2</c> value (the signed time).
    /// </summary>
    /// <param name="now">The current time.</param>
    public static string GetKey2(DateTimeOffset now)
    {
        long seconds = now.ToUnixTimeSeconds() + TimeOffsetSeconds;
        return ((uint)seconds).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Computes the <c>key1</c> value (the signature of the path).
    /// </summary>
    /// <param name="path">The request path, without the host and without a query string.</param>
    /// <param name="key2">The value of <c>key2</c>.</param>
    public static string GetKey1(string path, string key2)
    {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(SigningSecret + path + key2));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    ///     Appends the <c>key1</c>/<c>key2</c> parameters to an absolute URL.
    /// </summary>
    /// <param name="url">The absolute URL (without a query string).</param>
    /// <param name="now">The current time.</param>
    public static string Sign(string url, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        string path = GetPath(url);
        string key2 = GetKey2(now);
        string key1 = GetKey1(path, key2);

        char separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}key1={key1}&key2={key2}";
    }

    /// <summary>
    ///     Extracts the path of an absolute URL (the part the signature is computed over).
    /// </summary>
    /// <param name="url">The absolute URL.</param>
    public static string GetPath(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        int pathStart = url.IndexOf('/', schemeEnd + 3);
        if (pathStart < 0)
        {
            return "/";
        }

        int queryStart = url.IndexOfAny(['?', '#'], pathStart);
        return queryStart < 0 ? url[pathStart..] : url[pathStart..queryStart];
    }
}
