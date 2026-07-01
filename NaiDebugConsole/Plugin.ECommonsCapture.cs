using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ObjectLifeTracker;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace NaiDebugConsole;

public sealed partial class Plugin
{
    private const int MaxTrackedVfxPerSnapshot = 256;

    private bool ecommonsActionEffectHookEnabled;
    private bool ecommonsVfxHookEnabled;
    private bool ecommonsMapEffectHookEnabled;
    private bool ecommonsDirectorUpdateHookEnabled;
    private bool ecommonsObjectLifeHookEnabled;

    public bool ECommonsActionEffectHookEnabled => ecommonsActionEffectHookEnabled;

    public bool ECommonsVfxHookEnabled => ecommonsVfxHookEnabled;

    public bool ECommonsMapEffectHookEnabled => ecommonsMapEffectHookEnabled;

    public bool ECommonsDirectorUpdateHookEnabled => ecommonsDirectorUpdateHookEnabled;

    public bool ECommonsObjectLifeHookEnabled => ecommonsObjectLifeHookEnabled;

    private void InitializeECommonsCapture()
    {
        ActionEffect.ActionEffectEvent += OnECommonsActionEffect;
        ecommonsActionEffectHookEnabled = true;

        ActorVfx.ActorVfxCreateEvent += OnECommonsActorVfxCreate;
        ActorVfx.ActorVfxDtorEvent += OnECommonsActorVfxDtor;
        StaticVfx.StaticVfxCreateEvent += OnECommonsStaticVfxCreate;
        StaticVfx.StaticVfxRunEvent += OnECommonsStaticVfxRun;
        StaticVfx.StaticVfxDtorEvent += OnECommonsStaticVfxDtor;
        ecommonsVfxHookEnabled = true;

        ObjectLife.OnObjectCreation += OnECommonsObjectCreated;
        ecommonsObjectLifeHookEnabled = true;

        try
        {
            MapEffect.Init(OnECommonsMapEffect);
            ecommonsMapEffectHookEnabled = true;
        }
        catch (Exception ex)
        {
            ecommonsMapEffectHookEnabled = false;
            Log.Warning(ex, "Nai Debug Console ECommons MapEffect hook could not be enabled.");
        }

        try
        {
            DirectorUpdate.Init(OnECommonsDirectorUpdate);
            ecommonsDirectorUpdateHookEnabled = true;
        }
        catch (Exception ex)
        {
            ecommonsDirectorUpdateHookEnabled = false;
            Log.Warning(ex, "Nai Debug Console ECommons DirectorUpdate hook could not be enabled.");
        }
    }

    private void DisposeECommonsCapture()
    {
        try
        {
            ActionEffect.ActionEffectEvent -= OnECommonsActionEffect;
            ActorVfx.ActorVfxCreateEvent -= OnECommonsActorVfxCreate;
            ActorVfx.ActorVfxDtorEvent -= OnECommonsActorVfxDtor;
            StaticVfx.StaticVfxCreateEvent -= OnECommonsStaticVfxCreate;
            StaticVfx.StaticVfxRunEvent -= OnECommonsStaticVfxRun;
            StaticVfx.StaticVfxDtorEvent -= OnECommonsStaticVfxDtor;
            ObjectLife.OnObjectCreation -= OnECommonsObjectCreated;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not cleanly detach Nai Debug Console ECommons capture handlers.");
        }
    }

    private void OnECommonsActionEffect(ActionEffectSet set)
    {
        try
        {
            if (Configuration.CaptureActionEffects && ShouldCapture())
            {
                CaptureECommonsActionEffect(set);
            }
        }
        catch (Exception ex)
        {
            LastError = $"ActionEffect capture failed: {ex.Message}";
            Log.Warning(ex, "Could not capture Nai Debug Console ECommons action effect.");
        }
    }

    private void OnECommonsActorVfxCreate(nint vfxPtr, nint vfxPathPtr, nint casterAddress, nint targetAddress, float a4, byte a5, ushort a6, byte a7)
    {
        if (!Configuration.CaptureECommonsVfxEvents || !ShouldCapture())
        {
            return;
        }

        var path = ReadVfxPath(vfxPathPtr);
        WriteRecord("ecommons-actor-vfx-create", new
        {
            vfxPtr = FormatPointer(vfxPtr),
            path,
            vfx = CaptureVfxStruct(vfxPtr),
            casterAddress = FormatPointer(casterAddress),
            casterObject = CaptureGameObject(FindObjectByAddress(casterAddress)),
            targetAddress = FormatPointer(targetAddress),
            targetObject = CaptureGameObject(FindObjectByAddress(targetAddress)),
            a4,
            a5,
            a6,
            a7,
        });
    }

