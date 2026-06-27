using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.NativeWrapper;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NaiDebugConsole;

public sealed partial class Plugin
{
    private const int MaxDebugLogEntries = 1000;
    private const int MaxAddonInspectorEvents = 500;
    private const int MaxAddonInspectorNodes = 500;
    private const int MaxAddonInspectorAtkValues = 128;
    private const int MaxShareTraceEvents = 5000;
    private const int AddonInspectorDuplicateSuppressSeconds = 3;
    private const int MaxTofuInspectorFoldersPerDataSet = 50;
    private const int MaxTofuInspectorBoardsPerDataSet = 50;
    private const int MaxTofuInspectorObjectsPerBoard = 80;
    private const int MaxTofuInspectorTextLength = 240;
    private const int MaxTofuTextObjectLength = 30;
    private const string DebugTofuTestBoardName = "Pineapple";
    private const string DebugTofuLongTextBoardName = "NDC_TEXT_31";
    private const string DebugTofuLongTextValue = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
    private const int TofuHiddenTextX = 5120;
    private const int TofuHiddenTextY = 3840;

    private static readonly AddonEvent[] AddonInspectorLifecycleEvents =
    [
        AddonEvent.PostSetup,
        AddonEvent.PostShow,
        AddonEvent.PostHide,
        AddonEvent.PostOpen,
        AddonEvent.PostClose,
        AddonEvent.PreFinalize,
    ];

    private static readonly (int X, int Y)[] TofuJobIconCoverPositions =
    [
        (5120, 3830),
        (4880, 3790),
        (4670, 3780),
        (4440, 3820),
        (4220, 3780),
        (3810, 3800),
        (4030, 3770),
    ];

    private static readonly TofuObjectType[] DebugTofuJobIcons =
    [
        TofuObjectType.Warrior,
        TofuObjectType.DarkKnight,
        TofuObjectType.Gunbreaker,
        TofuObjectType.Paladin,
        TofuObjectType.WhiteMage,
        TofuObjectType.Scholar,
        TofuObjectType.Dragoon,
        TofuObjectType.BlackMage,
    ];

    private static readonly TimeSpan ShareTraceSelectionProbeInterval = TimeSpan.FromMilliseconds(100);

    private unsafe delegate void TofuContextMenuOptionsDelegate(AgentTofuList* agent, AtkValue* values, uint valueCount, uint code);
    private unsafe delegate TofuFolderEntry* TofuCreateFolderDelegate(TofuModule* module, TofuType type, TofuFolderEntry* folder);
    private unsafe delegate TofuBoardEntry* TofuCreateBoardDelegate(TofuModule* module, TofuType type, TofuBoardEntry* board, bool notInFolder);
    private unsafe delegate TofuBoardEntry* TofuCopyBoardToFolderDelegate(TofuModule* module, TofuType type, TofuBoardEntry* board, uint folderIndex);
    private unsafe delegate bool TofuDeleteItemAndContentsDelegate(TofuModule* module, TofuType type, uint index);
    private unsafe delegate void TofuMoveItemDelegate(TofuModule* module, TofuType type, TofuItem item, uint sourceIndex, uint targetIndex);
    private unsafe delegate uint TofuWriteToUnpackedBoardDelegate(TofuBoardOverview* overview, TofuUnpackedBoard* target, int size, RaptureAtkColorDataManager* colorDataManager);
    private unsafe delegate void TofuHandleStartSharingPacketDelegate(TofuHelper* helper, ServerIpcSegment<TofuStartSharingPacket>* packet);
    private unsafe delegate void TofuHandleStopSharingPacketDelegate(TofuHelper* helper, ServerIpcSegment<TofuStopSharingPacket>* packet);
    private unsafe delegate void TofuHandleRealTimeUpdatePacketDelegate(TofuHelper* helper, ServerIpcSegment<TofuRealTimeUpdatePacket>* packet);
    private unsafe delegate void TofuHandleConfirmationPacketDelegate(TofuHelper* helper, ServerIpcSegment<TofuConfirmationPacket>* packet);
    private unsafe delegate bool TofuHandleSharePacketDelegate(TofuHelper.TofuHelperData* data, Utf8String* value, ServerIpcSegment<TofuStartSharingPacket>* packet);
    private unsafe delegate void TofuSaveBoardAndPlaySoundDelegate(TofuHelper.TofuHelperData* data, TofuStartSharingPacket* packetData, TofuPackedBoard* boardInfo, uint boardIndexInSharedFolder, uint totalBoardsInSharedFolder);
    private unsafe delegate void TofuShowSharedNotificationDelegate(TofuHelper.TofuHelperData* data, bool isNotRealTimeSharing, bool openNotif);

    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private readonly List<DebugLogEntry> debugLogEntries = [];
    private readonly List<AddonInspectorEvent> addonInspectorEvents = [];
    private readonly List<ShareTraceEvent> shareTraceEvents = [];
    private readonly Dictionary<string, DateTime> addonInspectorEventSeenAtBySignature = new(StringComparer.Ordinal);
    private AddonInspectorSnapshot? addonInspectorSnapshot;
    private TofuInspectorSnapshot? tofuInspectorSnapshot;
    private TofuInspectorSnapshot? shareTraceStartSnapshot;
    private TofuInspectorSnapshot? shareTraceEndSnapshot;
    private AddonInspectorSnapshot? shareTraceConfirmationSnapshot;
    private string? lastShareTraceSavePath;
    private bool addonInspectorLifecycleRegistered;
    private bool debugToolsDisposed;
    private bool tofuFunctionWatchEnabled;
    private bool shareTraceActive;
    private bool shareTraceEnabledTofuWatcher;
    private DateTime nextShareTraceSelectionProbeAtUtc = DateTime.MinValue;
    private DateTime shareTraceStartedAtUtc = DateTime.MinValue;
    private DateTime? shareTraceStoppedAtUtc;
    private string? lastShareTraceSelectionProbeSignature;
    private string? lastShareTraceConfirmationPrompt;

    private Hook<TofuContextMenuOptionsDelegate>? tofuContextMenuOptionsHook;
    private Hook<TofuCreateFolderDelegate>? tofuCreateFolderHook;
    private Hook<TofuCreateBoardDelegate>? tofuCreateBoardHook;
    private Hook<TofuCopyBoardToFolderDelegate>? tofuCopyBoardToFolderHook;
    private Hook<TofuDeleteItemAndContentsDelegate>? tofuDeleteItemAndContentsHook;
    private Hook<TofuMoveItemDelegate>? tofuMoveItemHook;
    private Hook<TofuWriteToUnpackedBoardDelegate>? tofuWriteToUnpackedBoardHook;
    private Hook<TofuHandleStartSharingPacketDelegate>? tofuHandleStartSharingPacketHook;
    private Hook<TofuHandleStopSharingPacketDelegate>? tofuHandleStopSharingPacketHook;
    private Hook<TofuHandleRealTimeUpdatePacketDelegate>? tofuHandleRealTimeUpdatePacketHook;
    private Hook<TofuHandleConfirmationPacketDelegate>? tofuHandleConfirmationPacketHook;
    private Hook<TofuHandleSharePacketDelegate>? tofuHandleSharePacketHook;
    private Hook<TofuSaveBoardAndPlaySoundDelegate>? tofuSaveBoardAndPlaySoundHook;
    private Hook<TofuShowSharedNotificationDelegate>? tofuShowSharedNotificationHook;

    public IReadOnlyList<DebugLogEntry> DebugLogEntries => debugLogEntries;

