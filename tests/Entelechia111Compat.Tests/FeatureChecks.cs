using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BaseLib.Utils;
using Entelechia111Compat;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Character;
using Entelechia.EntelechiaCode.Powers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

internal static class FeatureChecks
{
    private static readonly Dictionary<ulong,decimal> Healed=[];
    private static DamageResult? LastDamage;
    internal static Task CardDefinition()
    {
        var property=typeof(RepleteReadyPower).GetProperty(CompatStatePatch.Key)!;
        Assert.That(property.GetCustomAttribute<SavedPropertyAttribute>()!=null,"Extension wire key remains registered after removing Shared Blood Covenant.");
        Assert.That(new RepleteReadyPower().Localization!.Any(p=>p.Item2.Contains("本场战斗可触发1次")),"Revive status must display its once-per-combat rule.");
        Assert.That(typeof(Main).Assembly.GetType("Entelechia111Compat.SharedBloodCovenant")==null,"Cancelled card must not remain in the assembly.");
        return Task.CompletedTask;
    }
    internal sealed class RelicProbe
    {
        private bool _emberAppliedThisCombat;
        private int _emberBloodlineStacks;
        private decimal _emberHarvestAmount;
        internal RelicProbe(bool applied,int stacks,decimal harvest){_emberAppliedThisCombat=applied;_emberBloodlineStacks=stacks;_emberHarvestAmount=harvest;}
        internal (bool,int,decimal) Values=>(_emberAppliedThisCombat,_emberBloodlineStacks,_emberHarvestAmount);
    }
    internal static Task EmberSave()
    {
        using var scope=new Scope();
        var relic=new RelicProbe(true,3,2.750000000000000000000000001m);
        var saved=CompatStatePatch.Pack(CompatStatePatch.CaptureRelic(relic),39);
        var json=JsonSerializer.Serialize(saved,new JsonSerializerOptions{IncludeFields=true});
        var decoded=JsonSerializer.Deserialize<SavedProperties>(json,new JsonSerializerOptions{IncludeFields=true})!;
        var other=new RelicProbe(false,0,0);CompatStatePatch.RestoreRelic(other,CompatStatePatch.Unpack(decoded,"relic"));
        Assert.That(other.Values==relic.Values && decoded.ints!.Single().value==39,"Relic state and existing revive floor must survive JSON exactly.");
        var ember=Power<EmberBloodlinePower>(Actor(1),null,2);ember.HarvestAllEnemiesAmount=3.25m;Set(ember,"_baseRewardGranted",true);
        var native=SavedProperties.FromInternal(ember,new ModelId("POWER","EMBER"))!;
        var target=Power<EmberBloodlinePower>(Actor(2));native.FillInternal(target);
        Assert.That(target.HarvestAllEnemiesAmount==3.25m && (bool)AccessTools.Field(typeof(EmberBloodlinePower),"_baseRewardGranted").GetValue(target)!,"Real Ember power must retain recurring harvest and avoid granting base rewards twice.");
        new SavedProperties().FillInternal(target);
        Assert.That(target.HarvestAllEnemiesAmount==0 && !(bool)AccessTools.Field(typeof(EmberBloodlinePower),"_baseRewardGranted").GetValue(target)!,"Legacy saves must have defined empty defaults, not invented historical state.");
        try { CompatStatePatch.Unpack(new SavedProperties{strings=[new(CompatStatePatch.Key,"{broken")]},"relic");throw new Exception("Malformed payload accepted"); }
        catch(InvalidDataException){}
        try { CompatStatePatch.Unpack(CompatStatePatch.Pack(new(){Kind="relic",Version=2}),"relic");throw new Exception("Unknown version accepted"); }
        catch(InvalidDataException){}
        return Task.CompletedTask;
    }
    internal static Task NativeWire()
    {
        using var scope=new Scope();using var cache=new NativeCache();
        var source=Power<EmberBloodlinePower>(Actor(1));source.HarvestAllEnemiesAmount=3.125m;Set(source,"_baseRewardGranted",true);
        var saved=SavedProperties.FromInternal(source,new ModelId("POWER","EMBER"))!;
        var writer=new PacketWriter();saved.Serialize(writer);var reader=new PacketReader();reader.Reset(writer.Buffer);
        var received=new SavedProperties();received.Deserialize(reader);var target=Power<EmberBloodlinePower>(Actor(1));received.FillInternal(target);
        Assert.That(reader.BitPosition==writer.BitPosition && target.HarvestAllEnemiesAmount==3.125m && (bool)AccessTools.Field(typeof(EmberBloodlinePower),"_baseRewardGranted").GetValue(target)!,"Actual network packet serialization must preserve the registered extension payload.");
        return Task.CompletedTask;
    }
    internal static Task BleedBalances()
    {
        using var scope=new Scope();var a=Actor(10);var b=Actor(20);var enemy=Enemy();var bleed=Power<BloodlossPower>(enemy,a,5);
        DamageAttribution.TrackBleed(bleed,5,a);Set(bleed,"_amount",10);DamageAttribution.TrackBleed(bleed,5,b);
        var first=DamageAttribution.Describe(bleed);Assert.That(first.Shares.Select(s=>s.Weight).SequenceEqual(new[]{5,5}),"Two appliers must each own their five stacks in the statistical ledger.");
        Set(bleed,"_amount",9);DamageAttribution.TrackBleed(bleed,-1,enemy);
        var payload=DamageAttribution.CaptureBleed(bleed);
        Assert.That(payload.BleedSources!.Values.Sum()==9 && bleed.Amount==9,"One global stack decays; attribution must not split the gameplay power.");
        var clone=Power<BloodlossPower>(enemy,a,9);CompatStatePatch.Capture(bleed).FillInternal(clone);
        Assert.That(DamageAttribution.Describe(clone).Shares.SequenceEqual(DamageAttribution.Describe(bleed).Shares),"Contribution ownership must survive native restore.");
        var allocations=DamageAttribution.Allocate(7,[new(10,5),new(20,4)]);
        Assert.That(allocations.Sum()==7 && allocations.SequenceEqual(new[]{4,3}),"Integer splitting must conserve damage with deterministic remainders.");
        var unknown=Power<BloodlossPower>(enemy,null,7);
        Assert.That(DamageAttribution.Describe(unknown).Shares.Single().PlayerId==null,"Unattributed legacy stacks must not be assigned to a local player.");
        return Task.CompletedTask;
    }
    internal static async Task ActualBleedTick()
    {
        using var scope=new Scope(commands:true);var a=Actor(10);var enemy=Enemy();var state=Combat([a],[enemy]);a.CombatState=enemy.CombatState=state;
        var bleed=Power<BloodlossPower>(enemy,a,5);Add(enemy,bleed);DamageAttribution.TrackBleed(bleed,5,a);LastDamage=null;
        await bleed.AfterSideTurnStart(CombatSide.Enemy,[enemy],state);
        Assert.That(enemy.CurrentHp==95 && bleed.Amount==4,"Original bleeding damage and one-stack decay must remain unchanged.");
        Assert.That(LastDamage!=null && DamageAttribution.TryGet(LastDamage,out var source) && source.Key=="ENTELECHIA-BLOODLOSS_POWER" && source.Shares.Single().PlayerId==10,
            "Actual original tick must tag the native damage result before history collection.");
    }
    internal static Task SkadaAttribution()
    {
        using var scope=new Scope();SkadaBridge.Install(scope.Harmony,typeof(FakeSkada));FakeSkada.Clear();
        var a=Actor(10);var b=Actor(20);var enemy=Enemy();enemy.CombatState=Combat([a,b],[enemy]);
        var result=new DamageResult(enemy,DamageProps.nonCardHpLoss){UnblockedDamage=7,OverkillDamage=2};
        DamageAttribution.Tag(result,new(enemy,"ENTELECHIA-BLOODLOSS_POWER",[new(10,5),new(20,4)]));
        FakeSkada.RecordDamage(null,null,enemy,result,null);
        Assert.That(FakeSkada.Fallback==0 && FakeSkada.Damage.Values.Sum()==9 && FakeSkada.Overkill.Values.Sum()==2,$"Modded bleeding attribution: fallback={FakeSkada.Fallback}, damage={FakeSkada.Damage.Values.Sum()}, overkill={FakeSkada.Overkill.Values.Sum()}, enemy={enemy.IsEnemy}.");
        Assert.That(FakeSkada.ResolveCardName("ENTELECHIA-BLOODLOSS_POWER")=="失血" && FakeSkada.ResolveCardName("ENTELECHIA-HEART_CANDLE_POWER")=="心烛","Effect names must replace Other.");
        FakeSkada.RecordDamage(null,a,enemy,new(enemy,DamageProps.card){UnblockedDamage=3},Card(a));
        Assert.That(FakeSkada.Fallback==1,"Ordinary card statistics must keep their original collector path.");
        var selfLoss=new DamageResult(a,DamageProps.nonCardHpLoss){UnblockedDamage=2};
        DamageAttribution.Tag(selfLoss,new(a,"ENTELECHIA-BLOODLOSS_POWER",[new(10,1)]));
        FakeSkada.RecordDamage(null,null,a,selfLoss,null);
        Assert.That(FakeSkada.Fallback==2,"Player bleeding must retain the original damage-taken collector path.");
        return Task.CompletedTask;
    }
    internal static async Task SayaReplay()
    {
        using var harmonyScope=new UnpatchScope("Entelechia111Compat.Tests.Saya");SayaLearningSyncPatch.Install(harmonyScope.Harmony,typeof(FakeSayaSync),typeof(FakeSayaRelic));
        FakeSayaRelic.Before=FakeSayaRelic.After=true;FakeSayaRelic.Fail=false;
        AccessTools.Method(typeof(FakeSayaSync),"HandleLearningSync").Invoke(null,null);
        Assert.That(FakeSayaRelic.IsCombatEvolutionSyncUnsafe(),"Replay scope must be restored immediately for the caller.");
        await FakeSayaSync.Last!;
        Assert.That(!FakeSayaRelic.Before && !FakeSayaRelic.After && FakeSayaRelic.IsCombatEvolutionSyncUnsafe(),"Late remote learning must run across awaits with off-combat ownership checks, while normal combat checks remain active.");
        FakeSayaRelic.Fail=true;
        try { await (Task)AccessTools.Method(typeof(FakeSayaRelic),"ApplySyncedLocalLearning").Invoke(new FakeSayaRelic(),null)!;throw new Exception("Missing failure"); }
        catch(ApplicationException){}
        Assert.That(!SayaLearningSyncPatch.Replaying,"Faulted replay must not leak the context.");
    }
    internal static Task ActualOptionalHooks()
    {
        using var scope=new UnpatchScope("Entelechia111Compat.Tests.OptionalActual");
        var meter=Assembly.LoadFrom("G:/SteamLibrary/steamapps/workshop/content/2868840/3747557283/DamageMeter.dll");
        SkadaBridge.Install(scope.Harmony,meter.GetType("DamageMeter.Scripts.CombatDataCollector",true)!);
        var saya=Assembly.LoadFrom("G:/单机版游戏/Entelechia111Compat/.tools/inspect/Saya.Api1110.dll");
        var sync=saya.GetType(SayaLearningSyncPatch.SyncType,true)!;var relic=saya.GetType(SayaLearningSyncPatch.RelicType,true)!;
        SayaLearningSyncPatch.Install(scope.Harmony,sync,relic);
        Assert.That(!PatchProcessor.GetCurrentInstructions(AccessTools.Method(sync,"HandleLearningSync")).Any(i=>i.operand is MethodInfo m&&m.Name=="IsCombatEvolutionSyncUnsafe"),"Actual installed Saya handler must stop dropping received messages during combat.");
        return Task.CompletedTask;
    }
    private static bool HealPrefix(Creature creature,decimal amount,ref Task __result)
    {var id=creature.Player!.NetId;Healed[id]=Healed.GetValueOrDefault(id)+amount;creature.SetCurrentHpInternal(creature.CurrentHp+amount);__result=Task.CompletedTask;return false;}
    private static bool LossPrefix(Creature creature,decimal amount,ValueProp props,ref Task __result)
    {LastDamage=(DamageResult)AccessTools.Method(typeof(Creature),nameof(Creature.LoseHpInternal)).Invoke(creature,[amount,props])!;__result=Task.CompletedTask;return false;}
    private static bool ModifyPrefix(PowerModel power,decimal offset,ref Task<int> __result){Set(power,"_amount",power.Amount+(int)offset);__result=Task.FromResult(power.Amount);return false;}
    private static bool RemovePrefix(PowerModel power,ref Task __result){Set(power,"_amount",0);__result=Task.CompletedTask;return false;}
    internal sealed class Scope:IDisposable
    {
        internal readonly Harmony Harmony=new("Entelechia111Compat.Tests.Features");
        internal Scope(bool commands=false)
        {
            try
            {
                if(commands)
                {
                    Harmony.Patch(AccessTools.Method(typeof(TurnStateTracker),"HealTracking"),prefix:new(typeof(FeatureChecks),nameof(HealPrefix)));
                    Harmony.Patch(AccessTools.Method(typeof(TurnStateTracker),"LoseHpTracking"),prefix:new(typeof(FeatureChecks),nameof(LossPrefix)));
                    Harmony.Patch(AccessTools.Method(typeof(PowerCmd),nameof(PowerCmd.ModifyAmount)),prefix:new(typeof(FeatureChecks),nameof(ModifyPrefix)));
                    Harmony.Patch(AccessTools.Method(typeof(PowerCmd),nameof(PowerCmd.Remove),[typeof(PowerModel)]),prefix:new(typeof(FeatureChecks),nameof(RemovePrefix)));
                }
                var assembly=typeof(BloodHarvestPower).Assembly;
                BloodHarvestOwnershipPatch.Install(Harmony,assembly);HeartCandleOwnershipPatch.Install(Harmony,assembly);
                DamageAttribution.Install(Harmony,assembly);CompatStatePatch.Install(Harmony,assembly);
            }
            catch{Harmony.UnpatchAll(Harmony.Id);throw;}
        }
        public void Dispose(){HeartCandleOwnershipPatch.Clear();Harmony.UnpatchAll(Harmony.Id);}
    }
    internal sealed class UnpatchScope(string id):IDisposable{internal readonly Harmony Harmony=new(id);public void Dispose()=>Harmony.UnpatchAll(Harmony.Id);}
    private sealed class NativeCache:IDisposable
    {
        private readonly Dictionary<string,int> Names=(Dictionary<string,int>)AccessTools.Field(typeof(ModelIdSerializationCache),"_propertyNameToNetIdMap").GetValue(null)!;
        private readonly List<string> Ids=(List<string>)AccessTools.Field(typeof(ModelIdSerializationCache),"_netIdToPropertyNameMap").GetValue(null)!;
        private readonly Dictionary<string,int> OldNames;private readonly string[] OldIds;private readonly bool Initialized;private readonly int Bits;
        internal NativeCache()
        {
            OldNames=new(Names);OldIds=Ids.ToArray();Initialized=(bool)Get("_initialized")!;Bits=(int)Get("<PropertyIdBitSize>k__BackingField")!;
            Names.Clear();Ids.Clear();AccessTools.Method(typeof(ModelIdSerializationCache),"CachePropertiesForType").Invoke(null,new object?[]{typeof(RepleteReadyPower),null,null});Names["LastRevivedFloor"]=Ids.Count;Ids.Add("LastRevivedFloor");Put("_initialized",true);Put("<PropertyIdBitSize>k__BackingField",1);
        }
        public void Dispose(){Names.Clear();foreach(var pair in OldNames)Names.Add(pair.Key,pair.Value);Ids.Clear();Ids.AddRange(OldIds);Put("_initialized",Initialized);Put("<PropertyIdBitSize>k__BackingField",Bits);}
        private static object? Get(string field)=>AccessTools.Field(typeof(ModelIdSerializationCache),field).GetValue(null);
        private static void Put(string field,object value)=>AccessTools.Field(typeof(ModelIdSerializationCache),field).SetValue(null,value);
    }
    public static class FakeSkada
    {
        private static bool _isTracking=true;
        internal static int Fallback;internal static readonly Dictionary<ulong,int> Damage=[];internal static readonly Dictionary<ulong,int> Overkill=[];
        internal static void Clear(){Damage.Clear();Overkill.Clear();Fallback=0;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static void RecordDamage(object? combatState,Creature? dealer,Creature receiver,DamageResult result,CardModel? cardSource){if(_isTracking)Fallback++;}
        private static void RecordDamageForPlayer(Creature playerCreature,DamageResult result,string sourceKey,int overrideDmg=-1){var id=playerCreature.Player!.NetId;Damage[id]=Damage.GetValueOrDefault(id)+result.UnblockedDamage+result.BlockedDamage+result.OverkillDamage;Overkill[id]=Overkill.GetValueOrDefault(id)+result.OverkillDamage;}
        private static void NotifyChanged(){}
        private static string ResolvePowerName(string key)=>key.Contains("HEART_CANDLE")?"心烛":"失血";
        [MethodImpl(MethodImplOptions.NoInlining)] public static string ResolveCardName(string key)=>key;
    }
    public sealed class FakeSayaRelic
    {
        internal static bool Before,After,Fail;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool IsCombatEvolutionSyncUnsafe()=>true;
        [MethodImpl(MethodImplOptions.NoInlining)] public async Task ApplySyncedLocalLearning(){Before=IsCombatEvolutionSyncUnsafe();await Task.Yield();After=IsCombatEvolutionSyncUnsafe();if(Fail)throw new ApplicationException("test");}
    }
    public static class FakeSayaSync
    {
        internal static Task? Last;
        [MethodImpl(MethodImplOptions.NoInlining)] private static void HandleLearningSync(){if(!FakeSayaRelic.IsCombatEvolutionSyncUnsafe())Last=new FakeSayaRelic().ApplySyncedLocalLearning();}
    }
    public class CombatProxy:DispatchProxy
    {
        internal Creature[] Players=[],Enemies=[];
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch{"get_PlayerCreatures"=>Players,"get_Enemies"=>Enemies,"get_Encounter"=>null,_=>throw new NotSupportedException(method?.Name)};
    }
    private static ICombatState Combat(Creature[] players,Creature[] enemies){var result=DispatchProxy.Create<ICombatState,CombatProxy>();var proxy=(CombatProxy)(object)result;proxy.Players=players;proxy.Enemies=enemies;return result;}
    private static Creature Actor(ulong id){var player=(Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));var creature=new Creature(player,30,60);Set(player,"<Creature>k__BackingField",creature);Set(player,"<NetId>k__BackingField",id);return creature;}
    private static Creature Enemy(){var creature=new Creature(null!,100,100);Set(creature,"<Side>k__BackingField",CombatSide.Enemy);return creature;}
    private static T Power<T>(Creature owner,Creature? applier=null,int amount=1)where T:PowerModel{var power=(T)RuntimeHelpers.GetUninitializedObject(typeof(T));Set(power,"<IsMutable>k__BackingField",true);Set(power,"<Id>k__BackingField",new ModelId("POWER",typeof(T).Name));Set(power,"_owner",owner);Set(power,"_applier",applier);Set(power,"_amount",amount);return power;}
    private static CardModel Card(Creature owner){var card=(CardModel)RuntimeHelpers.GetUninitializedObject(typeof(DiscontinuousPulse));Set(card,"<IsMutable>k__BackingField",true);Set(card,"_owner",owner.Player);Set(card,"<Type>k__BackingField",CardType.Attack);return card;}
    private static void Add(Creature creature,params PowerModel[] powers)=>((List<PowerModel>)AccessTools.Field(typeof(Creature),"_powers").GetValue(creature)!).AddRange(powers);
    private static void Set(object value,string field,object? data)=>AccessTools.Field(value.GetType(),field).SetValue(value,data);
}