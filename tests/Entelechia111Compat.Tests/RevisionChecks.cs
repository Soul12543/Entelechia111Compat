using System.Reflection;
using System.Reflection.Emit;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Commands.Builders;
using System.Runtime.CompilerServices;
using BaseLib.Extensions;
using BaseLib.Utils;
using Entelechia111Compat;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Powers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

internal static class RevisionChecks
{
    private static decimal Lost, Energy, CapturedDamage;
    private static int Drawn, Strength, Speed, MaxHpGained;
    private static bool FailDamage, TriggerHarvest;
    private static int HarvestCount;
    internal static Task Definitions()
    {
        using var scope = new Scope();
        var shield = Mutable(Activator.CreateInstance<BloodShield>());
        Assert.That(shield.EnergyCost.Canonical == 0 && shield.DynamicVars.Block.BaseValue == 4, "Shield canonical cost/block must be 0/4.");
        Upgrade(shield); Assert.That(shield.EnergyCost.GetAmountToSpend()==0 && shield.DynamicVars.Block.BaseValue==6,"Shield upgrade must be 0/6.");
        var ritual = Mutable(Activator.CreateInstance<HeartCandleRitual>());
        Assert.That(ritual.EnergyCost.Canonical==1 && ritual.Keywords.Contains(CardKeyword.Ethereal) && !ritual.Keywords.Contains(CardKeyword.Exhaust),"Ritual changes both cost and actual keywords.");
        var conduit=Mutable(Activator.CreateInstance<ResidualPulseConduit>()); Upgrade(conduit);
        Assert.That(conduit.EnergyCost.GetAmountToSpend()==3 && conduit.DynamicVars.Power<ResidualPulseConduitPower>().BaseValue==3 && conduit.Keywords.Contains(CardKeyword.Innate),$"Conduit: cost={conduit.EnergyCost.GetAmountToSpend()}, amount={conduit.DynamicVars.Power<ResidualPulseConduitPower>().BaseValue}, keywords={string.Join(",",conduit.Keywords)}");
        var feast=Mutable(Activator.CreateInstance<BloodFeast>()); Upgrade(feast);
        Assert.That(feast.EnergyCost.GetAmountToSpend()==1 && feast.TriggerThreshold==2,"Feast upgrade changes both threshold and cost.");
        var rebuild=Mutable(Activator.CreateInstance<BloodRebuild>()); Upgrade(rebuild);
        Assert.That(rebuild.EnergyCost.GetAmountToSpend()==1 && !rebuild.Keywords.Contains(CardKeyword.Exhaust),"Upgraded Rebuild must lose Exhaust.");
        return Task.CompletedTask;
    }
    internal static async Task CandleBurst()
    {
        using var scope=new Scope(); var a=Actor(1);var b=Actor(2);var enemy=Enemy(1000);var state=Combat([a,b],[enemy]);a.CombatState=b.CombatState=enemy.CombatState=state;
        var power=Power<HeartCandlePower>(enemy,a,100);Add(enemy,power);Add(a,Power<BloodDemonFormPower>(a,a,1));
        var card=Card<RedCandleAll>(a);var hit=new DamageResult(enemy,DamageProps.card){UnblockedDamage=5};
        Lost=0;await power.AfterDamageGiven(new ThrowingPlayerChoiceContext(),b,hit,DamageProps.card,enemy,Card<BloodStrike>(b));
        Assert.That(Lost==0 && power.Amount==100,"Foreign attacks cannot burst A's pool.");
        await power.AfterDamageGiven(new ThrowingPlayerChoiceContext(),a,hit,DamageProps.card,enemy,card);
        Assert.That(Lost==225 && enemy.CurrentHp==775 && power.Amount==0,"100 pool plus additive 50% and 75% must release 225 and clear the pool.");
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy,a,.5m)==113,"Natural burst actual damage supplies the original revival budget, rounded up.");
        HeartCandleOwnershipPatch.RecordOwnRevived(enemy,a,100);Set(power,"_amount",100);Lost=0;
        await power.AfterDamageGiven(new ThrowingPlayerChoiceContext(),a,hit,DamageProps.card,enemy,card);
        Assert.That(Lost==225 && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy,a,.5m)==13,"Amplified recycled candle must not mint new revival credit.");
        Set(power,"_amount",20);FailDamage=true;
        try { await power.AfterDamageGiven(new ThrowingPlayerChoiceContext(),a,hit,DamageProps.card,enemy,card); throw new Exception("Expected failure"); } catch(ApplicationException) {} finally {FailDamage=false;}
        Lost=0;await power.AfterDamageGiven(new ThrowingPlayerChoiceContext(),a,hit,DamageProps.card,enemy,card);
        Assert.That(Lost==45,"Candle recursion guard must clear after command failure.");
    }
    internal static async Task RepeatedPowers()
    {
        using var scope=new Scope();var a=Actor(1);var enemy=Enemy(100);a.CombatState=enemy.CombatState=Combat([a],[enemy]);
        var feast=Power<BloodFeastPower>(a,a,1);feast.Threshold=2;Strength=0;
        var count=AccessTools.Method(typeof(BloodFeastPower),"CountLethalHit");
        for(int i=0;i<5;i++)await (Task)count.Invoke(feast,[new ThrowingPlayerChoiceContext(),Card<BloodStrike>(a)])!;
        Assert.That(Strength==2 && feast.HitCount==1,"Five hits at threshold two grant two strength, with one hit carried within the turn.");
        var form=Power<BloodDemonFormPower>(a,a,2);Speed=0;
        await form.AfterCurrentHpChanged(a,-1);await form.AfterCurrentHpChanged(a,-1);await form.AfterCurrentHpChanged(a,1);
        Assert.That(Speed==4,"Each own-turn HP loss gives speed per form stack; healing does not trigger.");
        var pain=Power<PainConversionPower>(a,a,2);Energy=0;Strength=0;
        await pain.AfterCurrentHpChanged(a,-1);await pain.AfterCurrentHpChanged(a,-1);await pain.AfterCurrentHpChanged(a,-1);
        Assert.That(Energy==2 && Strength==2 && pain.TriggerCountThisTurn==2,"Upgraded pain refunds twice, with the two-trigger limit preserved.");
    }
    internal static async Task Absorption()
    {
        using var scope=new Scope();var a=Actor(1);var b=Actor(2);var one=Enemy(1);var blocked=Enemy(10);blocked.GainBlockInternal(10);var state=Combat([a,b],[one,blocked]);a.CombatState=b.CombatState=one.CombatState=blocked.CombatState=state;
        var power=Power<ResidualPulseConduitPower>(a,a,3);var oldHp=a.CurrentHp;var oldMax=a.MaxHp;MaxHpGained=0;
        await power.AfterCurrentHpChanged(b,-1);await power.AfterCurrentHpChanged(a,1);
        Assert.That(MaxHpGained==0,"Another player's damage or healing cannot trigger absorption.");
        await power.AfterCurrentHpChanged(a,-1);
        Assert.That(MaxHpGained==1 && a.MaxHp==oldMax+1 && a.CurrentHp==oldHp+1,"One HP enemy and fully blocked enemy grant exactly one permanent max HP and one heal.");
        Assert.That(blocked.CurrentHp==10 && blocked.Block==7,"Absorption is blockable and cannot count blocked damage as life loss.");
    }
    internal static async Task Replay()
    {
        using var scope=new Scope();var a=Actor(1);var card=Card<BloodStrike>(a);Add(a,Power<BloodSpeedPower>(a,a,2));
        var method=AccessTools.Method(typeof(BloodSpeedReplay),"AddReplay");
        var result=await (Task<int>)method.Invoke(null,[Task.FromResult(2),card])!;
        Assert.That(result==3 && a.Powers.OfType<BloodSpeedPower>().Single().Amount==1,"Blood speed appends one play to existing replays and consumes only one stack.");
        var damageMethod=AccessTools.Method(typeof(CreatureCmd),"Damage",[typeof(PlayerChoiceContext),typeof(IEnumerable<Creature>),typeof(decimal),typeof(ValueProp),typeof(Creature),typeof(CardModel),typeof(CardPlay)]);
        var enemy=Enemy(100);CapturedDamage=0;
        await (Task<IEnumerable<DamageResult>>)damageMethod.Invoke(null,[new ThrowingPlayerChoiceContext(),new[]{enemy},10m,DamageProps.card,a,card,Play(card,enemy,1)])!;
        Assert.That(CapturedDamage==10,"Other replay sources retain full damage.");
        await (Task<IEnumerable<DamageResult>>)damageMethod.Invoke(null,[new ThrowingPlayerChoiceContext(),new[]{enemy},10m,DamageProps.card,a,card,Play(card,enemy,2)])!;
        Assert.That(CapturedDamage==4,"Only the blood-speed replay scales each hit to ceil(35%).");
        await (Task<IEnumerable<DamageResult>>)damageMethod.Invoke(null,[new ThrowingPlayerChoiceContext(),new[]{enemy},3m,DamageProps.cardHpLoss,a,card,Play(card,enemy,2)])!;
        Assert.That(CapturedDamage==3,"Replay HP costs and non-attack effects are not scaled.");
        Assert.That(Harmony.GetPatchInfo(AccessTools.Method(typeof(CardModel),"GeneratePlayCount"))!.Postfixes.Any(p=>p.PatchMethod.DeclaringType==typeof(BloodSpeedReplay)),"Native play-count hook must actually be installed.");
    }
    internal static Task CounterSave()
    {
        using var scope=new Scope(); var a=Actor(1);var feast=Power<BloodFeastPower>(a,a,2);feast.HitCount=2;feast.Threshold=2;feast.TriggeredThisTurn=true;
        var saved=SavedProperties.FromInternal(feast,new ModelId("POWER","FEAST"))!;var restored=Power<BloodFeastPower>(a);saved.FillInternal(restored);
        Assert.That(restored.HitCount==2 && restored.Threshold==2 && restored.TriggeredThisTurn,"Feast trigger boundary survives snapshots.");
        RevisedPowers.FeastPrefix(restored);Assert.That(restored.HitCount==0 && !restored.TriggeredThisTurn,"Restored feast starts the next repeated group correctly.");
        var pain=Power<PainConversionPower>(a,a,2);pain.TriggerCountThisTurn=1;var p=SavedProperties.FromInternal(pain,new ModelId("POWER","PAIN"))!;var p2=Power<PainConversionPower>(a,a,2);p.FillInternal(p2);
        Assert.That(p2.TriggerCountThisTurn==1,"A snapshot cannot reset pain refunds mid-turn.");
        return Task.CompletedTask;
    }
    internal static async Task CardConditions()
    {
        using var scope=new Scope();var a=Actor(1);var enemy=Enemy(100);var debt=Card<BloodDebtSettlement>(a);Add(enemy,Power<BloodHarvestPower>(enemy,a,5));
        var foreign=Actor(2);Add(enemy,Power<BloodHarvestPower>(enemy,foreign,50));
        Energy=0;Drawn=0;await RevisedCards.PlayAsync(debt,new ThrowingPlayerChoiceContext(),Play(debt,enemy));
        Assert.That(a.CurrentHp==45 && Energy==1 && Drawn==0 && enemy.Powers.OfType<BloodHarvestPower>().First().Amount==5,"Debt heals from all owned stacks, leaves them intact and uses pre-heal low-health branch.");
        a.SetCurrentHpInternal(50);Energy=0;await RevisedCards.PlayAsync(debt,new ThrowingPlayerChoiceContext(),Play(debt,Enemy(20)));
        Assert.That(Drawn==1 && Energy==0,"Debt's new high-health draw has no minimum stack condition.");
        var soul=Card<SoulBloodDraw>(a);TriggerHarvest=true;HarvestCount=0;
        a.SetCurrentHpInternal(30);Energy=0;Drawn=0;
        await RevisedCards.PlayAsync(soul,new ThrowingPlayerChoiceContext(),Play(soul,enemy));
        Assert.That(a.CurrentHp==36 && Energy==1 && Drawn==1,"A low-health harvest trigger heals and grants both energy and draw.");
        a.SetCurrentHpInternal(50);Energy=0;Drawn=0;
        await RevisedCards.PlayAsync(soul,new ThrowingPlayerChoiceContext(),Play(soul,enemy));
        Assert.That(a.CurrentHp==56 && Energy==0 && Drawn==0,"A high-health harvest trigger only heals.");
        TriggerHarvest=false;a.SetCurrentHpInternal(30);Energy=0;Drawn=0;
        await RevisedCards.PlayAsync(soul,new ThrowingPlayerChoiceContext(),Play(soul,enemy));
        Assert.That(a.CurrentHp==30 && Energy==0 && Drawn==0,"Without a harvest trigger there is no bonus heal, energy or draw.");
    }
    internal static Task DescriptionTemplates()
    {
        Assert.That(RevisionDescriptions.Find("powers","ENTELECHIA-HEART_CANDLE_POWER.description")!.Contains("加成先相加"),"Candle description matches additive burst.");
        Assert.That(RevisionDescriptions.Find("cards","ENTELECHIA-RESIDUAL_PULSE_CONDUIT.description")!.Contains("吸取"),"Conduit uses the approved absorption terminology.");
        Assert.That(RevisionDescriptions.Find("relics","ENTELECHIA-BLOOD_DEMON_REPLETE.description")!.Contains("每场战斗限一次"),"Revive frequency matches approved rule.");
        Assert.That(RevisionDescriptions.Find("cards","ENTELECHIA-BLOOD_DEMON_FORM.description")!.Contains("每次失去生命"),"Repeated HP-loss form wording is synchronized.");
        using var scope=new Scope();
        var manager=(LocManager)RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
        AccessTools.Method(typeof(LocManager),"LoadLocFormatters").Invoke(manager,null);
        var formatter=AccessTools.Field(typeof(LocManager),"_smartFormatter").GetValue(null)!;
        var format=formatter.GetType().GetMethods().Single(m=>m.Name=="Format" && !m.IsGenericMethod && m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(IFormatProvider),typeof(string),typeof(object[])}));
        var data=(Dictionary<string,Dictionary<string,string>>)AccessTools.Field(typeof(RevisionDescriptions),"Text").GetValue(null)!;
        var types=typeof(BloodShield).Assembly.GetTypes().Where(t=>t.IsSubclassOf(typeof(EntelechiaCard))&&!t.IsAbstract).ToArray();
        foreach(var entry in data["cards"].Where(e=>!e.Key.StartsWith("ENTELECHIA-")))
        {
            var type=types.Single(t=>System.Text.RegularExpressions.Regex.Replace(t.Name,"(?<!^)([A-Z])","_$1").ToUpperInvariant()+".description"==entry.Key);
            foreach(var upgraded in new[]{false,true})
            {
                var card=Mutable((CardModel)Activator.CreateInstance(type)!);
                if(upgraded)Upgrade(card);
                var loc=new LocString("cards",entry.Key);card.DynamicVars.AddTo(loc);
                loc.Add(new IfUpgradedVar(upgraded?UpgradeDisplay.Upgraded:UpgradeDisplay.Normal));
                var rendered=(string)format.Invoke(formatter,[System.Globalization.CultureInfo.InvariantCulture,entry.Value,new object[]{loc.Variables}])!;
                Assert.That(!rendered.Contains('{'),$"Unresolved description: {entry.Key}, upgrade={upgraded}: {rendered}");
            }
        }
        return Task.CompletedTask;
    }
    private sealed class Scope:IDisposable
    {
        internal readonly Harmony H=new("Entelechia111Compat.Tests.Revision");
        internal Scope()
        {
            try
            {
                Lost=Energy=CapturedDamage=0;Drawn=Strength=Speed=MaxHpGained=0;FailDamage=false;
                H.Patch(AccessTools.Method(typeof(TurnStateTracker),"IsOwnerPlayerTurn"),prefix:new(typeof(RevisionChecks),nameof(OwnerTurn)));
                H.Patch(AccessTools.Method(typeof(TurnStateTracker),"LoseHpTracking"),prefix:new(typeof(RevisionChecks),nameof(Lose)));
                H.Patch(AccessTools.Method(typeof(TurnStateTracker),"HealTracking"),prefix:new(typeof(RevisionChecks),nameof(Heal)));
                H.Patch(AccessTools.Method(typeof(CreatureCmd),"GainMaxHp"),prefix:new(typeof(RevisionChecks),nameof(GainMax)));
                H.Patch(AccessTools.Method(typeof(CreatureCmd),"Damage",[typeof(PlayerChoiceContext),typeof(IEnumerable<Creature>),typeof(decimal),typeof(ValueProp),typeof(Creature),typeof(CardModel),typeof(CardPlay)]),prefix:new HarmonyMethod(typeof(RevisionChecks),nameof(Damage)){priority=Priority.Last});
                H.Patch(AccessTools.Method(typeof(PowerCmd),"ModifyAmount"),prefix:new(typeof(RevisionChecks),nameof(Modify)));
                H.Patch(AccessTools.Method(typeof(PowerCmd),"Remove",[typeof(PowerModel)]),prefix:new(typeof(RevisionChecks),nameof(Remove)));
                H.Patch(AccessTools.Method(typeof(PlayerCmd),"GainEnergy"),prefix:new(typeof(RevisionChecks),nameof(GainEnergy)));
                H.Patch(AccessTools.Method(typeof(CardPileCmd),"Draw",[typeof(PlayerChoiceContext),typeof(decimal),typeof(Player),typeof(bool)]),prefix:new(typeof(RevisionChecks),nameof(Draw)));
                foreach(var pair in new[]{(typeof(BloodDemonFormPower),"AfterCurrentHpChanged"),(typeof(PainConversionPower),"AfterCurrentHpChanged"),(typeof(BloodFeastPower),"ApplyStrengthReliablyDuringLethal")})
                    H.Patch(HeartCandleOwnershipPatch.Body(pair.Item1.Assembly,pair.Item1.FullName!,pair.Item2),transpiler:new(typeof(RevisionChecks),nameof(StubApply)));
                H.Patch(AccessTools.Method(typeof(EntelechiaCard),"ExecuteCardAttack"),prefix:new(typeof(RevisionChecks),nameof(FakeAttack)));
                H.Patch(AccessTools.Method(typeof(BloodHarvestPower),"GetTriggerCountForCurrentCardPlay"),prefix:new(typeof(RevisionChecks),nameof(HarvestTriggers)));
                var assembly=typeof(BloodHarvestPower).Assembly;
                BloodHarvestOwnershipPatch.Install(H,assembly);HeartCandleOwnershipPatch.Install(H,assembly);ImmortalBloodlinePatch.Install(H,assembly);DamageAttribution.Install(H,assembly);CompatStatePatch.Install(H,assembly);
                RevisedCards.Install(H);RevisedPowers.Install(H);BloodSpeedReplay.Install(H);RevisionDescriptions.Install(H);
            }
            catch{H.UnpatchAll(H.Id);throw;}
        }
        public void Dispose(){HeartCandleOwnershipPatch.Clear();H.UnpatchAll(H.Id);}
    }
    private static bool FakeAttack(ref Task<AttackCommand> __result){if(TriggerHarvest)HarvestCount++;__result=Task.FromResult<AttackCommand>(null!);return false;}
    private static bool HarvestTriggers(ref int __result){__result=HarvestCount;return false;}
    private static bool OwnerTurn(ref bool __result){__result=true;return false;}
    private static bool Lose(Creature creature,decimal amount,ref Task __result)
    {if(FailDamage){__result=Task.FromException(new ApplicationException("command failed"));return false;}Lost+=amount;creature.SetCurrentHpInternal(creature.CurrentHp-amount);__result=Task.CompletedTask;return false;}
    private static bool Heal(Creature creature,decimal amount,ref Task __result){creature.SetCurrentHpInternal(Math.Min(creature.MaxHp,creature.CurrentHp+amount));__result=Task.CompletedTask;return false;}
    private static bool GainMax(Creature creature,decimal amount,ref Task __result){MaxHpGained+=(int)amount;creature.SetMaxHpInternal(creature.MaxHp+amount);creature.SetCurrentHpInternal(creature.CurrentHp+amount);__result=Task.CompletedTask;return false;}
    private static bool Damage(IEnumerable<Creature> targets,decimal amount,ValueProp props,ref Task<IEnumerable<DamageResult>> __result)
    {
        CapturedDamage=amount;var hits=new List<DamageResult>();
        foreach(var target in targets){var block=target.DamageBlockInternal(amount,props);var result=target.LoseHpInternal(Math.Max(0,amount-block),props);result.BlockedDamage=(int)block;hits.Add(result);}
        __result=Task.FromResult<IEnumerable<DamageResult>>(hits);return false;
    }
    private static bool Modify(PowerModel power,decimal offset,ref Task<int> __result){Set(power,"_amount",Math.Max(0,power.Amount+(int)offset));__result=Task.FromResult(power.Amount);return false;}
    private static bool Remove(PowerModel power,ref Task __result){Set(power,"_amount",0);__result=Task.CompletedTask;return false;}
    private static bool GainEnergy(decimal amount,ref Task __result){Energy+=amount;__result=Task.CompletedTask;return false;}
    private static bool Draw(decimal count,ref Task<IEnumerable<CardModel>> __result){Drawn+=(int)count;__result=Task.FromResult<IEnumerable<CardModel>>([]);return false;}
    private static IEnumerable<CodeInstruction> StubApply(IEnumerable<CodeInstruction> code)
    {
        foreach(var i in code)
        {
            if(i.operand is MethodInfo m && m.DeclaringType==typeof(CommonActions) && m.Name=="Apply" && m.IsGenericMethod)
                i.operand=AccessTools.Method(typeof(RevisionChecks),m.GetParameters().Length==5?nameof(ApplyContext):nameof(ApplySimple)).MakeGenericMethod(m.GetGenericArguments());
            yield return i;
        }
    }
    private static Task<T> ApplyContext<T>(PlayerChoiceContext ctx,Creature target,CardModel? card,decimal amount,bool silent)where T:PowerModel=>ApplySimple<T>(target,card,amount,silent);
    private static Task<T> ApplySimple<T>(Creature target,CardModel? card,decimal amount,bool silent)where T:PowerModel
    {
        var power=target.Powers.OfType<T>().FirstOrDefault();
        if(power==null){power=Power<T>(target,target,0);Add(target,power);}
        Set(power,"_amount",power.Amount+(int)amount);
        if(typeof(T)==typeof(BloodSpeedPower))Speed+=(int)amount;else Strength+=(int)amount;
        return Task.FromResult(power);
    }
    private static CardPlay Play(CardModel card,Creature? target,int index=0)=>new(){Card=card,Player=card.Owner,Target=target,ResultPile=PileType.Discard,Resources=new ResourceInfo{EnergySpent=0,EnergyValue=0,StarsSpent=0,StarValue=0},IsAutoPlay=false,PlayIndex=index,PlayCount=3};
    private static T Mutable<T>(T model)where T:AbstractModel{Set(model,"<IsMutable>k__BackingField",true);return model;}
    private static void Upgrade(CardModel card)=>AccessTools.Method(card.GetType(),"OnUpgrade").Invoke(card,null);
    private static T Card<T>(Creature owner)where T:CardModel,new(){var c=Mutable(new T());Set(c,"_owner",owner.Player);return c;}
    private static Creature Actor(ulong id){var p=(Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));var c=new Creature(p,30,60);Set(p,"<Creature>k__BackingField",c);Set(p,"<NetId>k__BackingField",id);return c;}
    private static Creature Enemy(int hp){var c=new Creature(null!,hp,hp);Set(c,"<Side>k__BackingField",CombatSide.Enemy);return c;}
    private static T Power<T>(Creature owner,Creature? applier=null,int amount=1)where T:PowerModel{var p=(T)RuntimeHelpers.GetUninitializedObject(typeof(T));Mutable(p);Set(p,"<Id>k__BackingField",new ModelId("POWER",typeof(T).Name));Set(p,"_owner",owner);Set(p,"_applier",applier);Set(p,"_amount",amount);return p;}
    private static void Add(Creature c,params PowerModel[] powers)=>((List<PowerModel>)AccessTools.Field(typeof(Creature),"_powers").GetValue(c)!).AddRange(powers);
    private static void Set(object o,string field,object? value)=>AccessTools.Field(o.GetType(),field).SetValue(o,value);
    public class CombatProxy:DispatchProxy{internal Creature[] Players=[],Enemies=[];protected override object? Invoke(MethodInfo? m,object?[]? a)=>m?.Name switch{"get_PlayerCreatures"=>Players,"get_Enemies"=>Enemies,"get_Encounter"=>null,_=>throw new NotSupportedException(m?.Name)};}
    private static ICombatState Combat(Creature[] p,Creature[] e){var c=DispatchProxy.Create<ICombatState,CombatProxy>();((CombatProxy)(object)c).Players=p;((CombatProxy)(object)c).Enemies=e;return c;}
}