    public IReadOnlyList<AddonInspectorEvent> AddonInspectorEvents => addonInspectorEvents
        .OrderByDescending(entry => entry.SeenAtUtc)
        .ThenBy(entry => entry.AddonName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public AddonInspectorSnapshot? AddonInspectorSnapshot => addonInspectorSnapshot;

    public TofuInspectorSnapshot? TofuInspectorSnapshot => tofuInspectorSnapshot;

    public bool DebugTofuFunctionWatchEnabled => tofuFunctionWatchEnabled;

    public IReadOnlyList<ShareTraceEvent> ShareTraceEvents => shareTraceEvents
        .OrderByDescending(entry => entry.SeenAtUtc)
        .ToList();

    public bool ShareTraceActive => shareTraceActive;

    public DateTime? ShareTraceStartedAtUtc => shareTraceStartedAtUtc == DateTime.MinValue ? null : shareTraceStartedAtUtc;

    public DateTime? ShareTraceStoppedAtUtc => shareTraceStoppedAtUtc;

    public TofuInspectorSnapshot? ShareTraceStartSnapshot => shareTraceStartSnapshot;

    public TofuInspectorSnapshot? ShareTraceEndSnapshot => shareTraceEndSnapshot;

    public AddonInspectorSnapshot? ShareTraceConfirmationSnapshot => shareTraceConfirmationSnapshot;

    public string ShareTraceExportDirectory => Path.Combine(LogDirectory, "share-traces");

    public string? LastShareTraceSavePath => lastShareTraceSavePath;

    private void InitializeDebugTools()
    {
        RegisterAddonInspectorLifecycleListeners();
        if (Configuration.TofuFunctionWatchEnabled)
        {
            EnableTofuFunctionWatch();
        }

        AddDebugLog("Nai Debug Console loaded.");
    }

    private void DisposeDebugTools()
    {
        debugToolsDisposed = true;
        DisposeTofuFunctionWatchHooks();
        UnregisterAddonInspectorLifecycleListeners();
    }

    private void UpdateDebugTools()
    {
        CaptureShareTraceSelectionProbe();
    }

    public void ClearDebugTools()
    {
        debugLogEntries.Clear();
        addonInspectorEvents.Clear();
        addonInspectorEventSeenAtBySignature.Clear();
        addonInspectorSnapshot = null;
        tofuInspectorSnapshot = null;
        shareTraceEvents.Clear();
        shareTraceStartSnapshot = null;
        shareTraceEndSnapshot = null;
        shareTraceConfirmationSnapshot = null;
        shareTraceActive = false;
        shareTraceStoppedAtUtc = null;
        shareTraceStartedAtUtc = DateTime.MinValue;
        nextShareTraceSelectionProbeAtUtc = DateTime.MinValue;
        lastShareTraceSelectionProbeSignature = null;
        AddDebugLog("Debug console data cleared.");
    }

    public void SetTofuFunctionWatchEnabled(bool enabled)
    {
        if (enabled == tofuFunctionWatchEnabled)
        {
            return;
        }

        Configuration.TofuFunctionWatchEnabled = enabled;
        SaveConfiguration();
        if (enabled)
        {
            EnableTofuFunctionWatch();
        }
        else
        {
            DisableTofuFunctionWatch();
        }
    }

    public void SetShareTraceCaptureOnlyFilteredAddons(bool enabled)
    {
        Configuration.ShareTraceCaptureOnlyFilteredAddons = enabled;
        SaveConfiguration();
    }

    public void SetShareTraceAutoSnapshotConfirmationDialog(bool enabled)
    {
        Configuration.ShareTraceAutoSnapshotConfirmationDialog = enabled;
        SaveConfiguration();
    }

    public void SetShareTraceHoverProbeEnabled(bool enabled)
    {
        Configuration.ShareTraceHoverProbeEnabled = enabled;
        SaveConfiguration();
    }

    public void SetShareTraceAddonFilter(string filter)
    {
        Configuration.ShareTraceAddonFilter = NormalizeTraceFilter(filter);
        SaveConfiguration();
    }

    public void StartShareTrace()
    {
        if (shareTraceActive)
        {
            return;
        }

        shareTraceEvents.Clear();
        shareTraceStartSnapshot = null;
        shareTraceEndSnapshot = null;
        shareTraceConfirmationSnapshot = null;
        shareTraceStoppedAtUtc = null;
        shareTraceStartedAtUtc = DateTime.UtcNow;
        nextShareTraceSelectionProbeAtUtc = DateTime.MinValue;
        lastShareTraceSelectionProbeSignature = null;
        lastShareTraceConfirmationPrompt = null;
        shareTraceEnabledTofuWatcher = !tofuFunctionWatchEnabled;

        if (shareTraceEnabledTofuWatcher)
        {
            SetTofuFunctionWatchEnabled(true);
        }

        try
        {
            shareTraceStartSnapshot = CaptureTofuInspectorSnapshotInternal();
        }
        catch (Exception ex)
        {
            shareTraceStartSnapshot = CreateTofuInspectorErrorSnapshot(ex.Message);
        }

        shareTraceActive = true;
        AddShareTraceEvent("Trace", "Start", "Trace started. Manually open Strategy Board, choose the board/folder, click Share, then confirm Yes.", true);
        AddDebugLog("Strategy share trace started.");
    }

    public void StopShareTrace()
    {
        if (!shareTraceActive)
        {
            return;
        }

        try
        {
            shareTraceEndSnapshot = CaptureTofuInspectorSnapshotInternal();
        }
        catch (Exception ex)
        {
            shareTraceEndSnapshot = CreateTofuInspectorErrorSnapshot(ex.Message);
        }

        shareTraceStoppedAtUtc = DateTime.UtcNow;
        AddShareTraceEvent("Trace", "Stop", "Trace stopped and final Strategy Board snapshot captured.", true);
        shareTraceActive = false;

        if (shareTraceEnabledTofuWatcher)
        {
            shareTraceEnabledTofuWatcher = false;
            SetTofuFunctionWatchEnabled(false);
        }

        AddDebugLog("Strategy share trace stopped.");
    }

    public void ClearShareTrace()
    {
        var shouldDisableWatcher = shareTraceActive && shareTraceEnabledTofuWatcher;
        shareTraceEvents.Clear();
        shareTraceStartSnapshot = null;
        shareTraceEndSnapshot = null;
        shareTraceConfirmationSnapshot = null;
        lastShareTraceSavePath = null;
        shareTraceStoppedAtUtc = null;
        shareTraceStartedAtUtc = DateTime.MinValue;
        shareTraceActive = false;
        shareTraceEnabledTofuWatcher = false;
        nextShareTraceSelectionProbeAtUtc = DateTime.MinValue;
        lastShareTraceSelectionProbeSignature = null;
        lastShareTraceConfirmationPrompt = null;
        if (shouldDisableWatcher)
        {
            SetTofuFunctionWatchEnabled(false);
        }

        AddDebugLog("Strategy share trace cleared.");
    }

    public string SaveShareTraceToFile()
    {
        try
        {
            Directory.CreateDirectory(ShareTraceExportDirectory);
            var savedAtUtc = DateTime.UtcNow;
            var fileName = $"strategy-share-trace-{savedAtUtc:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(ShareTraceExportDirectory, fileName);
            var events = shareTraceEvents
                .OrderBy(entry => entry.SeenAtUtc)
                .Select(entry => new
                {
                    entry.SeenAtUtc,
                    entry.ElapsedSeconds,
                    entry.Category,
                    entry.Name,
                    entry.Details,
                    entry.IsFocused,
                })
                .ToList();

            var payload = new
            {
                schemaVersion = 1,
                kind = "strategy-share-trace-bundle",
                pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
                savedAtUtc,
                traceStartedAtUtc = ShareTraceStartedAtUtc,
                traceStoppedAtUtc = shareTraceStoppedAtUtc,
                traceActive = shareTraceActive,
                eventCount = events.Count,
                settings = new
                {
                    Configuration.ShareTraceCaptureOnlyFilteredAddons,
                    Configuration.ShareTraceAutoSnapshotConfirmationDialog,
                    Configuration.ShareTraceHoverProbeEnabled,
                    Configuration.ShareTraceAddonFilter,
                    tofuFunctionWatchEnabled,
                },
                events,
                startStrategyBoardSnapshot = ExportTofuSnapshot(shareTraceStartSnapshot),
                endStrategyBoardSnapshot = ExportTofuSnapshot(shareTraceEndSnapshot),
                confirmationDialogSnapshot = ExportAddonSnapshot(shareTraceConfirmationSnapshot),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            lastShareTraceSavePath = path;
            AddDebugLog($"Saved Strategy share trace to {Path.GetFileName(path)}.");
            return path;
        }
        catch (Exception ex)
        {
            LastError = $"Could not save share trace: {ex.Message}";
            Log.Warning(ex, "Could not save Strategy share trace.");
            return LastError;
        }
    }

    public void OpenShareTraceFolder()
    {
        Directory.CreateDirectory(ShareTraceExportDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = ShareTraceExportDirectory,
            UseShellExecute = true,
        });
    }

    public void CaptureAddonInspectorSnapshot(string addonName)
    {
        addonName = addonName.Trim();
        if (string.IsNullOrWhiteSpace(addonName))
        {
            addonInspectorSnapshot = CreateAddonInspectorErrorSnapshot(addonName, "Enter an addon name first.");
            return;
        }

        try
        {
            var addon = GameGui.GetAddonByName(addonName);
            addonInspectorSnapshot = CaptureAddonInspectorSnapshot(addonName, addon);
            AddDebugLog($"Captured addon snapshot for {addonName}.");
        }
        catch (Exception ex)
        {
            addonInspectorSnapshot = CreateAddonInspectorErrorSnapshot(addonName, ex.Message);
            Log.Debug(ex, "Could not capture addon inspector snapshot for {AddonName}.", addonName);
        }
    }

    public void ClearAddonInspector()
    {
        addonInspectorEvents.Clear();
        addonInspectorEventSeenAtBySignature.Clear();
        addonInspectorSnapshot = null;
        AddDebugLog("Addon inspector cleared.");
    }

    public void CaptureTofuInspectorSnapshot()
    {
        try
        {
            tofuInspectorSnapshot = CaptureTofuInspectorSnapshotInternal();
            AddDebugLog("Captured Strategy Board snapshot.");
        }
        catch (Exception ex)
        {
            tofuInspectorSnapshot = CreateTofuInspectorErrorSnapshot(ex.Message);
            Log.Warning(ex, "Could not capture Strategy Board snapshot.");
        }
    }

    public unsafe string CreateDebugTofuTestBoard()
    {
        try
        {
            var module = TofuModule.Instance();
            if (module is null)
            {
                return "Strategy Board module was not available yet.";
            }

            if (module->IsFull(TofuType.Saved, TofuItem.Board))
            {
                return "Saved Strategy Board list is full. Delete a saved board first.";
            }

            var board = new TofuBoardEntry
            {
                NameString = DebugTofuTestBoardName,
                Background = 0,
            };

            var objects = board.Objects;
            var objectIndex = 0;
            for (var chunkIndex = 0; chunkIndex < 8; chunkIndex++)
            {
                objects[objectIndex++] = CreateHiddenTofuTextObject($"NDC.TEST.{chunkIndex + 1}");
            }

            for (var iconIndex = 0; iconIndex < DebugTofuJobIcons.Length; iconIndex++)
            {
                objects[objectIndex++] = CreateCoveringTofuJobIcon(DebugTofuJobIcons[iconIndex], iconIndex);
            }

            board.NumberOfObjects = (byte)objectIndex;
            var created = module->CreateBoard(TofuType.Saved, &board, true);
            if (created is null)
            {
                return "The game did not create the test board.";
            }

            tofuInspectorSnapshot = CaptureTofuInspectorSnapshotInternal();
            AddDebugLog($"Created Strategy Board test board named {DebugTofuTestBoardName}.");
            return $"Created saved Strategy Board named {DebugTofuTestBoardName}.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create Strategy Board test board.");
            return $"Could not create test board: {ex.Message}";
        }
    }