    private void OnECommonsActorVfxDtor(nint actorVfxAddress)
    {
        if (!Configuration.CaptureECommonsVfxEvents || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-actor-vfx-destroy", new
        {
            vfxPtr = FormatPointer(actorVfxAddress),
        });
    }

    private void OnECommonsStaticVfxCreate(nint vfxPtr, string path, string systemSource)
    {
        if (!Configuration.CaptureECommonsVfxEvents || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-static-vfx-create", new
        {
            vfxPtr = FormatPointer(vfxPtr),
            path,
            vfx = CaptureVfxStruct(vfxPtr),
            systemSource,
        });
    }

    private void OnECommonsStaticVfxRun(nint staticVfxAddress, float a1, uint a2)
    {
        if (!Configuration.CaptureECommonsVfxEvents || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-static-vfx-run", new
        {
            vfxPtr = FormatPointer(staticVfxAddress),
            vfx = CaptureVfxStruct(staticVfxAddress),
            a1,
            a2,
        });
    }

    private void OnECommonsStaticVfxDtor(nint staticVfxAddress)
    {
        if (!Configuration.CaptureECommonsVfxEvents || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-static-vfx-destroy", new
        {
            vfxPtr = FormatPointer(staticVfxAddress),
        });
    }

    private void OnECommonsMapEffect(long a1, uint a2, ushort a3, ushort a4)
    {
        if (!Configuration.CaptureECommonsMapEffects || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-map-effect", new
        {
            a1,
            a2,
            a3,
            a4,
        });
    }

    private void OnECommonsDirectorUpdate(nint a1, uint a2, DirectorUpdateCategory category, uint a4, uint a5, int a6, int a7, int a8, int a9)
    {
        if (!Configuration.CaptureECommonsDirectorUpdates || !ShouldCapture())
        {
            return;
        }

        WriteRecord("ecommons-director-update", new
        {
            a1 = FormatPointer(a1),
            a2,
            category = category.ToString(),
            categoryRaw = (int)category,
            a4,
            a5,
            a6,
            a7,
            a8,
            a9,
        });
    }

    private void OnECommonsObjectCreated(nint address)
    {
        if (!Configuration.CaptureECommonsObjectLifeEvents || !ShouldCapture())
        {
            return;
        }

        var gameObject = FindObjectByAddress(address);
        WriteRecord("ecommons-object-created", new
        {
            address = FormatPointer(address),
            objectSnapshot = CaptureGameObject(gameObject),
        });
    }

    private void CaptureECommonsActionEffect(ActionEffectSet set)
    {
        if (set.TargetEffects.Length == 0)
        {
            return;
        }

        var targets = new List<object>();
        for (var targetIndex = 0; targetIndex < set.TargetEffects.Length; targetIndex++)
        {
            var target = set.TargetEffects[targetIndex];
            var rawEffects = new List<object>();

            for (var effectIndex = 0; effectIndex < 8; effectIndex++)
            {
                var effect = target[effectIndex];
                if (effect.type == ActionEffectType.Nothing)
                {
                    continue;
                }

                rawEffects.Add(new
                {
                    index = effectIndex,
                    type = (byte)effect.type,
                    typeName = effect.type.ToString(),
                    value = effect.value,
                    calculatedAmount = effect.Damage,
                    param0 = effect.param0,
                    param1 = effect.param1,
                    param2 = effect.param2,
                    mult = effect.mult,
                    flags = effect.flags,
                    damageType = effect.AttackType,
                    isCrit = (effect.param0 & 0x20) == 0x20,
                    isDirectHit = (effect.param0 & 0x40) == 0x40,
                });
            }

            if (rawEffects.Count == 0)
            {
                continue;
            }

            var targetObject = FindObjectByGameObjectIdOrEntityId(target.TargetID);
            var targetEntityId = targetObject?.EntityId ?? (target.TargetID <= uint.MaxValue ? (uint)target.TargetID : 0u);

            targets.Add(new
            {
                index = targetIndex,
                rawTargetId = target.TargetID.ToString(CultureInfo.InvariantCulture),
                objectId = target.TargetID.ToString(CultureInfo.InvariantCulture),
                entityId = targetEntityId,
                objectSnapshot = CaptureGameObject(targetObject),
                effects = rawEffects,
            });
        }

        if (targets.Count == 0)
        {
            return;
        }

        WriteRecord("action-effect", new
        {
            casterEntityId = set.Source?.EntityId ?? 0,
            casterName = set.Source?.Name.TextValue,
            casterObject = CaptureGameObject(set.Source),
            targetPosition = new
            {
                x = set.Position.X,
                y = set.Position.Y,
                z = set.Position.Z,
            },
            actionType = set.Header.ActionType.ToString(),
            actionId = set.Header.ActionID,
            spellId = set.Header.ActionID,
            actionName = string.IsNullOrWhiteSpace(set.Name) ? GetActionName(set.Header.ActionID) : set.Name,
            spellName = string.IsNullOrWhiteSpace(set.Name) ? GetActionName(set.Header.ActionID) : set.Name,
            animationId = set.Header.AnimationId,
            animationTargetId = set.Header.AnimationTargetId.ToString(CultureInfo.InvariantCulture),
            globalEffectCounter = set.Header.GlobalEffectCounter,
            sourceSequence = set.Header.SourceSequence,
            rotation = set.Header.Rotation,
            realRotation = set.Header.RealRotation,
            variation = set.Header.Variation,
            numTargets = set.TargetEffects.Length,
            targets,
        });
    }

