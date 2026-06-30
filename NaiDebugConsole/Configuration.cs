using Dalamud.Configuration;
using System;

namespace NaiDebugConsole;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    public bool ShowWindow { get; set; } = true;

    public bool CaptureEnabled { get; set; } = true;

    public bool CaptureOnlyInDuty { get; set; } = true;

    public bool CaptureLogMessages { get; set; } = true;

    public bool IncludeFormattedLogMessages { get; set; }

    public bool CaptureActionEffects { get; set; } = true;

    public bool CaptureECommonsVfxEvents { get; set; } = true;

    public bool CaptureECommonsMapEffects { get; set; } = true;

    public bool CaptureECommonsDirectorUpdates { get; set; } = true;

    public bool CaptureECommonsObjectLifeEvents { get; set; } = true;

    public bool CaptureECommonsTethers { get; set; } = true;

    public bool CaptureEffectResultPackets { get; set; } = true;

    public bool CaptureActorControlPackets { get; set; } = true;

    public bool CapturePartySnapshots { get; set; } = true;

    public bool PullRecorderEnabled { get; set; }

    public bool PullRecorderCaptureObjectTable { get; set; } = true;

    public bool PullRecorderCaptureAddonLifecycle { get; set; } = true;

    public bool TofuFunctionWatchEnabled { get; set; }

    public bool ShareTraceCaptureOnlyFilteredAddons { get; set; }

    public bool ShareTraceAutoSnapshotConfirmationDialog { get; set; } = true;

    public bool ShareTraceHoverProbeEnabled { get; set; } = true;

    public string ShareTraceAddonFilter { get; set; } = "tofu strategy board notification selectyes selectyesno contextmenu addoncontextsub";

    public int SnapshotIntervalMs { get; set; } = 250;

    public int PullRecorderSnapshotIntervalMs { get; set; } = 250;

    public int MaxLogFileSizeMb { get; set; } = 25;

    public int MaxLogFiles { get; set; } = 8;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
