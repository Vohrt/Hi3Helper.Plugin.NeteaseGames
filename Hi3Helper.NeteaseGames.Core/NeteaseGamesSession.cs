using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The login state of the NetEase account (the "session") that the game expects.
///     <para>
///         The game receives the state through the environment: it parses <c>UNISDK_LOGIN_JSON</c> and reads
///         the Jelly (JF) tokens from the sibling variables. The official launcher fills them after the
///         UniSDK login.
///     </para>
///     <para>
///         The state is a credential: it allows acting as the account. It is therefore kept encrypted with
///         DPAPI (<see cref="DataProtectionScope.CurrentUser" />) and only decrypted right before it is
///         applied to a game process.
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
    ///     Parses a login state from its JSON representation (the token values, not the encrypted store).
    /// </summary>
    /// <param name="text">The JSON text.</param>
    /// <returns>The state, or <c>null</c> when the text is not a valid state.</returns>
    public static NeteaseGamesLoginState? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
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
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads the encrypted login state from disk.
    /// </summary>
    /// <param name="path">The path of the encrypted store.</param>
    /// <returns>The state, or <c>null</c> when the file is missing or cannot be decrypted for this user.</returns>
    public static NeteaseGamesLoginState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return TryParse(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Writes the login state to disk, encrypted with DPAPI for the current user.
    /// </summary>
    /// <param name="path">The path of the encrypted store.</param>
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

        string json = JsonSerializer.Serialize(root);
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
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
    ///     The name of the file the plugin keeps the (encrypted) login state in.
    /// </summary>
    public const string SessionFileName = "session.dat";

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
    ///     Applies the stored login state to the environment of this process (the game inherits it) and
    ///     returns a handle that restores the previous values.
    ///     <para>
    ///         The tokens are a credential, so they must not stay in the long living host process: the caller
    ///         <b>must</b> dispose the returned handle right after the game was started.
    ///     </para>
    /// </summary>
    /// <returns>A handle that restores the environment, or <c>null</c> when no state was applied.</returns>
    public static IDisposable? ApplyStoredSession()
    {
        NeteaseGamesLoginState? state = NeteaseGamesLoginState.TryRead(SessionPath);
        if (state is null || !state.IsComplete)
        {
            return null;
        }

        Dictionary<string, string?> restore = new(StringComparer.Ordinal);
        foreach ((string name, string? value) in state.BuildEnvironment())
        {
            if (value is null)
            {
                continue;
            }

            restore[name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        }

        return new EnvironmentRestore(restore);
    }

    /// <summary>
    ///     Restores the process environment variables that were set by <see cref="ApplyStoredSession" />.
    /// </summary>
    private sealed class EnvironmentRestore(Dictionary<string, string?> previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach ((string name, string? value) in previous)
            {
                try
                {
                    Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException)
                {
                    // Nothing sensible can be done here; the process is about to hand over to the game anyway.
                }
            }

            _disposed = true;
        }
    }
}