    public unsafe string CreateDebugTofuLongTextBoard()
    {
        try
        {
            var module = TofuModule.Instance();
            if (module is null)
            {
                return "Strategy Board module was not available yet.";
            }

            if (module->IsFull(TofuType.Saved, TofuItem.Board))
            {
                return "Saved Strategy Board list is full. Delete a saved board first.";
            }

            var board = new TofuBoardEntry
            {
                NameString = DebugTofuLongTextBoardName,
                Background = 0,
                NumberOfObjects = 1,
            };

            var objects = board.Objects;
            objects[0] = new TofuShortObject
            {
                ObjectType = TofuObjectType.Text,
                PosX = TofuHiddenTextX,
                PosY = TofuHiddenTextY,
                Scale = 100,
                Angle = 0,
                Flags = TofuObjectFlags.IsVisible,
                TextString = DebugTofuLongTextValue,
            };

            var created = module->CreateBoard(TofuType.Saved, &board, true);
            if (created is null)
            {
                return "The game did not create the 31-character text board.";
            }

            tofuInspectorSnapshot = CaptureTofuInspectorSnapshotInternal();
            AddDebugLog($"Created Strategy Board named {DebugTofuLongTextBoardName} with requested text length {DebugTofuLongTextValue.Length:N0}.");
            return $"Created saved Strategy Board {DebugTofuLongTextBoardName} with requested text length {DebugTofuLongTextValue.Length:N0}.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create 31-character Strategy Board test board.");
            return $"Could not create 31-character text board: {ex.Message}";
        }
    }

    private void RegisterAddonInspectorLifecycleListeners()
    {
        if (addonInspectorLifecycleRegistered)
        {
            return;
        }

        foreach (var eventType in AddonInspectorLifecycleEvents)
        {
            AddonLifecycle.RegisterListener(eventType, OnAddonInspectorLifecycleEvent);
        }

        addonInspectorLifecycleRegistered = true;
    }

    private void UnregisterAddonInspectorLifecycleListeners()
    {
        if (!addonInspectorLifecycleRegistered)
        {
            return;
        }

        AddonLifecycle.UnregisterListener(OnAddonInspectorLifecycleEvent);
        addonInspectorLifecycleRegistered = false;
    }

    private void OnAddonInspectorLifecycleEvent(AddonEvent eventType, AddonArgs args)
    {
        if (debugToolsDisposed)
        {
            return;
        }

        try
        {
            var addon = args.Addon;
            var isKnown = addon.Address != 0 && !addon.IsNull;
            var now = DateTime.UtcNow;
            var signature = $"{eventType}|{args.AddonName}|{addon.Address}";
            if (addonInspectorEventSeenAtBySignature.TryGetValue(signature, out var lastSeen) &&
                now - lastSeen < TimeSpan.FromSeconds(AddonInspectorDuplicateSuppressSeconds))
            {
                return;
            }

            addonInspectorEventSeenAtBySignature[signature] = now;
            addonInspectorEvents.Add(new AddonInspectorEvent(
                now,
                eventType.ToString(),
                args.AddonName,
                addon.Address,
                isKnown && addon.IsReady,
                isKnown && addon.IsVisible));

            AddShareTraceAddonEvent(eventType, args.AddonName, addon.Address, isKnown && addon.IsReady, isKnown && addon.IsVisible);
            CaptureShareTraceConfirmationSnapshot(eventType, args.AddonName, addon);

            while (addonInspectorEvents.Count > MaxAddonInspectorEvents)
            {
                addonInspectorEvents.RemoveAt(0);
            }

            foreach (var expiredKey in addonInspectorEventSeenAtBySignature
                         .Where(pair => now - pair.Value > TimeSpan.FromMinutes(5))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                addonInspectorEventSeenAtBySignature.Remove(expiredKey);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not capture addon inspector lifecycle event.");
        }
    }

    private static AddonInspectorSnapshot CreateAddonInspectorErrorSnapshot(string addonName, string error)
    {
        return new AddonInspectorSnapshot(DateTime.UtcNow, addonName, 0, false, false, 0.0f, 0.0f, 0.0f, 0.0f, 0, [], [], error);
    }

    private unsafe AddonInspectorSnapshot CaptureAddonInspectorSnapshot(string addonName, AtkUnitBasePtr addon)
    {
        if (addon.Address == 0 || addon.IsNull)
        {
            return CreateAddonInspectorErrorSnapshot(addonName, "Addon was not found. Open the game window first, then snapshot again.");
        }

        var nodes = CaptureAddonInspectorNodes((AtkUnitBase*)addon.Address, out var nodeCount);
        return new AddonInspectorSnapshot(
            DateTime.UtcNow,
            addonName,
            addon.Address,
            addon.IsReady,
            addon.IsVisible,
            addon.X,
            addon.Y,
            addon.Width,
            addon.Height,
            nodeCount,
            nodes,
            CaptureAddonInspectorAtkValues(addon),
            null);
    }

    private static unsafe IReadOnlyList<AddonInspectorNode> CaptureAddonInspectorNodes(AtkUnitBase* unit, out int nodeCount)
    {
        var nodes = new List<AddonInspectorNode>();
        nodeCount = 0;
        if (unit is null)
        {
            return nodes;
        }

        CaptureAddonInspectorNode(unit->RootNode, nodes, ref nodeCount, 0);
        return nodes;
    }

    private static unsafe void CaptureAddonInspectorNode(AtkResNode* node, List<AddonInspectorNode> nodes, ref int nodeCount, int depth)
    {
        if (node is null || nodes.Count >= MaxAddonInspectorNodes || depth > 80)
        {
            return;
        }

        var current = node;
        while (current is not null && nodes.Count < MaxAddonInspectorNodes)
        {
            nodeCount++;
            nodes.Add(new AddonInspectorNode(
                nodes.Count,
                current->NodeId,
                current->Type.ToString(),
                current->IsVisible(),
                current->X,
                current->Y,
                current->Width,
                current->Height,
                ReadAddonInspectorNodeText(current)));

            if (current->ChildNode is not null)
            {
                CaptureAddonInspectorNode(current->ChildNode, nodes, ref nodeCount, depth + 1);
            }

            current = current->NextSiblingNode;
        }
    }

    private static unsafe string? ReadAddonInspectorNodeText(AtkResNode* node)
    {
        try
        {
            var textNode = node->GetAsAtkTextNode();
            if (textNode is null)
            {
                return null;
            }

            var text = textNode->NodeText.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<AddonInspectorValue> CaptureAddonInspectorAtkValues(AtkUnitBasePtr addon)
    {
        var values = new List<AddonInspectorValue>();
        try
        {
            var index = 0;
            foreach (var atkValue in addon.AtkValues)
            {
                if (values.Count >= MaxAddonInspectorAtkValues)
                {
                    break;
                }

                values.Add(new AddonInspectorValue(index, atkValue.ValueType.ToString(), FormatAddonInspectorAtkValue(atkValue)));
                index++;
            }
        }
        catch
        {
            values.Add(new AddonInspectorValue(0, "Error", "Could not read AtkValues."));
        }

        return values;
    }

    private static string FormatAddonInspectorAtkValue(AtkValuePtr atkValue)
    {
        try
        {
            var value = atkValue.GetValue();
            return value?.ToString() ?? "-";
        }
        catch (Exception ex)
        {
            return $"Unreadable: {ex.Message}";
        }
    }

    private unsafe void EnableTofuFunctionWatch()
    {
        DisposeTofuFunctionWatchHooks();

        var enabledHooks = 0;
        enabledHooks += TryEnableTofuFunctionHook(ref tofuContextMenuOptionsHook, (nint)AgentTofuList.MemberFunctionPointers.ContextMenuOptions, OnTofuContextMenuOptions, "AgentTofuList.ContextMenuOptions");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuCreateFolderHook, (nint)TofuModule.MemberFunctionPointers.CreateFolder, OnTofuCreateFolder, "TofuModule.CreateFolder");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuCreateBoardHook, (nint)TofuModule.MemberFunctionPointers.CreateBoard, OnTofuCreateBoard, "TofuModule.CreateBoard");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuCopyBoardToFolderHook, (nint)TofuModule.MemberFunctionPointers.CopyBoardToFolder, OnTofuCopyBoardToFolder, "TofuModule.CopyBoardToFolder");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuDeleteItemAndContentsHook, (nint)TofuModule.MemberFunctionPointers.DeleteItemAndContents, OnTofuDeleteItemAndContents, "TofuModule.DeleteItemAndContents");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuMoveItemHook, (nint)TofuModule.MemberFunctionPointers.MoveItem, OnTofuMoveItem, "TofuModule.MoveItem");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuWriteToUnpackedBoardHook, (nint)TofuBoardOverview.MemberFunctionPointers.WriteToUnpackedBoard, OnTofuWriteToUnpackedBoard, "TofuBoardOverview.WriteToUnpackedBoard");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuHandleStartSharingPacketHook, (nint)TofuHelper.MemberFunctionPointers.HandleStartSharingPacket, OnTofuHandleStartSharingPacket, "TofuHelper.HandleStartSharingPacket");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuHandleStopSharingPacketHook, (nint)TofuHelper.MemberFunctionPointers.HandleStopSharingPacket, OnTofuHandleStopSharingPacket, "TofuHelper.HandleStopSharingPacket");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuHandleRealTimeUpdatePacketHook, (nint)TofuHelper.MemberFunctionPointers.HandleRealTimeUpdatePacket, OnTofuHandleRealTimeUpdatePacket, "TofuHelper.HandleRealTimeUpdatePacket");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuHandleConfirmationPacketHook, (nint)TofuHelper.MemberFunctionPointers.HandleTofuConfirmationPacket, OnTofuHandleConfirmationPacket, "TofuHelper.HandleTofuConfirmationPacket");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuHandleSharePacketHook, (nint)TofuHelper.TofuHelperData.MemberFunctionPointers.HandleSharePacket, OnTofuHandleSharePacket, "TofuHelperData.HandleSharePacket");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuSaveBoardAndPlaySoundHook, (nint)TofuHelper.TofuHelperData.MemberFunctionPointers.SaveBoardAndPlaySound, OnTofuSaveBoardAndPlaySound, "TofuHelperData.SaveBoardAndPlaySound");
        enabledHooks += TryEnableTofuFunctionHook(ref tofuShowSharedNotificationHook, (nint)TofuHelper.TofuHelperData.MemberFunctionPointers.ShowSharedNotification, OnTofuShowSharedNotification, "TofuHelperData.ShowSharedNotification");

        tofuFunctionWatchEnabled = enabledHooks > 0;
        Configuration.TofuFunctionWatchEnabled = tofuFunctionWatchEnabled;
        SaveConfiguration();
        AddDebugLog(tofuFunctionWatchEnabled
            ? $"Tofu function watcher enabled with {enabledHooks:N0} hook(s)."
            : "Tofu function watcher could not attach any hooks.");
    }

    private int TryEnableTofuFunctionHook<T>(ref Hook<T>? hook, nint address, T detour, string name)
        where T : Delegate
    {
        if (address == nint.Zero)
        {
            AddDebugLog($"Tofu watcher missing pointer: {name}.");
            return 0;
        }

        try
        {
            hook = GameInteropProvider.HookFromAddress(address, detour);
            hook.Enable();
            AddDebugLog($"Tofu watcher attached: {name} at {FormatPointer(address)}.");
            return 1;
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            Log.Warning(ex, "Could not attach Tofu watcher hook for {FunctionName}.", name);
            AddDebugLog($"Tofu watcher failed: {name}: {ex.Message}");
            return 0;
        }
    }

    private void DisableTofuFunctionWatch()
    {
        DisposeTofuFunctionWatchHooks();
        tofuFunctionWatchEnabled = false;
        AddDebugLog("Tofu function watcher disabled.");
    }

    private void DisposeTofuFunctionWatchHooks()
    {
        tofuContextMenuOptionsHook?.Dispose();
        tofuContextMenuOptionsHook = null;
        tofuCreateFolderHook?.Dispose();
        tofuCreateFolderHook = null;
        tofuCreateBoardHook?.Dispose();
        tofuCreateBoardHook = null;
        tofuCopyBoardToFolderHook?.Dispose();
        tofuCopyBoardToFolderHook = null;
        tofuDeleteItemAndContentsHook?.Dispose();
        tofuDeleteItemAndContentsHook = null;
        tofuMoveItemHook?.Dispose();
        tofuMoveItemHook = null;
        tofuWriteToUnpackedBoardHook?.Dispose();
        tofuWriteToUnpackedBoardHook = null;
        tofuHandleStartSharingPacketHook?.Dispose();
        tofuHandleStartSharingPacketHook = null;
        tofuHandleStopSharingPacketHook?.Dispose();
        tofuHandleStopSharingPacketHook = null;
        tofuHandleRealTimeUpdatePacketHook?.Dispose();
        tofuHandleRealTimeUpdatePacketHook = null;
        tofuHandleConfirmationPacketHook?.Dispose();
        tofuHandleConfirmationPacketHook = null;
        tofuHandleSharePacketHook?.Dispose();
        tofuHandleSharePacketHook = null;
        tofuSaveBoardAndPlaySoundHook?.Dispose();
        tofuSaveBoardAndPlaySoundHook = null;
        tofuShowSharedNotificationHook?.Dispose();
        tofuShowSharedNotificationHook = null;
    }

    private unsafe void OnTofuContextMenuOptions(AgentTofuList* agent, AtkValue* values, uint valueCount, uint code)
    {
        AddTofuFunctionWatchLog("AgentTofuList.ContextMenuOptions", $"code {code}, valueCount {valueCount}, values [{FormatAtkValues(values, valueCount)}], data {FormatTofuListData(agent)}");
        tofuContextMenuOptionsHook?.Original(agent, values, valueCount, code);
    }

    private unsafe TofuFolderEntry* OnTofuCreateFolder(TofuModule* module, TofuType type, TofuFolderEntry* folder)
    {
        AddTofuFunctionWatchLog("TofuModule.CreateFolder", $"type {type}, input {FormatTofuFolder(folder)}");
        var result = tofuCreateFolderHook is null ? null : tofuCreateFolderHook.Original(module, type, folder);
        AddTofuFunctionWatchLog("TofuModule.CreateFolder", $"result {FormatTofuFolder(result)}");
        return result;
    }

    private unsafe TofuBoardEntry* OnTofuCreateBoard(TofuModule* module, TofuType type, TofuBoardEntry* board, bool notInFolder)
    {
        AddTofuFunctionWatchLog("TofuModule.CreateBoard", $"type {type}, notInFolder {notInFolder}, input {FormatTofuBoard(board)}");
        var result = tofuCreateBoardHook is null ? null : tofuCreateBoardHook.Original(module, type, board, notInFolder);
        AddTofuFunctionWatchLog("TofuModule.CreateBoard", $"result {FormatTofuBoard(result)}");
        return result;
    }

    private unsafe TofuBoardEntry* OnTofuCopyBoardToFolder(TofuModule* module, TofuType type, TofuBoardEntry* board, uint folderIndex)
    {
        AddTofuFunctionWatchLog("TofuModule.CopyBoardToFolder", $"type {type}, folderIndex {folderIndex}, input {FormatTofuBoard(board)}");
        var result = tofuCopyBoardToFolderHook is null ? null : tofuCopyBoardToFolderHook.Original(module, type, board, folderIndex);
        AddTofuFunctionWatchLog("TofuModule.CopyBoardToFolder", $"result {FormatTofuBoard(result)}");
        return result;
    }

    private unsafe bool OnTofuDeleteItemAndContents(TofuModule* module, TofuType type, uint index)
    {
        AddTofuFunctionWatchLog("TofuModule.DeleteItemAndContents", $"type {type}, index {index}");
        var result = tofuDeleteItemAndContentsHook?.Original(module, type, index) ?? false;
        AddTofuFunctionWatchLog("TofuModule.DeleteItemAndContents", $"result {result}");
        return result;
    }

    private unsafe void OnTofuMoveItem(TofuModule* module, TofuType type, TofuItem item, uint sourceIndex, uint targetIndex)
    {
        AddTofuFunctionWatchLog("TofuModule.MoveItem", $"type {type}, item {item}, sourceIndex {sourceIndex}, targetIndex {targetIndex}");
        tofuMoveItemHook?.Original(module, type, item, sourceIndex, targetIndex);
    }

    private unsafe uint OnTofuWriteToUnpackedBoard(TofuBoardOverview* overview, TofuUnpackedBoard* target, int size, RaptureAtkColorDataManager* colorDataManager)
    {
        AddTofuFunctionWatchLog("TofuBoardOverview.WriteToUnpackedBoard", $"overview {FormatTofuBoardOverview(overview)}, target {FormatPointer((nint)target)}, size {size}");
        var result = tofuWriteToUnpackedBoardHook?.Original(overview, target, size, colorDataManager) ?? 0;
        AddTofuFunctionWatchLog("TofuBoardOverview.WriteToUnpackedBoard", $"result {result}");
        return result;
    }

    private unsafe void OnTofuHandleStartSharingPacket(TofuHelper* helper, ServerIpcSegment<TofuStartSharingPacket>* packet)
    {
        AddTofuFunctionWatchLog("TofuHelper.HandleStartSharingPacket", $"helper {FormatPointer((nint)helper)}, packet {FormatPointer((nint)packet)}");
        tofuHandleStartSharingPacketHook?.Original(helper, packet);
    }

    private unsafe void OnTofuHandleStopSharingPacket(TofuHelper* helper, ServerIpcSegment<TofuStopSharingPacket>* packet)
    {
        AddTofuFunctionWatchLog("TofuHelper.HandleStopSharingPacket", $"helper {FormatPointer((nint)helper)}, packet {FormatPointer((nint)packet)}");
        tofuHandleStopSharingPacketHook?.Original(helper, packet);
    }

    private unsafe void OnTofuHandleRealTimeUpdatePacket(TofuHelper* helper, ServerIpcSegment<TofuRealTimeUpdatePacket>* packet)
    {
        AddTofuFunctionWatchLog("TofuHelper.HandleRealTimeUpdatePacket", $"helper {FormatPointer((nint)helper)}, packet {FormatPointer((nint)packet)}");
        tofuHandleRealTimeUpdatePacketHook?.Original(helper, packet);
    }

    private unsafe void OnTofuHandleConfirmationPacket(TofuHelper* helper, ServerIpcSegment<TofuConfirmationPacket>* packet)
    {
        AddTofuFunctionWatchLog("TofuHelper.HandleTofuConfirmationPacket", $"helper {FormatPointer((nint)helper)}, packet {FormatPointer((nint)packet)}");
        tofuHandleConfirmationPacketHook?.Original(helper, packet);
    }

    private unsafe bool OnTofuHandleSharePacket(TofuHelper.TofuHelperData* data, Utf8String* value, ServerIpcSegment<TofuStartSharingPacket>* packet)
    {
        AddTofuFunctionWatchLog("TofuHelperData.HandleSharePacket", $"data {FormatPointer((nint)data)}, value {FormatPointer((nint)value)}, packet {FormatPointer((nint)packet)}");
        var result = tofuHandleSharePacketHook?.Original(data, value, packet) ?? false;
        AddTofuFunctionWatchLog("TofuHelperData.HandleSharePacket", $"result {result}");
        return result;
    }

    private unsafe void OnTofuSaveBoardAndPlaySound(TofuHelper.TofuHelperData* data, TofuStartSharingPacket* packetData, TofuPackedBoard* boardInfo, uint boardIndexInSharedFolder, uint totalBoardsInSharedFolder)
    {
        AddTofuFunctionWatchLog("TofuHelperData.SaveBoardAndPlaySound", $"data {FormatPointer((nint)data)}, packetData {FormatPointer((nint)packetData)}, boardInfo {FormatPointer((nint)boardInfo)}, boardIndex {boardIndexInSharedFolder}, total {totalBoardsInSharedFolder}");
        tofuSaveBoardAndPlaySoundHook?.Original(data, packetData, boardInfo, boardIndexInSharedFolder, totalBoardsInSharedFolder);
    }

    private unsafe void OnTofuShowSharedNotification(TofuHelper.TofuHelperData* data, bool isNotRealTimeSharing, bool openNotif)
    {
        AddTofuFunctionWatchLog("TofuHelperData.ShowSharedNotification", $"isNotRealTimeSharing {isNotRealTimeSharing}, openNotif {openNotif}, sender 0x{(data is null ? 0UL : data->TofuShareData.SenderContentId):X16}, total {SafeTofuInt(data is null ? 0 : data->TofuShareData.TotalBoardsInSharedFolder)}");
        tofuShowSharedNotificationHook?.Original(data, isNotRealTimeSharing, openNotif);
    }

    private void AddTofuFunctionWatchLog(string functionName, string details)
    {
        if (!tofuFunctionWatchEnabled)
        {
            return;
        }

        AddShareTraceEvent("Tofu", functionName, details, true);
        AddDebugLog($"Tofu watcher | {functionName}: {details}");
    }

    private void CaptureShareTraceSelectionProbe()
    {
        if (!shareTraceActive || !Configuration.ShareTraceHoverProbeEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextShareTraceSelectionProbeAtUtc)
        {
            return;
        }

        nextShareTraceSelectionProbeAtUtc = now + ShareTraceSelectionProbeInterval;

        var addonState = FormatTofuListAddonProbe();
        var signature = addonState;
        if (string.Equals(signature, lastShareTraceSelectionProbeSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastShareTraceSelectionProbeSignature = signature;
        AddShareTraceEvent("Selection", "TofuList state", addonState, true);
    }

    private string FormatTofuListAddonProbe()
    {
        try
        {
            var addon = GameGui.GetAddonByName("TofuList");
            if (addon.Address == 0 || addon.IsNull)
            {
                return "TofuList addon not found";
            }

            var snapshot = CaptureAddonInspectorSnapshot("TofuList", addon);
            var visibleText = snapshot.Nodes
                .Where(node => node.IsVisible && !string.IsNullOrWhiteSpace(node.Text))
                .Take(8)
                .Select(node => $"{node.Index}:{node.NodeId}:{node.Text}")
                .ToList();
            var atkValues = snapshot.AtkValues
                .Where(value => !string.IsNullOrWhiteSpace(value.Value) && value.Value != "-")
                .Take(8)
                .Select(value => $"{value.Index}:{value.Type}:{value.Value}")
                .ToList();

            return $"ready {snapshot.IsReady}, visible {snapshot.IsVisible}, nodes {snapshot.NodeCount}, texts [{string.Join(" | ", visibleText)}], values [{string.Join(" | ", atkValues)}]";
        }
        catch (Exception ex)
        {
            return $"TofuList addon probe failed: {ex.Message}";
        }
    }

    private void AddShareTraceAddonEvent(AddonEvent eventType, string addonName, nint address, bool isReady, bool isVisible)
    {
        if (!shareTraceActive)
        {
            return;
        }

        var focused = MatchesTraceFilter(addonName, eventType.ToString());
        if (Configuration.ShareTraceCaptureOnlyFilteredAddons && !focused)
        {
            return;
        }

        AddShareTraceEvent(
            "Addon",
            addonName,
            $"{eventType} | address {FormatPointer(address)} | ready {isReady} | visible {isVisible}",
            focused);
    }

    private void CaptureShareTraceConfirmationSnapshot(AddonEvent eventType, string addonName, AtkUnitBasePtr addon)
    {
        if (!shareTraceActive ||
            !Configuration.ShareTraceAutoSnapshotConfirmationDialog ||
            !IsConfirmationAddon(addonName) ||
            eventType is not (AddonEvent.PostSetup or AddonEvent.PostShow or AddonEvent.PostOpen))
        {
            return;
        }

        try
        {
            shareTraceConfirmationSnapshot = CaptureAddonInspectorSnapshot(addonName, addon);
            AddShareTraceEvent(
                "Confirmation",
                addonName,
                $"Snapshot captured with {shareTraceConfirmationSnapshot.AtkValues.Count:N0} AtkValue(s) and {shareTraceConfirmationSnapshot.NodeCount:N0} node(s).",
                true);

            var promptAnalysis = AnalyzeStrategyShareConfirmationPrompt(shareTraceConfirmationSnapshot);
            if (promptAnalysis is not null && !string.Equals(promptAnalysis.Prompt, lastShareTraceConfirmationPrompt, StringComparison.Ordinal))
            {
                lastShareTraceConfirmationPrompt = promptAnalysis.Prompt;
                AddShareTraceEvent("Confirmation", "Prompt parse", FormatStrategySharePromptAnalysis(promptAnalysis), true);
            }
        }
        catch (Exception ex)
        {
            AddShareTraceEvent("Confirmation", addonName, $"Snapshot failed: {ex.Message}", true);
            Log.Debug(ex, "Could not capture Strategy share confirmation dialog snapshot.");
        }
    }

    private static StrategySharePromptAnalysis? AnalyzeStrategyShareConfirmationPrompt(AddonInspectorSnapshot snapshot)
    {
        var promptValue = FindStrategySharePromptValue(snapshot);
        if (promptValue is null)
        {
            return null;
        }

        var prompt = promptValue.Value;
        return new StrategySharePromptAnalysis(
            prompt,
            $"SelectYesno AtkValue[{promptValue.Index}]",
            GetStrategySharePromptTargetKind(prompt),
            TryParseQuotedPromptName(prompt));
    }

    private static AddonInspectorValue? FindStrategySharePromptValue(AddonInspectorSnapshot snapshot)
    {
        var directPrompt = snapshot.AtkValues.FirstOrDefault(value =>
            value.Index == 0 &&
            IsStrategySharePromptText(value.Value));
        if (directPrompt is not null)
        {
            return directPrompt;
        }

        return snapshot.AtkValues.FirstOrDefault(value =>
            value.Type.Contains("String", StringComparison.OrdinalIgnoreCase) &&
            IsStrategySharePromptText(value.Value));
    }

    private static bool IsStrategySharePromptText(string value)
    {
        return value.Contains("strategy board", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("share", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetStrategySharePromptTargetKind(string prompt)
    {
        return prompt.Contains("folder", StringComparison.OrdinalIgnoreCase)
            ? "folder"
            : "board";
    }

    private static string FormatStrategySharePromptAnalysis(StrategySharePromptAnalysis analysis)
    {
        var parsed = string.IsNullOrWhiteSpace(analysis.TargetName)
            ? $"{analysis.TargetKind} name was not parsed"
            : $"{analysis.TargetKind} '{analysis.TargetName}'";

        return $"Source: {analysis.Source}. Rule: read that prompt, then take the text between quote marks. Parsed: {parsed}. Raw prompt: {analysis.Prompt}";
    }

    private static string? TryParseQuotedPromptName(string prompt)
    {
        return TryExtractBetween(prompt, '\u201C', '\u201D') ??
            TryExtractBetween(prompt, '"', '"') ??
            TryExtractBetween(prompt, '\'', '\'');
    }

    private static string? TryExtractBetween(string value, char openQuote, char closeQuote)
    {
        var open = value.IndexOf(openQuote);
        var close = value.LastIndexOf(closeQuote);
        if (open < 0 || close <= open)
        {
            return null;
        }

        var result = value[(open + 1)..close].Trim();
        return result.Length == 0 ? null : result;
    }

    private sealed record StrategySharePromptAnalysis(
        string Prompt,
        string Source,
        string TargetKind,
        string? TargetName);

    private void AddShareTraceEvent(string category, string name, string details, bool isFocused)
    {
        if (!shareTraceActive && category != "Trace")
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = shareTraceStartedAtUtc == DateTime.MinValue
            ? 0.0
            : Math.Max(0.0, (now - shareTraceStartedAtUtc).TotalSeconds);

        var entry = new ShareTraceEvent(now, elapsed, category, name, details, isFocused);
        shareTraceEvents.Add(entry);
        while (shareTraceEvents.Count > MaxShareTraceEvents)
        {
            shareTraceEvents.RemoveAt(0);
        }

        WriteRecord("strategy-share-trace", new
        {
            elapsedSeconds = entry.ElapsedSeconds,
            entry.Category,
            entry.Name,
            entry.Details,
            entry.IsFocused,
        }, ignoreCaptureGate: true);
    }

    private bool MatchesTraceFilter(params string?[] values)
    {
        var terms = Configuration.ShareTraceAddonFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        return terms.Any(term => values.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static bool IsConfirmationAddon(string addonName)
    {
        var compact = addonName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Contains("SelectYes", StringComparison.OrdinalIgnoreCase) ||
            compact.Contains("SelectYesno", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTraceFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return string.Empty;
        }

        return string.Join(' ', filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static object? ExportTofuSnapshot(TofuInspectorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new
        {
            snapshot.SeenAtUtc,
            snapshot.Error,
            dataSets = snapshot.DataSets.Select(dataSet => new
            {
                dataSet.Name,
                dataSet.Total,
                dataSet.MaxCount,
                folders = dataSet.Folders.Select(folder => new
                {
                    folder.Index,
                    folder.IsValid,
                    folder.IsBoard,
                    folder.Name,
                    folder.PositionInList,
                }).ToList(),
                boards = dataSet.Boards.Select(board => new
                {
                    board.Index,
                    board.IsValid,
                    board.Name,
                    board.Folder,
                    board.PositionInList,
                    board.ServerTime,
                    board.Background,
                    board.ObjectCount,
                    objects = board.Objects.Select(obj => new
                    {
                        obj.Index,
                        obj.ObjectType,
                        obj.X,
                        obj.Y,
                        obj.Scale,
                        obj.Angle,
                        obj.Rgba,
                        obj.Visible,
                        obj.Locked,
                        obj.Flags,
                        obj.RawFlags,
                        obj.Args,
                        obj.Text,
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        };
    }

    private static object? ExportAddonSnapshot(AddonInspectorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return new
        {
            snapshot.SeenAtUtc,
            snapshot.AddonName,
            address = FormatPointer(snapshot.Address),
            snapshot.IsReady,
            snapshot.IsVisible,
            snapshot.X,
            snapshot.Y,
            snapshot.Width,
            snapshot.Height,
            snapshot.NodeCount,
            snapshot.Error,
            atkValues = snapshot.AtkValues.Select(value => new
            {
                value.Index,
                value.Type,
                value.Value,
            }).ToList(),
            nodes = snapshot.Nodes.Select(node => new
            {
                node.Index,
                node.NodeId,
                node.NodeType,
                node.IsVisible,
                node.X,
                node.Y,
                node.Width,
                node.Height,
                node.Text,
            }).ToList(),
        };
    }

    private static unsafe TofuInspectorSnapshot CaptureTofuInspectorSnapshotInternal()
    {
        var module = TofuModule.Instance();
        if (module is null)
        {
            return CreateTofuInspectorErrorSnapshot("Strategy Board module was not available yet.");
        }

        return new TofuInspectorSnapshot(
            DateTime.UtcNow,
            [
                CaptureTofuInspectorDataSet("Shared boards", module->SharedBoardData),
                CaptureTofuInspectorDataSet("Saved boards", module->SavedBoardData),
            ],
            null);
    }

    private static unsafe TofuInspectorDataSet CaptureTofuInspectorDataSet(string name, TofuData* data)
    {
        if (data is null)
        {
            return new TofuInspectorDataSet(name, 0, 0, [], []);
        }

        var folders = data->Folders;
        var boards = data->Boards;
        var maxCount = SafeTofuInt(data->MaxCount);
        var total = SafeTofuInt(data->Total);
        var folderCaptureCount = Math.Clamp(Math.Max(total, maxCount), 0, Math.Min(folders.Length, MaxTofuInspectorFoldersPerDataSet));
        var captureCount = Math.Clamp(Math.Max(total, maxCount), 0, Math.Min(boards.Length, MaxTofuInspectorBoardsPerDataSet));
        var capturedFolders = new List<TofuInspectorFolder>();
        var capturedBoards = new List<TofuInspectorBoard>();

        for (var i = 0; i < folderCaptureCount; i++)
        {
            try
            {
                var folder = folders[i];
                if (!folder.IsValid && string.IsNullOrWhiteSpace(folder.NameString))
                {
                    continue;
                }

                capturedFolders.Add(CaptureTofuInspectorFolder(i, folder));
            }
            catch (Exception ex)
            {
                capturedFolders.Add(new TofuInspectorFolder(i, false, false, $"Unreadable folder: {ex.Message}", "-"));
            }
        }

        for (var i = 0; i < captureCount; i++)
        {
            try
            {
                var board = boards[i];
                if (!board.IsValid && string.IsNullOrWhiteSpace(board.NameString) && board.NumberOfObjects == 0)
                {
                    continue;
                }

                capturedBoards.Add(CaptureTofuInspectorBoard(i, board));
            }
            catch (Exception ex)
            {
                capturedBoards.Add(new TofuInspectorBoard(i, false, $"Unreadable board: {ex.Message}", "-", "-", "-", "-", 0, []));
            }
        }

        return new TofuInspectorDataSet(name, total, maxCount, capturedFolders, capturedBoards);
    }

    private static TofuInspectorFolder CaptureTofuInspectorFolder(int index, TofuFolderEntry folder)
    {
        return new TofuInspectorFolder(
            index,
            folder.IsValid,
            folder.IsBoard,
            string.IsNullOrWhiteSpace(folder.NameString) ? "-" : TrimTofuInspectorText(folder.NameString) ?? "-",
            FormatTofuInspectorValue(folder.PositionInList));
    }

    private static unsafe TofuInspectorBoard CaptureTofuInspectorBoard(int index, TofuBoardEntry board)
    {
        var objects = board.Objects;
        var objectCount = Math.Clamp(SafeTofuInt(board.NumberOfObjects), 0, Math.Min(objects.Length, MaxTofuInspectorObjectsPerBoard));
        var capturedObjects = new List<TofuInspectorObject>();

        for (var i = 0; i < objectCount; i++)
        {
            try
            {
                var obj = objects[i];
                capturedObjects.Add(new TofuInspectorObject(
                    i,
                    obj.ObjectType.ToString(),
                    FormatTofuInspectorValue(obj.PosX),
                    FormatTofuInspectorValue(obj.PosY),
                    FormatTofuInspectorValue(obj.Scale),
                    FormatTofuInspectorValue(obj.Angle),
                    FormatTofuInspectorColor(obj.RGBA),
                    FormatTofuInspectorBool((obj.Flags & TofuObjectFlags.IsVisible) != 0),
                    FormatTofuInspectorBool((obj.Flags & TofuObjectFlags.IsLocked) != 0),
                    obj.Flags.ToString(),
                    FormatTofuInspectorRawFlags(obj.Flags),
                    $"{FormatTofuInspectorValue(obj.ArgsA)}, {FormatTofuInspectorValue(obj.ArgsB)}, {FormatTofuInspectorValue(obj.ArgsC)}",
                    TrimTofuInspectorText(obj.TextString)));
            }
            catch (Exception ex)
            {
                capturedObjects.Add(new TofuInspectorObject(i, "Unreadable", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", ex.Message));
            }
        }

        return new TofuInspectorBoard(
            index,
            board.IsValid,
            string.IsNullOrWhiteSpace(board.NameString) ? "-" : TrimTofuInspectorText(board.NameString) ?? "-",
            FormatTofuInspectorValue(board.Folder),
            FormatTofuInspectorValue(board.PositionInList),
            FormatTofuInspectorValue(board.ServerTime),
            FormatTofuInspectorValue(board.Background),
            SafeTofuInt(board.NumberOfObjects),
            capturedObjects);
    }

    private static TofuInspectorSnapshot CreateTofuInspectorErrorSnapshot(string error)
    {
        return new TofuInspectorSnapshot(DateTime.UtcNow, [], error);
    }

    private static TofuShortObject CreateHiddenTofuTextObject(string text)
    {
        return CreateSafeTofuObject(TofuObjectType.Text, TofuHiddenTextX, TofuHiddenTextY, text: text);
    }

    private static TofuShortObject CreateCoveringTofuJobIcon(TofuObjectType objectType, int iconIndex)
    {
        var position = TofuJobIconCoverPositions[iconIndex % TofuJobIconCoverPositions.Length];
        return CreateSafeTofuObject(objectType, position.X, position.Y);
    }

    private static TofuShortObject CreateSafeTofuObject(TofuObjectType objectType, int x, int y, int scale = 100, int angle = 0, TofuObjectFlags flags = TofuObjectFlags.IsVisible, int argsA = 0, int argsB = 0, int argsC = 0, string? text = null)
    {
        return new TofuShortObject
        {
            ObjectType = objectType,
            PosX = ClampToUShort(objectType == TofuObjectType.Text ? TofuHiddenTextX : x),
            PosY = ClampToUShort(objectType == TofuObjectType.Text ? TofuHiddenTextY : y),
            Scale = (byte)Math.Clamp(scale, 1, byte.MaxValue),
            Angle = ClampToUShort(angle),
            Flags = flags & TofuObjectFlags.IsVisible,
            ArgsA = ClampToUShort(argsA),
            ArgsB = ClampToUShort(argsB),
            ArgsC = ClampToUShort(argsC),
            TextString = objectType == TofuObjectType.Text ? SanitizeTofuObjectText(text) : string.Empty,
        };
    }

    private static ushort ClampToUShort(int value)
    {
        return (ushort)Math.Clamp(value, 0, ushort.MaxValue);
    }

    private static string SanitizeTofuObjectText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.ReplaceLineEndings(" ").Trim();
        return sanitized.Length <= MaxTofuTextObjectLength ? sanitized : sanitized[..MaxTofuTextObjectLength];
    }

    private void AddDebugLog(string message)
    {
        debugLogEntries.Add(new DebugLogEntry(DateTime.UtcNow, message));
        while (debugLogEntries.Count > MaxDebugLogEntries)
        {
            debugLogEntries.RemoveAt(0);
        }

        WriteRecord("debug-tools", new { message }, ignoreCaptureGate: true);
    }

    private static int SafeTofuInt<T>(T value)
        where T : IConvertible
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatTofuInspectorValue<T>(T value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-";
    }

    private static string FormatTofuInspectorColor<T>(T color)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref color, 1));
        return bytes.Length < 4
            ? Convert.ToString(color, CultureInfo.InvariantCulture) ?? "-"
            : $"{bytes[0]}, {bytes[1]}, {bytes[2]}, {bytes[3]}";
    }

    private static string FormatTofuInspectorRawFlags(TofuObjectFlags flags)
    {
        return Convert.ToUInt64(flags, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTofuInspectorBool(bool value)
    {
        return value ? "Yes" : "No";
    }

    private static string? TrimTofuInspectorText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= MaxTofuInspectorTextLength ? value : string.Concat(value.AsSpan(0, MaxTofuInspectorTextLength), "...");
    }

    private static unsafe string FormatTofuListData(AgentTofuList* agent)
    {
        if (agent is null || agent->Data is null)
        {
            return "data null";
        }

        var data = agent->Data;
        return $"savedSelected {data->SavedSelectedIndex}, sharedSelected {data->SharedSelectedIndex}, savedList {data->TotalSavedList}, sharedList {data->TotalSharedList}, sharedOpen {data->IsSharedListOpen}";
    }

    private static unsafe string FormatAtkValues(AtkValue* values, uint valueCount)
    {
        if (values is null || valueCount == 0)
        {
            return "-";
        }

        var maxCount = Math.Min(valueCount, 12);
        var parts = new List<string>((int)maxCount);
        for (var i = 0; i < maxCount; i++)
        {
            var value = values[i];
            parts.Add(value.Type switch
            {
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => $"{i}:Int={value.Int}",
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => $"{i}:UInt={value.UInt}",
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool => $"{i}:Bool={value.Byte != 0}",
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Float => $"{i}:Float={value.Float.ToString(CultureInfo.InvariantCulture)}",
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String => $"{i}:String={value.String}",
                _ => $"{i}:{value.Type}",
            });
        }

        if (valueCount > maxCount)
        {
            parts.Add($"...+{valueCount - maxCount}");
        }

        return string.Join(", ", parts);
    }

    private static unsafe string FormatTofuFolder(TofuFolderEntry* folder)
    {
        return folder is null
            ? "null"
            : $"idx {SafeTofuInt(folder->Index)}, pos {SafeTofuInt(folder->PositionInList)}, isValid {folder->IsValid}, isBoard {folder->IsBoard}, name '{FormatTofuName(folder->NameString)}'";
    }

    private static unsafe string FormatTofuBoard(TofuBoardEntry* board)
    {
        return board is null
            ? "null"
            : $"idx {SafeTofuInt(board->Index)}, pos {SafeTofuInt(board->PositionInList)}, folder {SafeTofuInt(board->Folder)}, isValid {board->IsValid}, objects {SafeTofuInt(board->NumberOfObjects)}, name '{FormatTofuName(board->NameString)}'";
    }

    private static unsafe string FormatTofuBoardOverview(TofuBoardOverview* overview)
    {
        return overview is null
            ? "null"
            : $"name '{FormatTofuName(overview->BoardName.ToString())}', background {overview->BoardBackground}";
    }

    private static string FormatTofuName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.ReplaceLineEndings(" ").Trim();
    }

    private static string FormatPointer(nint address)
    {
        return address == nint.Zero ? "0x0" : $"0x{address.ToInt64():X}";
    }
}
