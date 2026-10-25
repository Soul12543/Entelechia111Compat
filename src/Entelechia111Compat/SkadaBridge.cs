using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Entelechia111Compat;

internal static class SkadaBridge
{
    private static FieldInfo _tracking = null!;
    private static MethodInfo _record = null!, _notify = null!, _resolve = null!;
    internal static void Install(Harmony harmony,Type collector)
    {
        _tracking = AccessTools.Field(collector,"_isTracking") ?? throw new MissingFieldException(collector.FullName,"_isTracking");
        _record = AccessTools.Method(collector,"RecordDamageForPlayer",[typeof(Creature),typeof(DamageResult),typeof(string),typeof(int)])
            ?? throw new MissingMethodException(collector.FullName,"RecordDamageForPlayer");
        _notify = AccessTools.Method(collector,"NotifyChanged") ?? throw new MissingMethodException(collector.FullName,"NotifyChanged");
        _resolve = AccessTools.Method(collector,"ResolvePowerName") ?? throw new MissingMethodException(collector.FullName,"ResolvePowerName");
        var incoming = AccessTools.Method(collector,"RecordDamage") ?? throw new MissingMethodException(collector.FullName,"RecordDamage");
        var names = AccessTools.Method(collector,"ResolveCardName") ?? throw new MissingMethodException(collector.FullName,"ResolveCardName");
        harmony.Patch(incoming,prefix:new(typeof(SkadaBridge),nameof(RecordPrefix)));
        harmony.Patch(names,prefix:new(typeof(SkadaBridge),nameof(NamePrefix)));
    }
    private static bool NamePrefix(string key,ref string __result)
    {
        if (key is not ("ENTELECHIA-HEART_CANDLE_POWER" or "ENTELECHIA-BLOODLOSS_POWER")) return true;
        __result = (string)_resolve.Invoke(null,[key])!; return false;
    }
    private static bool RecordPrefix(Creature receiver,DamageResult result)
    {
        if (!DamageAttribution.TryGet(result,out var source) || !receiver.IsEnemy) return true;
        if (!(bool)_tracking.GetValue(null)!) return false;
        var shares = source.Shares;
        var unblocked = DamageAttribution.Allocate(result.UnblockedDamage,shares);
        var blocked = DamageAttribution.Allocate(result.BlockedDamage,shares);
        var overkill = DamageAttribution.Allocate(result.OverkillDamage,shares);
        for (int i=0;i<shares.Count;i++)
        {
            if (shares[i].PlayerId is not { } id || unblocked[i]+blocked[i]+overkill[i]<=0) continue;
            var player = receiver.CombatState?.PlayerCreatures.FirstOrDefault(creature=>creature.Player?.NetId==id);
            if (player == null) continue;
            var portion = new DamageResult(receiver,result.Props) { UnblockedDamage=unblocked[i], BlockedDamage=blocked[i], OverkillDamage=overkill[i], WasTargetKilled=result.WasTargetKilled };
            _record.Invoke(null,[player,portion,source.Key,-1]);
        }
        _notify.Invoke(null,null);
        // Skip Skada's heuristic inference, which can mistake modded bleeding for poison.
        return false;
    }
}