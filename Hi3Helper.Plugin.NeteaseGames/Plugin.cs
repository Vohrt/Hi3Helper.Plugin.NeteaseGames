using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NeteaseGames.Management.PresetConfig;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.NeteaseGames;

[GeneratedComClass]
public partial class NeteaseGamesPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new SeaOfRemnantsCnPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 10, 19, 12, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
    {
        result = "NetEase Games Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for NetEase published games (Sea of Remnants / 遗忘之海) in Collapse Launcher";
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
        SharedStatic.InstanceLogger.LogInformation("[NeteaseGames] Starting execution...");
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
}
