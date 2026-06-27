using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace NaiDebugConsole.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 EnabledColor = new(0.25f, 1.0f, 0.35f, 1.0f);
    private static readonly Vector4 WarningColor = new(1.0f, 0.25f, 0.25f, 1.0f);
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private string addonInspectorName = string.Empty;
    private string addonInspectorEventFilter = string.Empty;
    private string debugTextFilter = string.Empty;
    private string shareTraceAddonFilter = string.Empty;
    private string shareTraceEventFilter = string.Empty;
    private string tofuCreateResult = string.Empty;
    private bool addonInspectorHideCommonNoise = true;
    private bool shareTraceFocusedOnly = true;

    public ConfigWindow(Plugin plugin) : base("Nai Debug Console###NaiDebugConsoleConfig")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        shareTraceAddonFilter = configuration.ShareTraceAddonFilter ?? string.Empty;
        Size = new Vector2(920, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        plugin.SetShowWindow(false);
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##NaiDebugConsoleTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Logger"))
        {
            DrawCaptureControls();
            ImGui.Separator();
            DrawStorageControls();
            ImGui.Separator();
            DrawStatus();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Strategy Boards"))
        {
            DrawStrategyBoardTools();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Share Trace"))
        {
            DrawShareTraceTools();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Addons"))
        {
            DrawAddonInspector();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Log"))
        {
            DrawInternalLog();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawCaptureControls()
    {
        var captureEnabled = configuration.CaptureEnabled;
        if (ImGui.Checkbox("Capture enabled", ref captureEnabled))
        {
            plugin.SetCaptureEnabled(captureEnabled);
        }

        var captureOnlyInDuty = configuration.CaptureOnlyInDuty;
        if (ImGui.Checkbox("Capture only while bound by duty", ref captureOnlyInDuty))
        {
            plugin.SetCaptureOnlyInDuty(captureOnlyInDuty);
        }

        var captureLogMessages = configuration.CaptureLogMessages;
        if (ImGui.Checkbox("Capture structured log messages", ref captureLogMessages))
        {
            plugin.SetCaptureLogMessages(captureLogMessages);
        }

        var includeFormatted = configuration.IncludeFormattedLogMessages;
        if (ImGui.Checkbox("Also format log message text", ref includeFormatted))
        {
            plugin.SetIncludeFormattedLogMessages(includeFormatted);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Dalamud warns this debug formatter can have side effects, including sound effects. Keep this off unless we need the rendered line text.");
        }

        var captureActionEffects = configuration.CaptureActionEffects;
        if (ImGui.Checkbox("Capture action effects", ref captureActionEffects))
        {
            plugin.SetCaptureActionEffects(captureActionEffects);
        }

        var capturePartySnapshots = configuration.CapturePartySnapshots;
        if (ImGui.Checkbox("Capture party HP/status snapshots", ref capturePartySnapshots))
        {
            plugin.SetCapturePartySnapshots(capturePartySnapshots);
        }

        var snapshotInterval = configuration.SnapshotIntervalMs;
        if (ImGui.SliderInt("Snapshot interval (ms)", ref snapshotInterval, 100, 2_000))
        {
            plugin.SetSnapshotIntervalMs(snapshotInterval);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("How often to record party HP, shields, and statuses while capture is active.");
        }
    }

    private void DrawStorageControls()
    {
        var maxSize = configuration.MaxLogFileSizeMb;
        if (ImGui.SliderInt("Max file size (MB)", ref maxSize, 5, 100))
        {
            plugin.SetMaxLogFileSizeMb(maxSize);
        }

        var maxFiles = configuration.MaxLogFiles;
        if (ImGui.SliderInt("Files to keep", ref maxFiles, 2, 30))
        {
            plugin.SetMaxLogFiles(maxFiles);
        }

        if (ImGui.Button("Start new file"))
        {
            plugin.StartNewLogFile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Open log folder"))
        {
            plugin.OpenLogFolder();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear logs") && ImGui.GetIO().KeyCtrl)
        {
            plugin.ClearLogFiles();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Ctrl+click to delete stored Nai Debug Console files.");
        }
    }

    private void DrawStatus()
    {
        ImGui.TextUnformatted($"Log folder: {plugin.LogDirectory}");
        ImGui.TextUnformatted($"Current file: {plugin.CurrentLogFileDisplay}");
        ImGui.TextUnformatted($"Current file entries: {plugin.CurrentFileEntriesWritten:N0}");
        ImGui.TextUnformatted($"Session entries: {plugin.TotalEntriesWritten:N0}");
        ImGui.TextUnformatted($"Pending records: {plugin.PendingRecordCount:N0}");
        ImGui.TextUnformatted($"Dropped records: {plugin.DroppedPendingRecords:N0}");

        if (plugin.IsCaptureGateOpen)
        {
            ImGui.TextColored(EnabledColor, "Capture gate is open.");
        }
        else
        {
            ImGui.TextDisabled("Capture gate is closed by settings, login state, or duty filter.");
        }

        if (!string.IsNullOrWhiteSpace(plugin.LastError))
        {
            ImGui.TextColored(WarningColor, $"Last error: {plugin.LastError}");
        }

        ImGui.Spacing();
        ImGui.TextColored(WarningColor, "This stores raw local debug data and can include character names, combat events, HP values, statuses, and territory information.");
        ImGui.TextWrapped("Leave formatted log text off unless we specifically need it. The IDs, entities, parameters, action effects, and snapshots are the important comparison data.");
    }

    private void DrawStrategyBoardTools()
    {
        ImGui.TextColored(EnabledColor, "Strategy Board inspector");
        ImGui.TextDisabled("Reads the client's shared/saved Strategy Board lists and logs Tofu function calls when the watcher is enabled.");

        var watchTofuFunctions = plugin.DebugTofuFunctionWatchEnabled;
        if (ImGui.Checkbox("Watch Tofu functions", ref watchTofuFunctions))
        {
            plugin.SetTofuFunctionWatchEnabled(watchTofuFunctions);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Turn this on right before testing Strategy Board share, save, notification, or context-menu actions.");
        }

        if (ImGui.Button("Snapshot strategy boards"))
        {
            plugin.CaptureTofuInspectorSnapshot();
        }

        ImGui.SameLine();
        if (ImGui.Button("Create test board"))
        {
            tofuCreateResult = plugin.CreateDebugTofuTestBoard();
        }

        ImGui.SameLine();
        DrawDebugFilter();

        if (!string.IsNullOrWhiteSpace(tofuCreateResult))
        {
            ImGui.TextDisabled(tofuCreateResult);
        }

        var snapshot = plugin.TofuInspectorSnapshot;
        if (snapshot is null)
        {
            ImGui.TextDisabled("No Strategy Board snapshot yet.");
            return;
        }

        ImGui.TextDisabled($"Latest snapshot: {snapshot.SeenAtUtc:HH:mm:ss} UTC");
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            ImGui.TextColored(WarningColor, snapshot.Error);
            return;
        }

        foreach (var dataSet in snapshot.DataSets)
        {
            DrawTofuDataSet(dataSet);
        }
    }

    private void DrawShareTraceTools()
    {
        ImGui.TextColored(EnabledColor, "Strategy share trace");
        ImGui.TextDisabled("Start this right before manually sharing a Strategy Board. Stop it after the Yes/No prompt is answered.");

        if (plugin.ShareTraceActive)
        {
            ImGui.TextColored(EnabledColor, "Trace is recording.");
        }
        else if (plugin.ShareTraceStoppedAtUtc is { } stoppedAt)
        {
            ImGui.TextDisabled($"Trace stopped at {stoppedAt:HH:mm:ss} UTC.");
        }
        else
        {
            ImGui.TextDisabled("Trace is idle.");
        }

        if (!plugin.ShareTraceActive && ImGui.Button("Start share trace"))
        {
            plugin.StartShareTrace();
        }

        ImGui.SameLine();
        if (plugin.ShareTraceActive && ImGui.Button("Stop share trace"))
        {
            plugin.StopShareTrace();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear trace"))
        {
            plugin.ClearShareTrace();
        }

        ImGui.Spacing();
        var filteredOnly = configuration.ShareTraceCaptureOnlyFilteredAddons;
        if (ImGui.Checkbox("Only capture matching addon lifecycle events", ref filteredOnly))
        {
            plugin.SetShareTraceCaptureOnlyFilteredAddons(filteredOnly);
        }

        var snapshotDialog = configuration.ShareTraceAutoSnapshotConfirmationDialog;
        if (ImGui.Checkbox("Snapshot confirmation dialog automatically", ref snapshotDialog))
        {
            plugin.SetShareTraceAutoSnapshotConfirmationDialog(snapshotDialog);
        }

        ImGui.SetNextItemWidth(MathF.Max(320.0f, ImGui.GetContentRegionAvail().X * 0.55f));
        if (ImGui.InputText("Addon capture terms##ShareTraceAddonFilter", ref shareTraceAddonFilter, 256))
        {
            plugin.SetShareTraceAddonFilter(shareTraceAddonFilter);
        }

        ImGui.SameLine();
        if (ImGui.Button("Share preset"))
        {
            shareTraceAddonFilter = "tofu strategy board notification selectyes selectyesno contextmenu addoncontextsub";
            plugin.SetShareTraceAddonFilter(shareTraceAddonFilter);
        }

        ImGui.SetNextItemWidth(MathF.Max(240.0f, ImGui.GetContentRegionAvail().X * 0.35f));
        ImGui.InputText("Trace event filter##ShareTraceEventFilter", ref shareTraceEventFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Focused only", ref shareTraceFocusedOnly);

        DrawShareTraceEvents();

        ImGui.Spacing();
        DrawTraceSnapshotSummary("Start Strategy Board snapshot", plugin.ShareTraceStartSnapshot);
        DrawTraceSnapshotSummary("End Strategy Board snapshot", plugin.ShareTraceEndSnapshot);
        DrawShareTraceConfirmationSnapshot();
    }

    private void DrawShareTraceEvents()
    {
        var allEvents = plugin.ShareTraceEvents;
        var events = allEvents
            .Where(MatchesShareTraceEvent)
            .Take(200)
            .OrderBy(entry => entry.SeenAtUtc)
            .ToList();

        ImGui.TextDisabled($"Showing {events.Count:N0} of {allEvents.Count:N0} trace events.");
        if (events.Count == 0)
        {
            return;
        }

        if (!ImGui.BeginTable("##ShareTraceEvents", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("UTC", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableSetupColumn("+s", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1.35f);
        ImGui.TableSetupColumn("Focus", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 3.0f);
        ImGui.TableHeadersRow();

        foreach (var entry in events)
        {
            ImGui.TableNextRow();
            DrawTableText(0, entry.SeenAtUtc.ToString("HH:mm:ss"));
            DrawTableText(1, entry.ElapsedSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            DrawTableText(2, entry.Category);
            DrawTableText(3, entry.Name, wrap: true);
            DrawTableText(4, entry.IsFocused ? "yes" : "no", disabled: !entry.IsFocused);
            DrawTableText(5, entry.Details, wrap: true);
        }

        ImGui.EndTable();
    }

    private void DrawTraceSnapshotSummary(string label, TofuInspectorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            ImGui.TextDisabled($"{label}: none");
            return;
        }

        if (!ImGui.TreeNode($"{label} ({snapshot.SeenAtUtc:HH:mm:ss} UTC)###{label}"))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            ImGui.TextColored(WarningColor, snapshot.Error);
            ImGui.TreePop();
            return;
        }

        foreach (var dataSet in snapshot.DataSets)
        {
            var objectCount = dataSet.Boards.Sum(board => board.ObjectCount);
            ImGui.TextUnformatted($"{dataSet.Name}: {dataSet.Boards.Count:N0} captured board(s), total {dataSet.Total:N0}, max {dataSet.MaxCount:N0}, objects {objectCount:N0}");
        }

        ImGui.TreePop();
    }

    private void DrawShareTraceConfirmationSnapshot()
    {
        var snapshot = plugin.ShareTraceConfirmationSnapshot;
        if (snapshot is null)
        {
            ImGui.TextDisabled("Confirmation dialog snapshot: none");
            return;
        }

        if (!ImGui.TreeNode($"Confirmation dialog snapshot ({snapshot.AddonName}, {snapshot.SeenAtUtc:HH:mm:ss} UTC)###ShareTraceConfirmationSnapshot"))
        {
            return;
        }

        ImGui.TextDisabled($"{FormatAddress(snapshot.Address)} | Ready {FormatBool(snapshot.IsReady)} | Visible {FormatBool(snapshot.IsVisible)} | AtkValues {snapshot.AtkValues.Count:N0} | Nodes {snapshot.NodeCount:N0}");
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            ImGui.TextColored(WarningColor, snapshot.Error);
            ImGui.TreePop();
            return;
        }

        DrawAddonValues(snapshot);
        DrawAddonNodes(snapshot);
        ImGui.TreePop();
    }

    private void DrawTofuDataSet(TofuInspectorDataSet dataSet)
    {
        var boards = dataSet.Boards.Where(MatchesTofuBoard).ToList();
        if (!ImGui.TreeNode($"{dataSet.Name} ({boards.Count:N0}/{dataSet.Boards.Count:N0}, total {dataSet.Total:N0}, max {dataSet.MaxCount:N0})###Tofu{dataSet.Name}"))
        {
            return;
        }

        if (boards.Count == 0)
        {
            ImGui.TextDisabled("No boards match the current filter.");
            ImGui.TreePop();
            return;
        }

        if (ImGui.BeginTable($"##TofuBoards{dataSet.Name}", 8, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableSetupColumn("Valid", ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableSetupColumn("Objects", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableSetupColumn("Background", ImGuiTableColumnFlags.WidthStretch, 0.75f);
            ImGui.TableSetupColumn("Server time", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableHeadersRow();

            foreach (var board in boards)
            {
                ImGui.TableNextRow();
                DrawTableText(0, board.Index.ToString(CultureInfo.InvariantCulture));
                DrawTableText(1, FormatBool(board.IsValid));
                DrawTableText(2, board.Name, wrap: true);
                DrawTableText(3, board.Folder);
                DrawTableText(4, board.PositionInList);
                DrawTableText(5, board.ObjectCount.ToString(CultureInfo.InvariantCulture));
                DrawTableText(6, board.Background);
                DrawTableText(7, board.ServerTime);
            }

            ImGui.EndTable();
        }

        foreach (var board in boards)
        {
            DrawTofuObjects(dataSet.Name, board);
        }

        ImGui.TreePop();
    }

    private void DrawTofuObjects(string dataSetName, TofuInspectorBoard board)
    {
        var objects = board.Objects.Where(MatchesTofuObject).ToList();
        if (!ImGui.TreeNode($"Board {board.Index}: {board.Name} objects ({objects.Count:N0}/{board.Objects.Count:N0})###Tofu{dataSetName}{board.Index}Objects"))
        {
            return;
        }

        if (objects.Count == 0)
        {
            ImGui.TextDisabled("No objects match the current filter.");
            ImGui.TreePop();
            return;
        }

        if (ImGui.BeginTable($"##TofuObjects{dataSetName}{board.Index}", 13, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var header in new[] { "#", "Type", "X", "Y", "Scale", "Angle", "RGBA", "Visible", "Locked", "Flags", "Flag #", "Args", "Text" })
            {
                ImGui.TableSetupColumn(header);
            }

            ImGui.TableHeadersRow();
            foreach (var obj in objects)
            {
                ImGui.TableNextRow();
                DrawTableText(0, obj.Index.ToString(CultureInfo.InvariantCulture));
                DrawTableText(1, obj.ObjectType);
                DrawTableText(2, obj.X);
                DrawTableText(3, obj.Y);
                DrawTableText(4, obj.Scale);
                DrawTableText(5, obj.Angle);
                DrawTableText(6, obj.Rgba);
                DrawTableText(7, obj.Visible);
                DrawTableText(8, obj.Locked);
                DrawTableText(9, obj.Flags, wrap: true);
                DrawTableText(10, obj.RawFlags);
                DrawTableText(11, obj.Args, wrap: true);
                DrawTableText(12, string.IsNullOrWhiteSpace(obj.Text) ? "-" : obj.Text, wrap: true, disabled: string.IsNullOrWhiteSpace(obj.Text));
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void DrawAddonInspector()
    {
        ImGui.TextColored(EnabledColor, "Addon inspector");
        ImGui.TextDisabled("Open the game window you want to inspect, then snapshot its addon name.");

        ImGui.SetNextItemWidth(MathF.Max(220.0f, ImGui.GetContentRegionAvail().X * 0.35f));
        ImGui.InputText("Addon name##AddonInspectorName", ref addonInspectorName, 128);
        ImGui.SameLine();
        if (ImGui.Button("Snapshot addon"))
        {
            plugin.CaptureAddonInspectorSnapshot(addonInspectorName);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear addon inspector"))
        {
            plugin.ClearAddonInspector();
        }

        ImGui.SetNextItemWidth(MathF.Max(220.0f, ImGui.GetContentRegionAvail().X * 0.35f));
        ImGui.InputText("Addon event filter##AddonInspectorEventFilter", ref addonInspectorEventFilter, 128);
        ImGui.SameLine();
        if (ImGui.Button("board/strat"))
        {
            addonInspectorEventFilter = "board strat strategy tofu notification";
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear filter"))
        {
            addonInspectorEventFilter = string.Empty;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Hide common UI noise", ref addonInspectorHideCommonNoise);
        DrawDebugFilter();

        DrawAddonInspectorEvents();
        DrawAddonInspectorSnapshot();
    }

    private void DrawAddonInspectorEvents()
    {
        var allEvents = plugin.AddonInspectorEvents;
        var events = allEvents.Where(MatchesAddonEvent).Take(100).ToList();
        if (allEvents.Count == 0)
        {
            ImGui.TextDisabled("No addon lifecycle events captured yet. Open a game UI window.");
            return;
        }

        ImGui.TextDisabled($"Showing {events.Count:N0} of {allEvents.Count:N0} latest addon lifecycle events.");
        if (!ImGui.BeginTable("##AddonInspectorEvents", 7, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        foreach (var header in new[] { "Use", "UTC", "Event", "Addon", "Address", "Ready", "Visible" })
        {
            ImGui.TableSetupColumn(header);
        }

        ImGui.TableHeadersRow();
        foreach (var entry in events)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.SmallButton($"Use##Addon{entry.SeenAtUtc.Ticks}{entry.Address}"))
            {
                addonInspectorName = entry.AddonName;
            }

            DrawTableText(1, entry.SeenAtUtc.ToString("HH:mm:ss"));
            DrawTableText(2, entry.EventName);
            DrawTableText(3, entry.AddonName);
            DrawTableText(4, FormatAddress(entry.Address));
            DrawTableText(5, FormatBool(entry.IsReady));
            DrawTableText(6, FormatBool(entry.IsVisible));
        }

        ImGui.EndTable();
    }

    private void DrawAddonInspectorSnapshot()
    {
        var snapshot = plugin.AddonInspectorSnapshot;
        if (snapshot is null)
        {
            ImGui.TextDisabled("No addon snapshot yet.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(EnabledColor, "Latest addon snapshot");
        ImGui.TextDisabled($"{snapshot.AddonName} | {snapshot.SeenAtUtc:HH:mm:ss} UTC | {FormatAddress(snapshot.Address)} | Ready {FormatBool(snapshot.IsReady)} | Visible {FormatBool(snapshot.IsVisible)} | Pos {snapshot.X:N0}, {snapshot.Y:N0} | Size {snapshot.Width:N0} x {snapshot.Height:N0}");

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            ImGui.TextColored(WarningColor, snapshot.Error);
            return;
        }

        DrawAddonValues(snapshot);
        DrawAddonNodes(snapshot);
    }

    private void DrawAddonValues(AddonInspectorSnapshot snapshot)
    {
        var values = snapshot.AtkValues.Where(MatchesAddonValue).ToList();
        if (!ImGui.TreeNode($"AtkValues ({values.Count:N0}/{snapshot.AtkValues.Count:N0})###AddonInspectorAtkValues"))
        {
            return;
        }

        if (ImGui.BeginTable("##AddonInspectorAtkValuesTable", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var header in new[] { "#", "Type", "Value" })
            {
                ImGui.TableSetupColumn(header);
            }

            ImGui.TableHeadersRow();
            foreach (var value in values)
            {
                ImGui.TableNextRow();
                DrawTableText(0, value.Index.ToString(CultureInfo.InvariantCulture));
                DrawTableText(1, value.Type);
                DrawTableText(2, value.Value, wrap: true);
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void DrawAddonNodes(AddonInspectorSnapshot snapshot)
    {
        var nodes = snapshot.Nodes.Where(MatchesAddonNode).ToList();
        if (!ImGui.TreeNode($"Nodes ({nodes.Count:N0}/{snapshot.NodeCount:N0})###AddonInspectorNodes"))
        {
            return;
        }

        if (ImGui.BeginTable("##AddonInspectorNodesTable", 7, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            foreach (var header in new[] { "#", "ID", "Type", "Visible", "X/Y", "Size", "Text" })
            {
                ImGui.TableSetupColumn(header);
            }

            ImGui.TableHeadersRow();
            foreach (var node in nodes)
            {
                ImGui.TableNextRow();
                DrawTableText(0, node.Index.ToString(CultureInfo.InvariantCulture));
                DrawTableText(1, node.NodeId.ToString(CultureInfo.InvariantCulture));
                DrawTableText(2, node.NodeType);
                DrawTableText(3, FormatBool(node.IsVisible));
                DrawTableText(4, $"{node.X:N0}, {node.Y:N0}");
                DrawTableText(5, $"{node.Width:N0} x {node.Height:N0}");
                DrawTableText(6, string.IsNullOrWhiteSpace(node.Text) ? "-" : node.Text, wrap: true, disabled: string.IsNullOrWhiteSpace(node.Text));
            }

            ImGui.EndTable();
        }

        ImGui.TreePop();
    }

    private void DrawInternalLog()
    {
        DrawDebugFilter();
        ImGui.SameLine();
        if (ImGui.Button("Clear console data"))
        {
            plugin.ClearDebugTools();
        }

        var entries = plugin.DebugLogEntries.Where(MatchesLogEntry).ToList();
        ImGui.TextDisabled($"{entries.Count:N0}/{plugin.DebugLogEntries.Count:N0} debug rows visible.");
        if (!ImGui.BeginTable("##NaiDebugConsoleLog", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("UTC", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 3.0f);
        ImGui.TableHeadersRow();

        foreach (var entry in entries.OrderBy(entry => entry.SeenAtUtc))
        {
            ImGui.TableNextRow();
            DrawTableText(0, entry.SeenAtUtc.ToString("HH:mm:ss"));
            DrawTableText(1, entry.Message, wrap: true);
        }

        ImGui.EndTable();
    }

    private void DrawDebugFilter()
    {
        ImGui.SetNextItemWidth(MathF.Max(180.0f, ImGui.GetContentRegionAvail().X * 0.30f));
        ImGui.InputText("Filter##DebugTextFilter", ref debugTextFilter, 128);
        if (!string.IsNullOrWhiteSpace(debugTextFilter))
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##DebugTextFilterClear"))
            {
                debugTextFilter = string.Empty;
            }
        }
    }

    private bool MatchesTofuBoard(TofuInspectorBoard board)
    {
        return MatchesFilter(board.Index.ToString(CultureInfo.InvariantCulture), board.Name, board.Folder, board.PositionInList, board.Background, board.ServerTime, string.Join(" ", board.Objects.Select(obj => obj.Text)));
    }

    private bool MatchesTofuObject(TofuInspectorObject obj)
    {
        return MatchesFilter(obj.Index.ToString(CultureInfo.InvariantCulture), obj.ObjectType, obj.X, obj.Y, obj.Scale, obj.Angle, obj.Rgba, obj.Visible, obj.Locked, obj.Flags, obj.RawFlags, obj.Args, obj.Text);
    }

    private bool MatchesAddonEvent(AddonInspectorEvent entry)
    {
        if (addonInspectorHideCommonNoise && IsCommonAddonNoise(entry.AddonName))
        {
            return false;
        }

        if (!MatchesSpaceSeparatedFilter(addonInspectorEventFilter, entry.EventName, entry.AddonName, FormatAddress(entry.Address)))
        {
            return false;
        }

        return MatchesFilter(entry.EventName, entry.AddonName, FormatAddress(entry.Address), FormatBool(entry.IsReady), FormatBool(entry.IsVisible));
    }

    private bool MatchesAddonValue(AddonInspectorValue value)
    {
        return MatchesFilter(value.Index.ToString(CultureInfo.InvariantCulture), value.Type, value.Value);
    }

    private bool MatchesAddonNode(AddonInspectorNode node)
    {
        return MatchesFilter(node.Index.ToString(CultureInfo.InvariantCulture), node.NodeId.ToString(CultureInfo.InvariantCulture), node.NodeType, FormatBool(node.IsVisible), node.Text);
    }

    private bool MatchesLogEntry(DebugLogEntry entry)
    {
        return MatchesFilter(entry.SeenAtUtc.ToString("HH:mm:ss"), entry.Message);
    }

    private bool MatchesShareTraceEvent(ShareTraceEvent entry)
    {
        if (shareTraceFocusedOnly && !entry.IsFocused)
        {
            return false;
        }

        return MatchesSpaceSeparatedFilter(
            shareTraceEventFilter,
            entry.SeenAtUtc.ToString("HH:mm:ss"),
            entry.ElapsedSeconds.ToString("0.000", CultureInfo.InvariantCulture),
            entry.Category,
            entry.Name,
            entry.Details,
            entry.IsFocused ? "focused" : "regular");
    }

    private bool MatchesFilter(params string?[] values)
    {
        return MatchesSpaceSeparatedFilter(debugTextFilter, values);
    }

    private static bool MatchesSpaceSeparatedFilter(string filterText, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        var terms = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => values.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static bool IsCommonAddonNoise(string addonName)
    {
        return addonName is "NamePlate" or "CastBarEnemy" or "_NaviMap" or "_ParameterWidget" or "_DTR" or "_TargetInfo" or "_FocusTargetInfo" or "_EnemyList" or "_PartyList" ||
            addonName.StartsWith("_ActionBar", StringComparison.Ordinal) ||
            addonName.StartsWith("_Status", StringComparison.Ordinal) ||
            addonName.StartsWith("_CastBar", StringComparison.Ordinal);
    }

    private static void DrawTableText(int column, string text, bool wrap = false, bool disabled = false)
    {
        ImGui.TableSetColumnIndex(column);
        if (disabled)
        {
            ImGui.TextDisabled(text);
        }
        else if (wrap)
        {
            ImGui.TextWrapped(text);
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
    }

    private static string FormatBool(bool value)
    {
        return value ? "yes" : "no";
    }

    private static string FormatAddress(nint address)
    {
        return address == 0 ? "-" : $"0x{(long)address:X}";
    }
}