    private IReadOnlyList<object> CaptureTrackedVfxSnapshot()
    {
        if (!Configuration.CaptureECommonsVfxEvents)
        {
            return Array.Empty<object>();
        }

        lock (VfxManager.TrackedEffects)
        {
            return VfxManager.TrackedEffects
                .OrderBy(effect => effect.Age)
                .Take(MaxTrackedVfxPerSnapshot)
                .Select(effect => new
                {
                    effect.VfxID,
                    casterId = effect.CasterID.ToString(CultureInfo.InvariantCulture),
                    targetId = effect.TargetID.ToString(CultureInfo.InvariantCulture),
                    path = effect.Path,
                    effect.IsStatic,
                    effect.HasRun,
                    ageSeconds = Math.Round(effect.AgeSeconds, 3),
                    placement = effect.Placement is null
                        ? null
                        : new
                        {
                            position = new
                            {
                                x = effect.Placement.Position.X,
                                y = effect.Placement.Position.Y,
                                z = effect.Placement.Position.Z,
                            },
                            rotation = new
                            {
                                x = effect.Placement.Rotation.X,
                                y = effect.Placement.Rotation.Y,
                                z = effect.Placement.Rotation.Z,
                                w = effect.Placement.Rotation.W,
                            },
                            scale = new
                            {
                                x = effect.Placement.Scale.X,
                                y = effect.Placement.Scale.Y,
                                z = effect.Placement.Scale.Z,
                            },
                        },
                })
                .ToList();
        }
    }

    private static unsafe object? CaptureVfxStruct(nint vfxPtr)
    {
        if (vfxPtr == 0)
        {
            return null;
        }

        try
        {
            var vfx = (VfxStruct*)vfxPtr;
            return new
            {
                position = new
                {
                    x = vfx->Position.X,
                    y = vfx->Position.Y,
                    z = vfx->Position.Z,
                },
                rotation = new
                {
                    x = vfx->Rotation.X,
                    y = vfx->Rotation.Y,
                    z = vfx->Rotation.Z,
                    w = vfx->Rotation.W,
                },
                scale = new
                {
                    x = vfx->Scale.X,
                    y = vfx->Scale.Y,
                    z = vfx->Scale.Z,
                },
                actorCasterId = vfx->ActorCasterID.ToString(CultureInfo.InvariantCulture),
                actorTargetId = vfx->ActorTargetID.ToString(CultureInfo.InvariantCulture),
                staticCasterId = vfx->StaticCasterID.ToString(CultureInfo.InvariantCulture),
                staticTargetId = vfx->StaticTargetID.ToString(CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<object> CaptureTethers(IGameObject? gameObject)
    {
        if (!Configuration.CaptureECommonsTethers || gameObject is not ICharacter character)
        {
            return Array.Empty<object>();
        }

        try
        {
            return character.GetTethers()
                .Select(tether => new
                {
                    tether.Id,
                    tether.IsSource,
                    pairId = tether.PairId,
                    pairObject = CaptureGameObject(tether.Pair, includeTethers: false),
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not capture ECommons tether information.");
            return Array.Empty<object>();
        }
    }

    private static IGameObject? FindObjectByAddress(nint address)
    {
        if (address == nint.Zero)
        {
            return null;
        }

        return ObjectTable.FirstOrDefault(gameObject => gameObject.Address == address);
    }

    private static IGameObject? FindObjectByGameObjectIdOrEntityId(ulong id)
    {
        var gameObject = ObjectTable.SearchById(id);
        if (gameObject is not null)
        {
            return gameObject;
        }

        return id <= uint.MaxValue ? ObjectTable.SearchByEntityId((uint)id) : null;
    }

    private static string ReadVfxPath(nint pathPtr)
    {
        if (pathPtr == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            return MemoryHelper.ReadString(pathPtr, Encoding.ASCII, 512);
        }
        catch
        {
            return string.Empty;
        }
    }
}
