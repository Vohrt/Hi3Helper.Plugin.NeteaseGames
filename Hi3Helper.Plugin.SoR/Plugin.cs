using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.SoR.Management.PresetConfig;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.SoR;

[GeneratedComClass]
public partial class SeaOfRemnantsPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new SeaOfRemnantsCnPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 10, 19, 12, 00, 0, DateTimeKind.Utc);
    private static string? _appIconData;
    private static string? _posterData;

    public override void GetPluginName(out string result)
    {
        result = "Sea of Remnants Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for Sea of Remnants (遗忘之海, NetEase) in Collapse Launcher";
    }

    public override void GetPluginAuthor(out string result)
    {
        result = "Voheart";
    }

    public override unsafe void GetPluginCreationDate(out DateTime* result)
    {
        result = _pluginCreationDate.AsPointer();
    }

    public override void GetPresetConfigCount(out int count)
    {
        count = PresetConfigInstances.Length;
    }

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        SharedStatic.InstanceLogger.LogInformation("[SoR] Starting execution...");
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            presetConfig = null!;
            return;
        }

        presetConfig = PresetConfigInstances[index];
    }

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate selfUpdate)
    {
        selfUpdate = new SelfUpdate();
    }

    /// <summary>
    ///     The icon of the plugin: the application icon of the game itself (extracted from
    ///     <c>ywzh.exe</c>), base64 encoded as the API expects.
    /// </summary>
    public override void GetPluginAppIconUrl(out string result)
    {
        result = _appIconData ??= ReadEmbeddedResourceAsBase64("icon.png");
    }

    /// <summary>
    ///     The poster shown in the notification area: the key visual of the game, base64 encoded.
    /// </summary>
    public override void GetNotificationPosterUrl(out string result)
    {
        result = _posterData ??= ReadEmbeddedResourceAsBase64("poster.jpg");
    }

    private static string ReadEmbeddedResourceAsBase64(string name)
    {
        try
        {
            Assembly assembly = typeof(SeaOfRemnantsPlugin).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return string.Empty;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return Convert.ToBase64String(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SharedStatic.InstanceLogger?.LogWarning(
                "[SoR] The embedded resource '{Name}' could not be read: {Msg}", name, ex.Message);
            return string.Empty;
        }
    }
}
