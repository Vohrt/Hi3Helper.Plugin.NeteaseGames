using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The NetEase "hot domain server" (HD server) that hands out the addresses of a service domain.
///     <para>
///         The official launcher resolves a domain through two steps:
///         <list type="number">
///             <item>
///                 <c>https://dns.update.netease.com/hdserver</c> returns a base64 encoded JSON document with the
///                 edge addresses of the mainland and the oversea network.
///             </item>
///             <item>
///                 Every address is then asked for the concrete domain:
///                 <c>GET https://&lt;address&gt;/v2/?domain=&lt;domain&gt;</c> with the headers
///                 <c>AUTH-PROJECT</c> and <c>AUTH-TOKEN</c>. The answer is
///                 <c>{"addrs":[...],"domain":"...","ttl":300}</c>.
///             </item>
///         </list>
///         The two headers are the only authentication of the service - the token is a short, rotating value
///         that the client receives from the service itself.
///     </para>
/// </summary>
public static class NeteaseGamesDispatch
{
    /// <summary>The name of the header that carries the project id.</summary>
    public const string ProjectHeader = "AUTH-PROJECT";

    /// <summary>The name of the header that carries the access token.</summary>
    public const string TokenHeader = "AUTH-TOKEN";

    /// <summary>
    ///     A dedicated client for the edges: they are addressed by their IP, so their certificate does not
    ///     match the host name (the official client accepts them as well).
    /// </summary>
    private static readonly HttpClient EdgeClient = CreateEdgeClient();

    private static HttpClient CreateEdgeClient()
    {
        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    ///     Resolves the addresses of a service domain.
    /// </summary>
    /// <param name="client">The HTTP client to use.</param>
    /// <param name="domain">The domain to resolve (e.g. <c>h74.gdl.netease.com</c>).</param>
    /// <param name="project">The project id (e.g. <c>mpay</c>).</param>
    /// <param name="token">The access token of the project.</param>
    /// <param name="cancellationToken">Cancel token.</param>
    /// <returns>The addresses of the domain (empty when the service does not know it).</returns>
    public static async Task<IReadOnlyList<string>> ResolveDomainAsync(HttpClient client, string domain,
        string project, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(domain);

        IReadOnlyList<string> edges = await GetEdgeAddressesAsync(client, cancellationToken).ConfigureAwait(false);
        foreach (string edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<string>? addresses = await QueryEdgeAsync(client, edge, domain, project, token, cancellationToken)
                .ConfigureAwait(false);
            if (addresses is { Count: > 0 })
            {
                return addresses;
            }
        }

        return [];
    }

    /// <summary>
    ///     Reads the edge addresses of the mainland and the oversea network.
    /// </summary>
    /// <param name="client">The HTTP client to use.</param>
    /// <param name="cancellationToken">Cancel token.</param>
    public static async Task<IReadOnlyList<string>> GetEdgeAddressesAsync(HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        byte[] raw = await client.GetByteArrayAsync(new Uri(NeteaseGamesEndpoints.HdServerUrl), cancellationToken)
            .ConfigureAwait(false);
        string json = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()));

        List<string> addresses = [];
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } address)
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    /// <summary>
    ///     Asks a single edge for the addresses of a domain.
    /// </summary>
    /// <param name="client">The HTTP client to use.</param>
    /// <param name="edge">The address of the edge (e.g. <c>1.95.233.187</c>).</param>
    /// <param name="domain">The domain to resolve.</param>
    /// <param name="project">The project id.</param>
    /// <param name="token">The access token of the project.</param>
    /// <param name="cancellationToken">Cancel token.</param>
    public static async Task<IReadOnlyList<string>> QueryEdgeAsync(HttpClient client, string edge, string domain,
        string project, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(edge);

        Uri uri = new($"https://{edge}/v2/?domain={Uri.EscapeDataString(domain)}");
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation(ProjectHeader, project);
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        HttpResponseMessage response;
        try
        {
            response = await EdgeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The edges are addressed by their IP, so the certificate of the service does not match the
            // host name - the official client accepts them as well.
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(payload);

            List<string> addresses = [];
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("addrs", out JsonElement array) &&
                    array.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in array.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } address)
                        {
                            addresses.Add(address);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return [];
            }

            return addresses;
        }
    }
}
