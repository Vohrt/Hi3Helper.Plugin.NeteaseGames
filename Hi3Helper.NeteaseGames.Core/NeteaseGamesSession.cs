using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The login state of the NetEase account (the "session") that the game expects.
///     <para>
///         The game receives the state through the environment: it parses <c>UNISDK_LOGIN_JSON</c> and reads
///         the Jelly (JF) tokens from the sibling variables. The official launcher fills them after the
///         UniSDK login; the plugin does the same with a login state that it keeps on disk.
///     </para>
/// </summary>
public sealed class NeteaseGamesLoginState
{
    /// <summary>The name of the variable that carries the login state as JSON.</summary>
    public const string LoginJsonVariable = "UNISDK_LOGIN_JSON";

    /// <summary>The name of the variable that carries the Jelly access token.</summary>
    public const string AccessTokenVariable = "UNISDK_JF_ACCESS_TOKEN";

    /// <summary>The name of the variable that carries the Jelly refresh token.</summary>
    public const string RefreshTokenVariable = "UNISDK_JF_REFRESH_TOKEN";

    /// <summary>The name of the variable that carries the Jelly GAS JWT.</summary>
    public const string GasJwtTokenVariable = "UNISDK_JF_GAS_JWTTOKEN";

    /// <summary>The name of the variable that carries the login status.</summary>
    public const string SauthStatusVariable = "UNISDK_SAUTH_STATUS";

    /// <summary>The OAuth2 access token.</summary>
    public string? AccessToken { get; set; }

    /// <summary>The OAuth2 refresh token.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>The identity token (the "JWT").</summary>
    public string? IdToken { get; set; }

    /// <summary>The account name of the logged in user.</summary>
    public string? UserName { get; set; }

    /// <summary>The "challenge" payload of the login.</summary>
    public string? ChlSdkJson { get; set; }

    /// <summary>The anti addiction system version.</summary>
    public string? AasVersion { get; set; }

    /// <summary>The GAS token.</summary>
    public string? GasToken { get; set; }

    /// <summary>The SDK token.</summary>
    public string? SdkToken { get; set; }

    /// <summary>The id of the logged in user.</summary>
    public string? SdkUid { get; set; }

    /// <summary>The application id of the project.</summary>
    public string? Aid { get; set; }

    /// <summary>The id of the login session.</summary>
    public string? SessionId { get; set; }

    /// <summary>The id of the device.</summary>
    public string? DeviceId { get; set; }

    /// <summary>The version of the SDK.</summary>
    public string? SdkVersion { get; set; }

    /// <summary>The extra payload of the login (an opaque JSON string).</summary>
    public string? Extra { get; set; }

    /// <summary>
    ///     Whether the state carries the tokens that the game needs.
    /// </summary>
    public bool IsComplete =>
        !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

    /// <summary>
    ///     Builds the login JSON that the game parses.
    /// </summary>
    public string BuildLoginJson()
    {
        Dictionary<string, object?> root = new()
        {
            ["oauth2"] = new Dictionary<string, string?>
            {
                ["access_token"] = AccessToken,
                ["refresh_token"] = RefreshToken
            },
            ["id_token"] = IdToken,
            ["jwttoken"] = IdToken,
            ["username"] = UserName,
            ["chl_sdk_json"] = ChlSdkJson,
            ["aas_version"] = AasVersion,
            ["gas_token"] = GasToken,
            ["sdk_token"] = SdkToken,
            ["sdkuid"] = SdkUid,
            ["aid"] = Aid,
            ["sessionid"] = SessionId,
            ["deviceid"] = DeviceId,
            ["sdk_version"] = SdkVersion,
            ["extra"] = Extra
        };

        return JsonSerializer.Serialize(root);
    }

    /// <summary>
    ///     Builds the environment variables the game reads.
    /// </summary>
    public IReadOnlyDictionary<string, string?> BuildEnvironment()
    {
        Dictionary<string, string?> variables = new()
        {
            [LoginJsonVariable] = BuildLoginJson(),
            [AccessTokenVariable] = AccessToken,
            [RefreshTokenVariable] = RefreshToken,
            [GasJwtTokenVariable] = IdToken,
            [SauthStatusVariable] = "0"
        };

        return variables;
    }

