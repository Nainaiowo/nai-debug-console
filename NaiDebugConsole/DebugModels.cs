using System;
using System.Collections.Generic;

namespace NaiDebugConsole;

public sealed record DebugLogEntry(
    DateTime SeenAtUtc,
    string Message);

public sealed record AddonInspectorEvent(
    DateTime SeenAtUtc,
    string EventName,
    string AddonName,
    nint Address,
    bool IsReady,
    bool IsVisible);

public sealed record AddonInspectorSnapshot(
    DateTime SeenAtUtc,
    string AddonName,
    nint Address,
    bool IsReady,
    bool IsVisible,
    float X,
    float Y,
    float Width,
    float Height,
    int NodeCount,
    IReadOnlyList<AddonInspectorNode> Nodes,
    IReadOnlyList<AddonInspectorValue> AtkValues,
    string? Error);

public sealed record AddonInspectorNode(
    int Index,
    uint NodeId,
    string NodeType,
    bool IsVisible,
    float X,
    float Y,
    ushort Width,
    ushort Height,
    string? Text);

public sealed record AddonInspectorValue(
    int Index,
    string Type,
    string Value);

public sealed record ShareTraceEvent(
    DateTime SeenAtUtc,
    double ElapsedSeconds,
    string Category,
    string Name,
    string Details,
    bool IsFocused);

public sealed record TofuInspectorSnapshot(
    DateTime SeenAtUtc,
    IReadOnlyList<TofuInspectorDataSet> DataSets,
    string? Error);

public sealed record TofuInspectorDataSet(
    string Name,
    int Total,
    int MaxCount,
    IReadOnlyList<TofuInspectorBoard> Boards);

public sealed record TofuInspectorBoard(
    int Index,
    bool IsValid,
    string Name,
    string Folder,
    string PositionInList,
    string ServerTime,
    string Background,
    int ObjectCount,
    IReadOnlyList<TofuInspectorObject> Objects);

public sealed record TofuInspectorObject(
    int Index,
    string ObjectType,
    string X,
    string Y,
    string Scale,
    string Angle,
    string Rgba,
    string Visible,
    string Locked,
    string Flags,
    string RawFlags,
    string Args,
    string? Text);
