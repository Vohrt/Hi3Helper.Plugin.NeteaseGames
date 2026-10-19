using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Hi3Helper.NeteaseGames.Core;

/// <summary>
///     The launcher's config.xml (decrypted from the LCFG container) in a strongly typed shape.
///     <para>
///         The plugin does not require the launcher to be installed: the same values are shipped as constants
///         in <see cref="NeteaseGamesEndpoints" />. When a launcher is present, its config is parsed as well so
///         that CDN migrations are picked up automatically.
///     </para>
/// </summary>
public sealed class NeteaseGamesLauncherConfig
{
    /// <summary>A single cdn_map entry: requests whose host matches are retried against every domain.</summary>
    public sealed class CdnMapEntry
    {
        public string Match { get; init; } = string.Empty;
        public List<string> Domains { get; init; } = [];
    }

    public string Name { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string GameName { get; init; } = string.Empty;
    public string GameArgs { get; init; } = string.Empty;
    public string GameDir { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string PatchListUrl { get; init; } = string.Empty;
    public string GphUrl { get; init; } = string.Empty;
    public string DrpfUrl { get; init; } = string.Empty;
    public string RemoteTimeUrl { get; init; } = string.Empty;
    public string LauncherList { get; init; } = string.Empty;
    public string MiniPackList { get; init; } = string.Empty;
    public string MiniPackBranch { get; init; } = string.Empty;
    public string MiniPackClientMode { get; init; } = string.Empty;
    public string MiniPackEngineMode { get; init; } = string.Empty;
    public string MiniPackListSuffix { get; init; } = string.Empty;
    public string NoticeList { get; init; } = string.Empty;
    public string DeviceLimitList { get; init; } = string.Empty;
    public string GrayTestList { get; init; } = string.Empty;
    public string LauncherPrefix { get; init; } = string.Empty;
    public string MiniPackPrefix { get; init; } = string.Empty;
    public string NoticePrefix { get; init; } = string.Empty;
    public List<CdnMapEntry> CdnMap { get; init; } = [];

    /// <summary>Parses the decrypted config.xml.</summary>
    public static NeteaseGamesLauncherConfig Parse(string xml)
    {
        XDocument document = XDocument.Parse(TrimTrailingPadding(xml));
        XElement root = document.Root ?? throw new InvalidDataException("Launcher config has no root element.");

        List<CdnMapEntry> cdnMap = [];
        XElement? cdnMapElement = root.Element("cdn_map");
        if (cdnMapElement is not null)
        {
            foreach (XElement item in cdnMapElement.Elements("item"))
            {
                string match = item.Attribute("match")?.Value ?? string.Empty;
                List<string> domains = item.Elements("domain")
                    .Select(d => d.Value.Trim())
                    .Where(d => d.Length > 0)
                    .ToList();
                cdnMap.Add(new CdnMapEntry { Match = match, Domains = domains });
            }
        }

        return new NeteaseGamesLauncherConfig
        {
            Name = Text(root, "name"),
            AppName = Text(root, "app_name"),
            GameName = Text(root, "game_name"),
            GameArgs = Text(root, "game_args"),
            GameDir = Text(root, "game_dir"),
            Language = Text(root, "lan_type"),
            PatchListUrl = Text(root, "patch_list_url"),
            GphUrl = Text(root, "gph_url"),
            DrpfUrl = Text(root, "drpf_url"),
            RemoteTimeUrl = Text(root, "remote_time_url"),
            LauncherList = Text(root, "launcher_list"),
            MiniPackList = Text(root, "minipack_list"),
            MiniPackBranch = Text(root, "minipack_branch"),
            MiniPackClientMode = Text(root, "minipack_client_mode"),
            MiniPackEngineMode = Text(root, "minipack_engine_mode"),
            MiniPackListSuffix = Text(root, "minipack_list_suffix"),
            NoticeList = Text(root, "notice_list"),
            DeviceLimitList = Text(root, "device_limit_list"),
            GrayTestList = Text(root, "graytest_list"),
            LauncherPrefix = Text(root, "launcher_prefix"),
            MiniPackPrefix = Text(root, "minipack_prefix"),
            NoticePrefix = Text(root, "notice_prefix"),
            CdnMap = cdnMap
        };
    }

    /// <summary>Tries to load the launcher config from disk, decrypting the LCFG container on the way.</summary>
    public static bool TryLoad(string launcherDirectory, out NeteaseGamesLauncherConfig? config)
    {
        config = null;
        try
        {
            string configDirectory = Path.Combine(launcherDirectory, "PCLauncher", "config");
            if (!Directory.Exists(configDirectory))
            {
                return false;
            }

            foreach (string file in Directory.EnumerateFiles(configDirectory))
            {
                byte[] raw = File.ReadAllBytes(file);
                if (raw.Length < 21 || !raw.AsSpan(0, 4).SequenceEqual(NeteaseGamesCrypto.LcfgMagic))
                {
                    continue;
                }

                byte[] plain = NeteaseGamesCrypto.DecryptLcfg(raw, NeteaseGamesEndpoints.LcfgKeyFallback);
                config = Parse(Encoding.UTF8.GetString(plain));
                return true;
            }
        }
        catch (Exception)
        {
            // A missing or broken launcher config must never break the plugin: the shipped constants are used.
        }

        return false;
    }

    /// <summary>Parses the launcher's dump.config (three lines: project id, app key, channel tag).</summary>
    public static bool TryParseDumpConfig(string content, out string project, out string appKey, out string channelTag)
    {
        project = string.Empty;
        appKey = string.Empty;
        channelTag = string.Empty;

        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return false;
        }

        project = lines[0];
        appKey = lines[1];
        channelTag = lines.Length > 2 ? lines[2] : string.Empty;
        return true;
    }

    /// <summary>
    ///     The LCFG container is padded to the AES block size, so the decrypted plaintext carries garbage
    ///     after the closing root element. Everything from the first control character on is dropped.
    /// </summary>
    private static string TrimTrailingPadding(string xml)
    {
        for (int i = 0; i < xml.Length; i++)
        {
            char c = xml[i];
            if (c < 0x20 && c is not ('\r' or '\n' or '\t'))
            {
                return xml[..i];
            }
        }

        return xml;
    }

    private static string Text(XElement root, string name) => root.Element(name)?.Value.Trim() ?? string.Empty;
}