    /// <summary>
    ///     Reads a login state from a JSON file.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The state or null when the file does not exist or cannot be read.</returns>
    public static NeteaseGamesLoginState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path);
            if (text.Length == 0)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;

            NeteaseGamesLoginState state = new()
            {
                AccessToken = GetString(root, "access_token"),
                RefreshToken = GetString(root, "refresh_token"),
                IdToken = GetString(root, "id_token"),
                UserName = GetString(root, "username"),
                ChlSdkJson = GetString(root, "chl_sdk_json"),
                AasVersion = GetString(root, "aas_version"),
                GasToken = GetString(root, "gas_token"),
                SdkToken = GetString(root, "sdk_token"),
                SdkUid = GetString(root, "sdkuid"),
                Aid = GetString(root, "aid"),
                SessionId = GetString(root, "sessionid"),
                DeviceId = GetString(root, "deviceid"),
                SdkVersion = GetString(root, "sdk_version"),
                Extra = GetString(root, "extra")
            };

            if (root.TryGetProperty("oauth2", out JsonElement oauth2) && oauth2.ValueKind == JsonValueKind.Object)
            {
                state.AccessToken = GetString(oauth2, "access_token") ?? state.AccessToken;
                state.RefreshToken = GetString(oauth2, "refresh_token") ?? state.RefreshToken;
            }

            return state;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Writes the login state to a JSON file.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Dictionary<string, string?> root = new()
        {
            ["access_token"] = AccessToken,
            ["refresh_token"] = RefreshToken,
            ["id_token"] = IdToken,
            ["username"] = UserName,
            ["chl_sdk_json"] = ChlSdkJson,
            ["aas_version"] = AasVersion,
            ["gas_token"] = GasToken,
            ["sdk_token"] = SdkToken,
            ["sdkuid"] = SdkUid,
            ["aid"] = Aid,
            ["sessionid"] = SessionId,
            ["deviceid"] = DeviceId,
            ["sdk_version"] = SdkVersion,
            ["extra"] = Extra
        };

        File.WriteAllText(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
///     The helper that applies the login state to the game process.
/// </summary>
public static class NeteaseGamesSession
{
    /// <summary>
    ///     The name of the file the plugin keeps the login state in.
    /// </summary>
    public const string SessionFileName = "session.json";

    /// <summary>
    ///     The UniSDK application key of the game (found in the game process).
    /// </summary>
    public const string UniSdkAppKey = "B91xFDWsRsEu41otp8JR7Clt03QCpjhI";

    /// <summary>
    ///     The UniSDK project of the game (the directory the SDK stores its data in).
    /// </summary>
    public const string UniSdkProject = "ywzh";

    /// <summary>
    ///     The name of the file that lists the UniSDK modules that may be loaded
    ///     (<c>&lt;dll&gt;&amp;&lt;version&gt;</c> per line, base64 encoded).
    /// </summary>
    public const string UniSdkWhitelistFileName = "unisdk_dll_load_whitelist";

    /// <summary>
    ///     The directory the plugin keeps its own files in.
    /// </summary>
    public static string SessionDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hi3Helper", "NeteaseGames");

    /// <summary>
    ///     The path of the file the plugin keeps the login state in.
    /// </summary>
    public static string SessionPath => Path.Combine(SessionDirectory, SessionFileName);

    /// <summary>
    ///     Applies the stored login state to the environment of this process (the game inherits it).
    /// </summary>
    /// <returns>Whether a complete login state was applied.</returns>
    public static bool ApplyStoredSession()
    {
        NeteaseGamesLoginState? state = NeteaseGamesLoginState.TryRead(SessionPath);
        if (state is null)
        {
            return false;
        }

        if (!state.IsComplete)
        {
            return false;
        }

        foreach ((string name, string? value) in state.BuildEnvironment())
        {
            if (value is null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        return true;
    }
}
