using NaiDebugConsole.Windows;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace NaiDebugConsole;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string MainCommandName = "/ndc";
    private const int LogSchemaVersion = 1;
    private const int MaxPendingRecords = 5_000;
    private const int MaxRecordsFlushedPerFrame = 1_000;
    private const int MaxEffectResultEntries = 4;
    private const int MaxMechanicCandidatesPerSnapshot = 96;
    private const int MaxMechanicCandidateLifetimes = 512;
    private const string ActorControlSignature = "E8 ?? ?? ?? ?? 0F B7 0B 83 E9 64";
    private const string EffectResultSignature = "48 8B C4 44 88 40 18 89 48 08";
    private static readonly TimeSpan MinimumSnapshotInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumPullRecorderSnapshotInterval = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct EffectResultPacket
    {
        public uint Unknown1;
        public uint RelatedActionSequence;
        public uint ActorId;
        public uint CurrentHp;
        public uint MaxHp;
        public ushort CurrentMp;
        public ushort Unknown3;
        public byte DamageShield;
        public byte EffectCount;
        public ushort Unknown6;
        public fixed byte Effects[64];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct EffectResultStatusEntry
    {
        public byte EffectIndex;
        public byte Unknown1;
        public ushort EffectId;
        public ushort StackCount;
        public ushort Unknown3;
        public float Duration;
        public uint SourceActorId;
    }

    private delegate void ProcessPacketEffectResultDelegate(uint targetId, IntPtr actionIntegrityData, byte isReplay);

    private delegate void ProcessPacketActorControlDelegate(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("NaiDebugConsole");
    private readonly ConfigWindow configWindow;
    private readonly object fileLock = new();
    private readonly Queue<object> pendingRecords = new();
    private readonly Dictionary<uint, string> actionNameCache = new();
    private readonly Dictionary<uint, string> statusNameCache = new();
    private readonly Dictionary<uint, uint> statusIconCache = new();
    private readonly Dictionary<uint, string> territoryNameCache = new();
    private readonly Dictionary<uint, MechanicCandidateLifetime> mechanicCandidateLifetimes = new();
    private Hook<ProcessPacketEffectResultDelegate>? effectResultHook;
    private Hook<ProcessPacketActorControlDelegate>? actorControlHook;
    private DateTime sessionStartedAtUtc = DateTime.UtcNow;
    private DateTime nextSnapshotAtUtc = DateTime.MinValue;
    private DateTime nextPullRecorderSnapshotAtUtc = DateTime.MinValue;
    private string? currentLogFilePath;
    private string? lastCaptureSavePath;
    private long totalEntriesWritten;
    private long currentFileEntriesWritten;
    private long droppedPendingRecords;
    private bool effectResultHookEnabled;
    private bool actorControlHookEnabled;

    private sealed class MechanicCandidateLifetime
    {
        public DateTime FirstSeenAtUtc { get; init; }

        public DateTime LastSeenAtUtc { get; set; }

        public string Name { get; set; } = string.Empty;

        public string ObjectKind { get; set; } = string.Empty;

        public uint BaseId { get; set; }

        public Vector3 FirstPosition { get; init; }

        public Vector3 LastPosition { get; set; }

        public float MaxDistanceFromFirst { get; set; }

        public int SampleCount { get; set; }

        public HashSet<string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MechanicCandidateSnapshot(int Score, object Payload);

    public Configuration Configuration { get; }

    public string LogDirectory => Path.Combine(PluginInterface.ConfigDirectory.FullName, "logs");

    public string CaptureExportDirectory => Path.Combine(LogDirectory, "captures");

    public string? CurrentLogFilePath => currentLogFilePath;

    public string CurrentLogFileDisplay => currentLogFilePath is null ? "No file yet" : Path.GetFileName(currentLogFilePath);

    public string? LastCaptureSavePath => lastCaptureSavePath;

    public long TotalEntriesWritten => totalEntriesWritten;

    public long CurrentFileEntriesWritten => currentFileEntriesWritten;

    public int PendingRecordCount
    {
        get
        {
            lock (fileLock)
            {
                return pendingRecords.Count;
            }
        }
    }

    public long DroppedPendingRecords => droppedPendingRecords;

    public string? LastError { get; private set; }

    public bool IsCaptureGateOpen => ShouldCapture();

    public bool IsPullRecorderActive => Configuration.PullRecorderEnabled && ShouldCapture();

    public bool EffectResultHookEnabled => effectResultHookEnabled;

    public bool ActorControlHookEnabled => actorControlHookEnabled;

    public unsafe Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        NormalizeConfiguration();

        configWindow = new ConfigWindow(this)
        {
            IsOpen = Configuration.ShowWindow,
        };
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(MainCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Nai Debug Console.",
        });

        ChatGui.LogMessage += OnLogMessage;
        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        ECommonsMain.ReducedLogging = true;
        ECommonsMain.Init(PluginInterface, this, Module.VfxTracking, Module.ObjectLife, Module.ObjectFunctions);
        InitializeECommonsCapture();

        try
        {
            effectResultHook = GameInteropProvider.HookFromSignature<ProcessPacketEffectResultDelegate>(
                EffectResultSignature,
                OnProcessPacketEffectResult);
            effectResultHook.Enable();
            effectResultHookEnabled = true;
        }
        catch (Exception ex)
        {
            effectResultHookEnabled = false;
            effectResultHook = null;
            Log.Warning(ex, "Nai Debug Console EffectResult hook could not be enabled.");
        }

        try
        {
            actorControlHook = GameInteropProvider.HookFromSignature<ProcessPacketActorControlDelegate>(
                ActorControlSignature,
                OnProcessPacketActorControl);
            actorControlHook.Enable();
            actorControlHookEnabled = true;
        }
        catch (Exception ex)
        {
            actorControlHookEnabled = false;
            actorControlHook = null;
            Log.Warning(ex, "Nai Debug Console ActorControl hook could not be enabled.");
        }

        InitializeDebugTools();

        StartNewLogFile("plugin-loaded");
    }

    public void Dispose()
    {
        WriteRecord("session-end", new { reason = "plugin-unloaded" }, ignoreCaptureGate: true);
        FlushPendingRecords(int.MaxValue);

        effectResultHook?.Dispose();
        actorControlHook?.Dispose();
        DisposeECommonsCapture();
        ECommonsMain.Dispose();
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Framework.Update -= OnFrameworkUpdate;
        ChatGui.LogMessage -= OnLogMessage;
        CommandManager.RemoveHandler(MainCommandName);
        DisposeDebugTools();
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
    }

    public void OpenMainUi()
    {
        configWindow.IsOpen = true;
        SetShowWindow(true);
    }

    public void SetShowWindow(bool open)
    {
        Configuration.ShowWindow = open;
        SaveConfiguration();
    }

    public void SetCaptureEnabled(bool enabled)
    {
        Configuration.CaptureEnabled = enabled;
        SaveConfiguration();
        WriteRecord("capture-toggle", new { enabled }, ignoreCaptureGate: true);
    }

    public void SetCaptureOnlyInDuty(bool enabled)
    {
        Configuration.CaptureOnlyInDuty = enabled;
        SaveConfiguration();
        WriteRecord("capture-filter-changed", new { captureOnlyInDuty = enabled }, ignoreCaptureGate: true);
    }

    public void SetCaptureLogMessages(bool enabled)
    {
        Configuration.CaptureLogMessages = enabled;
        SaveConfiguration();
    }

    public void SetIncludeFormattedLogMessages(bool enabled)
    {
        Configuration.IncludeFormattedLogMessages = enabled;
        SaveConfiguration();
    }

    public void SetCaptureActionEffects(bool enabled)
    {
        Configuration.CaptureActionEffects = enabled;
        SaveConfiguration();
    }

    public void SetCaptureECommonsVfxEvents(bool enabled)
    {
        Configuration.CaptureECommonsVfxEvents = enabled;
        SaveConfiguration();
    }

    public void SetCaptureECommonsMapEffects(bool enabled)
    {
        Configuration.CaptureECommonsMapEffects = enabled;
        SaveConfiguration();
    }

    public void SetCaptureECommonsDirectorUpdates(bool enabled)
    {
        Configuration.CaptureECommonsDirectorUpdates = enabled;
        SaveConfiguration();
    }

    public void SetCaptureECommonsObjectLifeEvents(bool enabled)
    {
        Configuration.CaptureECommonsObjectLifeEvents = enabled;
        SaveConfiguration();
    }

    public void SetCaptureECommonsTethers(bool enabled)
    {
        Configuration.CaptureECommonsTethers = enabled;
        SaveConfiguration();
    }

    public void SetCaptureEffectResultPackets(bool enabled)
    {
        Configuration.CaptureEffectResultPackets = enabled;
        SaveConfiguration();
    }

    public void SetCaptureActorControlPackets(bool enabled)
    {
        Configuration.CaptureActorControlPackets = enabled;
        SaveConfiguration();
    }

    public void SetCapturePartySnapshots(bool enabled)
    {
        Configuration.CapturePartySnapshots = enabled;
        SaveConfiguration();
    }

    public void SetPullRecorderEnabled(bool enabled)
    {
        if (enabled == Configuration.PullRecorderEnabled)
        {
            return;
        }

        Configuration.PullRecorderEnabled = enabled;
        SaveConfiguration();
        nextPullRecorderSnapshotAtUtc = DateTime.MinValue;
        mechanicCandidateLifetimes.Clear();
        if (enabled)
        {
            StartNewLogFile("pull-recorder-enabled");
        }

        WriteRecord("pull-recorder-toggle", new
        {
            enabled,
            captureGateOpen = ShouldCapture(),
            objectTable = Configuration.PullRecorderCaptureObjectTable,
            addonLifecycle = Configuration.PullRecorderCaptureAddonLifecycle,
            snapshotIntervalMs = Configuration.PullRecorderSnapshotIntervalMs,
        }, ignoreCaptureGate: true);

        if (!enabled)
        {
            FlushPendingRecords(int.MaxValue);
        }
    }

    public void SetPullRecorderCaptureObjectTable(bool enabled)
    {
        Configuration.PullRecorderCaptureObjectTable = enabled;
        SaveConfiguration();
    }

    public void SetPullRecorderCaptureAddonLifecycle(bool enabled)
    {
        Configuration.PullRecorderCaptureAddonLifecycle = enabled;
        SaveConfiguration();
    }

    public void SetSnapshotIntervalMs(int intervalMs)
    {
        Configuration.SnapshotIntervalMs = Math.Clamp(intervalMs, 100, 2_000);
        SaveConfiguration();
    }

    public void SetPullRecorderSnapshotIntervalMs(int intervalMs)
    {
        Configuration.PullRecorderSnapshotIntervalMs = Math.Clamp(intervalMs, 100, 2_000);
        SaveConfiguration();
    }

    public void SetMaxLogFileSizeMb(int sizeMb)
    {
        Configuration.MaxLogFileSizeMb = Math.Clamp(sizeMb, 5, 100);
        SaveConfiguration();
    }

    public void SetMaxLogFiles(int maxFiles)
    {
        Configuration.MaxLogFiles = Math.Clamp(maxFiles, 2, 30);
        SaveConfiguration();
        lock (fileLock)
        {
            PruneLogFilesLocked();
        }
    }

    public void StartNewLogFile(string reason = "manual")
    {
        lock (fileLock)
        {
            FlushPendingRecordsLocked(int.MaxValue);
            CreateNewLogFileLocked(reason);
        }
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true,
        });
    }

    public string SaveCurrentCaptureBundle()
    {
        WriteRecord("capture-export", new
        {
            reason = "manual-save",
            sourceLogFile = currentLogFilePath is null ? null : Path.GetFileName(currentLogFilePath),
        }, ignoreCaptureGate: true);

        try
        {
            string sourcePath;
            long copiedEntries;
            long sessionEntries;
            long droppedRecords;
            string captureDirectory;
            var savedAtUtc = DateTime.UtcNow;

            lock (fileLock)
            {
                FlushPendingRecordsLocked(int.MaxValue);
                if (currentLogFilePath is null || !File.Exists(currentLogFilePath))
                {
                    CreateNewLogFileLocked("capture-save-created-file");
                    FlushPendingRecordsLocked(int.MaxValue);
                }

                sourcePath = currentLogFilePath!;
                copiedEntries = currentFileEntriesWritten;
                sessionEntries = totalEntriesWritten;
                droppedRecords = droppedPendingRecords;

                Directory.CreateDirectory(CaptureExportDirectory);
                captureDirectory = Path.Combine(CaptureExportDirectory, $"capture-{savedAtUtc:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(captureDirectory);
                File.Copy(sourcePath, Path.Combine(captureDirectory, "records.jsonl"), overwrite: true);
            }

            var metadata = new
            {
                schemaVersion = 1,
                kind = "nai-debug-console-capture-bundle",
                pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
                savedAtUtc,
                sourceLogFile = Path.GetFileName(sourcePath),
                copiedEntries,
                sessionEntries,
                droppedRecords,
                currentTerritoryId = ClientState.TerritoryType,
                currentTerritoryName = GetTerritoryName(ClientState.TerritoryType),
                inDuty = IsInDuty(),
                inCombat = Condition[ConditionFlag.InCombat],
                hooks = new
                {
                    actionEffect = ECommonsActionEffectHookEnabled,
                    effectResult = effectResultHookEnabled,
                    actorControl = actorControlHookEnabled,
                    ecommonsVfx = ECommonsVfxHookEnabled,
                    ecommonsMapEffect = ECommonsMapEffectHookEnabled,
                    ecommonsDirectorUpdate = ECommonsDirectorUpdateHookEnabled,
                    ecommonsObjectLife = ECommonsObjectLifeHookEnabled,
                    tofuFunctionWatch = DebugTofuFunctionWatchEnabled,
                },
                settings = new
                {
                    Configuration.CaptureEnabled,
                    Configuration.CaptureOnlyInDuty,
                    Configuration.CaptureLogMessages,
                    Configuration.IncludeFormattedLogMessages,
                    Configuration.CaptureActionEffects,
                    Configuration.CaptureECommonsVfxEvents,
                    Configuration.CaptureECommonsMapEffects,
                    Configuration.CaptureECommonsDirectorUpdates,
                    Configuration.CaptureECommonsObjectLifeEvents,
                    Configuration.CaptureECommonsTethers,
                    Configuration.CaptureEffectResultPackets,
                    Configuration.CaptureActorControlPackets,
                    Configuration.CapturePartySnapshots,
                    Configuration.PullRecorderEnabled,
                    Configuration.PullRecorderCaptureObjectTable,
                    Configuration.PullRecorderCaptureAddonLifecycle,
                    Configuration.SnapshotIntervalMs,
                    Configuration.PullRecorderSnapshotIntervalMs,
                    Configuration.MaxLogFileSizeMb,
                    Configuration.MaxLogFiles,
                },
            };

            File.WriteAllText(
                Path.Combine(captureDirectory, "capture-info.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

            lastCaptureSavePath = captureDirectory;
            return captureDirectory;
        }
        catch (Exception ex)
        {
            LastError = $"Could not save capture bundle: {ex.Message}";
            Log.Warning(ex, "Could not save Nai Debug Console capture bundle.");
            return LastError;
        }
    }

    public void OpenCaptureExportFolder()
    {
        Directory.CreateDirectory(CaptureExportDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = CaptureExportDirectory,
            UseShellExecute = true,
        });
    }

    public void ClearLogFiles()
    {
        lock (fileLock)
        {
            Directory.CreateDirectory(LogDirectory);
            foreach (var file in Directory.GetFiles(LogDirectory, "nai-debug-console-*.jsonl"))
            {
                File.Delete(file);
            }

            pendingRecords.Clear();
            totalEntriesWritten = 0;
            droppedPendingRecords = 0;
            CreateNewLogFileLocked("logs-cleared");
        }
    }

    public void SaveConfiguration()
    {
        Configuration.Save();
    }

    private void OnCommand(string command, string args)
    {
        OpenMainUi();
    }

    private void NormalizeConfiguration()
    {
        if (Configuration.Version < 2)
        {
            Configuration.ShareTraceCaptureOnlyFilteredAddons = false;
            Configuration.Version = 2;
        }
        if (Configuration.Version < 3)
        {
            Configuration.CaptureEffectResultPackets = true;
            Configuration.CaptureActorControlPackets = true;
            Configuration.PullRecorderCaptureObjectTable = true;
            Configuration.PullRecorderCaptureAddonLifecycle = true;
            Configuration.PullRecorderSnapshotIntervalMs = 250;
            Configuration.Version = 3;
        }
        if (Configuration.Version < 4)
        {
            Configuration.CaptureECommonsVfxEvents = true;
            Configuration.CaptureECommonsMapEffects = true;
            Configuration.CaptureECommonsDirectorUpdates = true;
            Configuration.CaptureECommonsObjectLifeEvents = true;
            Configuration.CaptureECommonsTethers = true;
            Configuration.Version = 4;
        }

        Configuration.SnapshotIntervalMs = Math.Clamp(Configuration.SnapshotIntervalMs, 100, 2_000);
        Configuration.PullRecorderSnapshotIntervalMs = Math.Clamp(Configuration.PullRecorderSnapshotIntervalMs, 100, 2_000);
        Configuration.MaxLogFileSizeMb = Math.Clamp(Configuration.MaxLogFileSizeMb, 5, 100);
        Configuration.MaxLogFiles = Math.Clamp(Configuration.MaxLogFiles, 2, 30);
        Configuration.ShareTraceAddonFilter = string.IsNullOrWhiteSpace(Configuration.ShareTraceAddonFilter)
            ? "tofu strategy board notification selectyes selectyesno contextmenu addoncontextsub"
            : NormalizeTraceFilter(Configuration.ShareTraceAddonFilter);
        SaveConfiguration();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        FlushPendingRecords();
        UpdateDebugTools();
        UpdatePullRecorder();

        if (!Configuration.CapturePartySnapshots || !ShouldCapture())
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextSnapshotAtUtc)
        {
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Configuration.SnapshotIntervalMs);
        if (interval < MinimumSnapshotInterval)
        {
            interval = MinimumSnapshotInterval;
        }

        nextSnapshotAtUtc = now + interval;
        WriteRecord("party-snapshot", new
        {
            members = CapturePartyMembers(),
        });
    }

    private void UpdatePullRecorder()
    {
        if (!Configuration.PullRecorderEnabled || !ShouldCapture())
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextPullRecorderSnapshotAtUtc)
        {
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Configuration.PullRecorderSnapshotIntervalMs);
        if (interval < MinimumPullRecorderSnapshotInterval)
        {
            interval = MinimumPullRecorderSnapshotInterval;
        }

        nextPullRecorderSnapshotAtUtc = now + interval;
        IReadOnlyList<object> objectTable = Configuration.PullRecorderCaptureObjectTable
            ? CaptureObjectTableSnapshot()
            : Array.Empty<object>();
        IReadOnlyList<object> mechanicCandidates = Configuration.PullRecorderCaptureObjectTable
            ? CaptureMechanicCandidateSnapshots(now)
            : Array.Empty<object>();
        IReadOnlyList<object> mechanicCandidateLifetimes = Configuration.PullRecorderCaptureObjectTable
            ? CaptureMechanicCandidateLifetimeSummaries(now)
            : Array.Empty<object>();
        IReadOnlyList<object> activeVfx = Configuration.CaptureECommonsVfxEvents
            ? CaptureTrackedVfxSnapshot()
            : Array.Empty<object>();

        WriteRecord("pull-recorder-snapshot", new
        {
            partyMembers = CapturePartyMembers(),
            objectTable,
            mechanicCandidates,
            mechanicCandidateLifetimes,
            activeVfx,
            activeConditions = CaptureActiveConditions(),
        });

        PruneMechanicCandidateLifetimes(now);
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (!Configuration.CaptureLogMessages || !ShouldCapture())
        {
            return;
        }

        try
        {
            var parameters = new List<object>();
            for (var i = 0; i < message.ParameterCount; i++)
            {
                parameters.Add(CaptureLogParameter(message, i));
            }

            string? formatted = null;
            if (Configuration.IncludeFormattedLogMessages)
            {
                try
                {
                    formatted = message.FormatLogMessageForDebugging().ToString();
                }
                catch (Exception ex)
                {
                    formatted = $"Could not format message: {ex.Message}";
                }
            }

            WriteRecord("log-message", new
            {
                logMessageId = message.LogMessageId,
                source = CaptureLogEntity(message.SourceEntity),
                target = CaptureLogEntity(message.TargetEntity),
                parameterCount = message.ParameterCount,
                parameters,
                formatted,
            });
        }
        catch (Exception ex)
        {
            LastError = $"LogMessage capture failed: {ex.Message}";
            Log.Warning(ex, "Could not capture Nai Debug Console log message.");
        }
    }

    private unsafe void OnProcessPacketEffectResult(uint targetId, IntPtr actionIntegrityData, byte isReplay)
    {
        effectResultHook?.Original(targetId, actionIntegrityData, isReplay);

        try
        {
            if (Configuration.CaptureEffectResultPackets && ShouldCapture())
            {
                CaptureEffectResultPacket(targetId, actionIntegrityData, isReplay);
            }
        }
        catch (Exception ex)
        {
            LastError = $"EffectResult capture failed: {ex.Message}";
            Log.Warning(ex, "Could not capture Nai Debug Console EffectResult packet.");
        }
    }

    private void OnProcessPacketActorControl(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9)
    {
        actorControlHook?.Original(entityId, category, param1, param2, param3, param4, param5, param6, param7, param8, targetId, param9);

        try
        {
            if (Configuration.CaptureActorControlPackets && ShouldCapture())
            {
                CaptureActorControlPacket(entityId, category, param1, param2, param3, param4, param5, param6, param7, param8, targetId, param9);
            }
        }
        catch (Exception ex)
        {
            LastError = $"ActorControl capture failed: {ex.Message}";
            Log.Warning(ex, "Could not capture Nai Debug Console ActorControl packet.");
        }
    }

    private unsafe void CaptureEffectResultPacket(uint targetId, IntPtr actionIntegrityData, byte isReplay)
    {
        if (actionIntegrityData == IntPtr.Zero)
        {
            return;
        }

        var packet = (EffectResultPacket*)actionIntegrityData;
        var effectCount = Math.Min(packet->EffectCount, (byte)MaxEffectResultEntries);
        var statuses = new List<object>(effectCount);
        var effects = (EffectResultStatusEntry*)packet->Effects;
        for (var i = 0; i < effectCount; i++)
        {
            var effect = effects[i];
            if (effect.EffectId == 0)
            {
                continue;
            }

            statuses.Add(new
            {
                index = i,
                effectIndex = effect.EffectIndex,
                effectId = effect.EffectId,
                effectName = GetStatusName(effect.EffectId),
                effectIconId = GetStatusIconId(effect.EffectId),
                stackCount = effect.StackCount,
                duration = effect.Duration,
                sourceActorId = effect.SourceActorId,
                sourceObject = CaptureGameObject(ObjectTable.SearchByEntityId(effect.SourceActorId)),
            });
        }

        WriteRecord("effect-result", new
        {
            targetId,
            targetObject = CaptureGameObject(ObjectTable.SearchByEntityId(targetId)),
            relatedActionSequence = packet->RelatedActionSequence,
            actorId = packet->ActorId,
            currentHp = packet->CurrentHp,
            maxHp = packet->MaxHp,
            currentMp = packet->CurrentMp,
            damageShield = packet->DamageShield,
            effectCount = packet->EffectCount,
            isReplay,
            statuses,
        });
    }

    private void CaptureActorControlPacket(
        uint entityId,
        uint category,
        uint param1,
        uint param2,
        uint param3,
        uint param4,
        uint param5,
        uint param6,
        uint param7,
        uint param8,
        ulong targetId,
        byte param9)
    {
        var targetEntityId = targetId <= uint.MaxValue ? (uint)targetId : 0u;
        WriteRecord("actor-control", new
        {
            entityId,
            entityObject = CaptureGameObject(ObjectTable.SearchByEntityId(entityId)),
            category,
            categoryName = GetActorControlCategoryName(category),
            param1,
            param2,
            param3,
            param4,
            param5,
            param6,
            param7,
            param8,
            targetId = targetId.ToString(CultureInfo.InvariantCulture),
            targetEntityId,
            targetObject = targetEntityId == 0 ? null : CaptureGameObject(ObjectTable.SearchByEntityId(targetEntityId)),
            param9,
        });
    }

    private void WriteRecord(string kind, object payload, bool ignoreCaptureGate = false)
    {
        if (!ignoreCaptureGate && !ShouldCapture())
        {
            return;
        }

        try
        {
            var envelope = CreateEnvelope(kind, payload);
            lock (fileLock)
            {
                pendingRecords.Enqueue(envelope);
                while (pendingRecords.Count > MaxPendingRecords)
                {
                    pendingRecords.Dequeue();
                    droppedPendingRecords++;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = $"Queue failed: {ex.Message}";
            Log.Warning(ex, "Could not queue Nai Debug Console record.");
        }
    }

    private void FlushPendingRecords(int maxRecords = MaxRecordsFlushedPerFrame)
    {
        try
        {
            lock (fileLock)
            {
                FlushPendingRecordsLocked(maxRecords);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Flush failed: {ex.Message}";
            Log.Warning(ex, "Could not flush Nai Debug Console records.");
        }
    }

    private void FlushPendingRecordsLocked(int maxRecords)
    {
        if (pendingRecords.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        if (currentLogFilePath is null)
        {
            CreateNewLogFileLocked("lazy-created");
        }

        RotateLogFileIfNeededLocked();

        var limit = Math.Min(maxRecords, pendingRecords.Count);
        var lines = new List<string>(limit);
        for (var i = 0; i < limit; i++)
        {
            lines.Add(JsonSerializer.Serialize(pendingRecords.Dequeue(), JsonOptions));
        }

        File.AppendAllLines(currentLogFilePath!, lines);
        totalEntriesWritten += lines.Count;
        currentFileEntriesWritten += lines.Count;
    }

    private object CreateEnvelope(string kind, object payload)
    {
        return new
        {
            schemaVersion = LogSchemaVersion,
            pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            kind,
            utc = DateTime.UtcNow,
            sessionElapsedSeconds = (DateTime.UtcNow - sessionStartedAtUtc).TotalSeconds,
            territoryId = ClientState.TerritoryType,
            territoryName = GetTerritoryName(ClientState.TerritoryType),
            inDuty = IsInDuty(),
            inCombat = Condition[ConditionFlag.InCombat],
            payload,
        };
    }

    private void CreateNewLogFileLocked(string reason)
    {
        Directory.CreateDirectory(LogDirectory);
        sessionStartedAtUtc = DateTime.UtcNow;
        currentFileEntriesWritten = 0;
        currentLogFilePath = Path.Combine(LogDirectory, $"nai-debug-console-{sessionStartedAtUtc:yyyyMMdd-HHmmss}.jsonl");
        var json = JsonSerializer.Serialize(CreateEnvelope("session-start", new
        {
            reason,
            settings = new
            {
                Configuration.CaptureEnabled,
                Configuration.CaptureOnlyInDuty,
                Configuration.CaptureLogMessages,
                Configuration.IncludeFormattedLogMessages,
                Configuration.CaptureActionEffects,
                Configuration.CapturePartySnapshots,
                Configuration.SnapshotIntervalMs,
                Configuration.MaxLogFileSizeMb,
                Configuration.MaxLogFiles,
            },
        }), JsonOptions);
        File.AppendAllText(currentLogFilePath, json + Environment.NewLine);
        totalEntriesWritten++;
        currentFileEntriesWritten++;
        PruneLogFilesLocked();
    }

    private void RotateLogFileIfNeededLocked()
    {
        if (currentLogFilePath is null || !File.Exists(currentLogFilePath))
        {
            CreateNewLogFileLocked("missing-file");
            return;
        }

        var maxBytes = (long)Configuration.MaxLogFileSizeMb * 1024L * 1024L;
        var fileInfo = new FileInfo(currentLogFilePath);
        if (fileInfo.Length < maxBytes)
        {
            return;
        }

        CreateNewLogFileLocked("size-rotation");
    }

    private void PruneLogFilesLocked()
    {
        var files = Directory.GetFiles(LogDirectory, "nai-debug-console-*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var staleFile in files.Skip(Configuration.MaxLogFiles))
        {
            try
            {
                staleFile.Delete();
            }
            catch (Exception ex)
            {
                LastError = $"Could not prune {staleFile.Name}: {ex.Message}";
                Log.Warning(ex, "Could not prune Nai Debug Console file {File}.", staleFile.FullName);
            }
        }
    }

    private bool ShouldCapture()
    {
        if (!Configuration.CaptureEnabled || !ClientState.IsLoggedIn)
        {
            return false;
        }

        return !Configuration.CaptureOnlyInDuty || IsInDuty();
    }

    private static bool IsInDuty()
    {
        return Condition[ConditionFlag.BoundByDuty] ||
            Condition[ConditionFlag.BoundByDuty56] ||
            Condition[ConditionFlag.BoundByDuty95];
    }

    private IReadOnlyList<object> CapturePartyMembers()
    {
        var members = new List<object>();
        var partyIndex = 0;
        foreach (var member in PartyList)
        {
            var gameObject = member.GameObject;
            var shieldHp = CalculateShieldHp(gameObject, member.MaxHP);
            members.Add(new
            {
                partyIndex,
                name = member.Name.TextValue,
                contentId = member.ContentId.ToString("X16", CultureInfo.InvariantCulture),
                entityId = member.EntityId,
                classJobId = member.ClassJob.RowId,
                currentHp = member.CurrentHP,
                shieldHp,
                maxHp = member.MaxHP,
                shieldPercentage = gameObject is ICharacter character ? character.ShieldPercentage : 0,
                isDead = gameObject?.IsDead == true || (member.MaxHP > 0 && member.CurrentHP == 0),
                position = gameObject is null
                    ? null
                    : new
                    {
                        x = gameObject.Position.X,
                        y = gameObject.Position.Y,
                        z = gameObject.Position.Z,
                    },
                rotation = gameObject?.Rotation,
                statuses = CaptureStatuses(member.Statuses),
                tethers = CaptureTethers(gameObject),
            });
            partyIndex++;
        }

        return members;
    }

    private IReadOnlyList<object> CaptureObjectTableSnapshot()
    {
        var objects = new List<object>();
        foreach (var gameObject in ObjectTable)
        {
            if (gameObject is null || gameObject.EntityId == 0)
            {
                continue;
            }

            if (CaptureGameObject(gameObject, includeTethers: false) is { } snapshot)
            {
                objects.Add(snapshot);
            }
        }

        return objects;
    }

    private IReadOnlyList<object> CaptureMechanicCandidateSnapshots(DateTime now)
    {
        var candidates = new List<MechanicCandidateSnapshot>();
        foreach (var gameObject in ObjectTable)
        {
            if (gameObject is null || gameObject.EntityId == 0)
            {
                continue;
            }

            if (TryCaptureMechanicCandidate(gameObject, now, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(MaxMechanicCandidatesPerSnapshot)
            .Select(candidate => candidate.Payload)
            .ToList();
    }

    private bool TryCaptureMechanicCandidate(IGameObject gameObject, DateTime now, out MechanicCandidateSnapshot candidate)
    {
        candidate = default!;

        var objectKind = gameObject.ObjectKind.ToString();
        if (objectKind.Equals("Player", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = gameObject.Name.TextValue;
        var lowerName = name.ToLowerInvariant();
        var reasons = new List<string>();
        var score = 0;
        var isKnownMechanicName = lowerName.Contains("black hole", StringComparison.OrdinalIgnoreCase) ||
            lowerName.Contains("tower", StringComparison.OrdinalIgnoreCase) ||
            lowerName.Contains("meteor", StringComparison.OrdinalIgnoreCase) ||
            lowerName.Contains("aoe", StringComparison.OrdinalIgnoreCase);
        var isBattleNpc = gameObject is IBattleNpc;
        var isEventObject = objectKind.Contains("Event", StringComparison.OrdinalIgnoreCase);
        if (!isBattleNpc && !isEventObject && !isKnownMechanicName)
        {
            return false;
        }

        if (gameObject is IBattleNpc battleNpc)
        {
            reasons.Add("battle-npc");
            score += 30;

            if (!battleNpc.IsTargetable)
            {
                reasons.Add("untargetable-battle-npc");
                score += 18;
            }

            var battleNpcKind = battleNpc.BattleNpcKind.ToString();
            if (!string.IsNullOrWhiteSpace(battleNpcKind) &&
                !battleNpcKind.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"battle-npc-kind:{battleNpcKind}");
                score += 8;
            }
        }

        if (isEventObject)
        {
            reasons.Add("event-object");
            score += 24;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            reasons.Add("unnamed-object");
            score += 14;
        }
        else
        {
            reasons.Add("named-object");
            score += 6;
        }

        if (isKnownMechanicName)
        {
            reasons.Add("known-mechanic-name");
            score += 40;
        }

        if (gameObject.BaseId != 0)
        {
            reasons.Add("has-base-id");
            score += 4;
        }

        var distanceToLocalPlayer = CalculateDistanceToLocalPlayer(gameObject.Position);
        var nearestPartyDistance = CalculateNearestPartyDistance(gameObject.Position);
        if (distanceToLocalPlayer is <= 60.0f || nearestPartyDistance is <= 60.0f)
        {
            reasons.Add("near-player-or-party");
            score += 10;
        }

        if (score == 0)
        {
            return false;
        }

        var lifetime = UpdateMechanicCandidateLifetime(gameObject, now, objectKind, name, reasons);
        candidate = new MechanicCandidateSnapshot(score, new
        {
            seenAtUtc = now,
            score,
            candidateReasons = reasons,
            entityId = gameObject.EntityId,
            objectIndex = gameObject.ObjectIndex,
            name,
            objectKind,
            baseId = gameObject.BaseId,
            rotation = gameObject.Rotation,
            position = new
            {
                x = gameObject.Position.X,
                y = gameObject.Position.Y,
                z = gameObject.Position.Z,
            },
            distanceToLocalPlayer,
            nearestPartyDistance,
            lifetime = new
            {
                lifetime.FirstSeenAtUtc,
                lifetime.LastSeenAtUtc,
                durationSeconds = Math.Round((lifetime.LastSeenAtUtc - lifetime.FirstSeenAtUtc).TotalSeconds, 3),
                lifetime.SampleCount,
                maxDistanceFromFirst = Math.Round(lifetime.MaxDistanceFromFirst, 3),
                firstPosition = new
                {
                    x = lifetime.FirstPosition.X,
                    y = lifetime.FirstPosition.Y,
                    z = lifetime.FirstPosition.Z,
                },
                lastPosition = new
                {
                    x = lifetime.LastPosition.X,
                    y = lifetime.LastPosition.Y,
                    z = lifetime.LastPosition.Z,
                },
                reasonsSeen = lifetime.Reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToList(),
            },
            battleNpc = gameObject is IBattleNpc capturedBattleNpc
                ? new
                {
                    battleNpcKind = capturedBattleNpc.BattleNpcKind.ToString(),
                    isTargetable = capturedBattleNpc.IsTargetable,
                }
                : null,
            character = gameObject is ICharacter character
                ? new
                {
                    currentHp = character.CurrentHp,
                    maxHp = character.MaxHp,
                    shieldPercentage = character.ShieldPercentage,
                    shieldHp = CalculateShieldHp(gameObject, character.MaxHp),
                    isDead = character.IsDead || character.CurrentHp == 0,
                }
                : null,
            statuses = gameObject is IBattleChara battleChara ? CaptureStatuses(battleChara.StatusList) : [],
        });
        return true;
    }

    private MechanicCandidateLifetime UpdateMechanicCandidateLifetime(
        IGameObject gameObject,
        DateTime now,
        string objectKind,
        string name,
        IReadOnlyList<string> reasons)
    {
        if (!mechanicCandidateLifetimes.TryGetValue(gameObject.EntityId, out var lifetime))
        {
            lifetime = new MechanicCandidateLifetime
            {
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                Name = name,
                ObjectKind = objectKind,
                BaseId = gameObject.BaseId,
                FirstPosition = gameObject.Position,
                LastPosition = gameObject.Position,
                SampleCount = 0,
            };
            mechanicCandidateLifetimes[gameObject.EntityId] = lifetime;
        }

        lifetime.LastSeenAtUtc = now;
        lifetime.Name = name;
        lifetime.ObjectKind = objectKind;
        lifetime.BaseId = gameObject.BaseId;
        lifetime.LastPosition = gameObject.Position;
        lifetime.SampleCount++;
        lifetime.MaxDistanceFromFirst = Math.Max(
            lifetime.MaxDistanceFromFirst,
            Vector3.Distance(lifetime.FirstPosition, gameObject.Position));

        foreach (var reason in reasons)
        {
            lifetime.Reasons.Add(reason);
        }

        return lifetime;
    }

    private IReadOnlyList<object> CaptureMechanicCandidateLifetimeSummaries(DateTime now)
    {
        return mechanicCandidateLifetimes
            .OrderByDescending(pair => pair.Value.LastSeenAtUtc)
            .Take(MaxMechanicCandidateLifetimes)
            .Select(pair => new
            {
                entityId = pair.Key,
                pair.Value.Name,
                pair.Value.ObjectKind,
                pair.Value.BaseId,
                pair.Value.FirstSeenAtUtc,
                pair.Value.LastSeenAtUtc,
                durationSeconds = Math.Round((pair.Value.LastSeenAtUtc - pair.Value.FirstSeenAtUtc).TotalSeconds, 3),
                staleSeconds = Math.Round((now - pair.Value.LastSeenAtUtc).TotalSeconds, 3),
                pair.Value.SampleCount,
                maxDistanceFromFirst = Math.Round(pair.Value.MaxDistanceFromFirst, 3),
                firstPosition = new
                {
                    x = pair.Value.FirstPosition.X,
                    y = pair.Value.FirstPosition.Y,
                    z = pair.Value.FirstPosition.Z,
                },
                lastPosition = new
                {
                    x = pair.Value.LastPosition.X,
                    y = pair.Value.LastPosition.Y,
                    z = pair.Value.LastPosition.Z,
                },
                reasonsSeen = pair.Value.Reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .ToList();
    }

    private void PruneMechanicCandidateLifetimes(DateTime now)
    {
        foreach (var staleEntityId in mechanicCandidateLifetimes
                     .Where(pair => now - pair.Value.LastSeenAtUtc > TimeSpan.FromMinutes(5))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            mechanicCandidateLifetimes.Remove(staleEntityId);
        }

        if (mechanicCandidateLifetimes.Count <= MaxMechanicCandidateLifetimes)
        {
            return;
        }

        foreach (var oldEntityId in mechanicCandidateLifetimes
                     .OrderBy(pair => pair.Value.LastSeenAtUtc)
                     .Take(mechanicCandidateLifetimes.Count - MaxMechanicCandidateLifetimes)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            mechanicCandidateLifetimes.Remove(oldEntityId);
        }
    }

    private static float? CalculateDistanceToLocalPlayer(Vector3 position)
    {
        var localPlayer = ObjectTable.LocalPlayer;
        return localPlayer is null ? null : Vector3.Distance(localPlayer.Position, position);
    }

    private static float? CalculateNearestPartyDistance(Vector3 position)
    {
        float? nearestDistance = null;
        foreach (var member in PartyList)
        {
            var gameObject = member.GameObject;
            if (gameObject is null)
            {
                continue;
            }

            var distance = Vector3.Distance(gameObject.Position, position);
            if (nearestDistance is null || distance < nearestDistance.Value)
            {
                nearestDistance = distance;
            }
        }

        return nearestDistance;
    }

    private static IReadOnlyList<string> CaptureActiveConditions()
    {
        var active = new List<string>();
        foreach (var flag in Enum.GetValues<ConditionFlag>())
        {
            try
            {
                if (Condition[flag])
                {
                    active.Add(flag.ToString());
                }
            }
            catch
            {
                // Some enum values can be invalid for indexing on older clients. Ignore them for debug snapshots.
            }
        }

        return active;
    }

    private IReadOnlyList<object> CaptureStatuses(IEnumerable<Dalamud.Game.ClientState.Statuses.IStatus> statuses)
    {
        return statuses
            .Where(status => status.StatusId != 0)
            .Select(status => new
            {
                id = status.StatusId,
                name = GetStatusName(status.StatusId),
                iconId = GetStatusIconId(status.StatusId),
                param = status.Param,
                remainingTime = status.RemainingTime,
                sourceId = status.SourceId,
                source = CaptureStatusSource(status.SourceId),
            })
            .ToList();
    }

    private static object? CaptureStatusSource(uint sourceId)
    {
        if (sourceId == 0)
        {
            return null;
        }

        var sourceObject = ObjectTable.SearchByEntityId(sourceId);
        if (sourceObject is null)
        {
            return new
            {
                entityId = sourceId,
                name = $"Entity {sourceId:X8}",
                found = false,
            };
        }

        return new
        {
            entityId = sourceObject.EntityId,
            objectIndex = sourceObject.ObjectIndex,
            name = sourceObject.Name.TextValue,
            objectKind = sourceObject.ObjectKind.ToString(),
            found = true,
        };
    }

    private object? CaptureGameObject(IGameObject? gameObject, bool includeTethers = false)
    {
        if (gameObject is null)
        {
            return null;
        }

        return new
        {
            entityId = gameObject.EntityId,
            objectIndex = gameObject.ObjectIndex,
            name = gameObject.Name.TextValue,
            objectKind = gameObject.ObjectKind.ToString(),
            baseId = gameObject.BaseId,
            rotation = gameObject.Rotation,
            position = new
            {
                x = gameObject.Position.X,
                y = gameObject.Position.Y,
                z = gameObject.Position.Z,
            },
            battleNpc = gameObject is IBattleNpc battleNpc
                ? new
                {
                    battleNpcKind = battleNpc.BattleNpcKind.ToString(),
                    isTargetable = battleNpc.IsTargetable,
                }
                : null,
            character = gameObject is ICharacter character
                ? new
                {
                    currentHp = character.CurrentHp,
                    maxHp = character.MaxHp,
                    shieldPercentage = character.ShieldPercentage,
                    shieldHp = CalculateShieldHp(gameObject, character.MaxHp),
                    isDead = character.IsDead || character.CurrentHp == 0,
                }
                : null,
            statuses = gameObject is IBattleChara battleChara ? CaptureStatuses(battleChara.StatusList) : [],
            tethers = includeTethers ? CaptureTethers(gameObject) : [],
        };
    }

    private static object? CaptureLogEntity(ILogMessageEntity? entity)
    {
        if (entity is null)
        {
            return null;
        }

        return new
        {
            name = entity.Name.ToString(),
            isPlayer = entity.IsPlayer,
            homeWorldId = entity.HomeWorldId,
            objStrId = entity.ObjStrId,
        };
    }

    private static object CaptureLogParameter(ILogMessage message, int index)
    {
        if (message.TryGetIntParameter(index, out var intValue))
        {
            return new
            {
                index,
                type = "int",
                value = intValue,
            };
        }

        if (message.TryGetStringParameter(index, out var stringValue))
        {
            return new
            {
                index,
                type = "string",
                value = stringValue.ToString(),
            };
        }

        return new
        {
            index,
            type = "unknown",
            value = string.Empty,
        };
    }

    private string GetActionName(uint actionId)
    {
        if (actionId == 0)
        {
            return "None";
        }

        if (actionNameCache.TryGetValue(actionId, out var cachedName))
        {
            return cachedName;
        }

        var name = $"Action {actionId}";
        try
        {
            var action = DataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
            var sheetName = action?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                name = sheetName;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load action name for {ActionId}.", actionId);
        }

        actionNameCache[actionId] = name;
        return name;
    }

    private string GetStatusName(uint statusId)
    {
        if (statusNameCache.TryGetValue(statusId, out var cachedName))
        {
            return cachedName;
        }

        var name = $"Status {statusId}";
        try
        {
            var status = DataManager.GetExcelSheet<Status>()?.GetRowOrDefault(statusId);
            var sheetName = status?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                name = sheetName;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load status name for {StatusId}.", statusId);
        }

        statusNameCache[statusId] = name;
        return name;
    }

    private uint GetStatusIconId(uint statusId)
    {
        if (statusIconCache.TryGetValue(statusId, out var cachedIconId))
        {
            return cachedIconId;
        }

        var iconId = 0u;
        try
        {
            var status = DataManager.GetExcelSheet<Status>()?.GetRowOrDefault(statusId);
            iconId = status?.Icon ?? 0u;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load status icon for {StatusId}.", statusId);
        }

        statusIconCache[statusId] = iconId;
        return iconId;
    }

    private static string GetActorControlCategoryName(uint category)
    {
        return category switch
        {
            0x0006 => "Death",
            0x0014 => "GainEffect",
            0x0015 => "LoseEffect",
            0x0016 => "UpdateEffect",
            0x0022 => "TargetIcon",
            0x0604 => "HoT",
            0x0605 => "DoT",
            _ => $"Category {category:X}",
        };
    }

    private string GetTerritoryName(uint territoryId)
    {
        if (territoryId == 0)
        {
            return "Unknown territory";
        }

        if (territoryNameCache.TryGetValue(territoryId, out var cachedName))
        {
            return cachedName;
        }

        var name = $"Territory {territoryId}";
        try
        {
            var territory = DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
            var sheetName = territory?.PlaceName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                name = sheetName;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not load territory name for {TerritoryId}.", territoryId);
        }

        territoryNameCache[territoryId] = name;
        return name;
    }

    private static uint CalculateShieldHp(IGameObject? gameObject, uint maxHp)
    {
        if (maxHp == 0 || gameObject is not ICharacter character)
        {
            return 0;
        }

        var shieldPercentage = Math.Clamp((double)character.ShieldPercentage, 0.0, 100.0);
        return (uint)Math.Round(maxHp * shieldPercentage / 100.0, MidpointRounding.AwayFromZero);
    }
}
