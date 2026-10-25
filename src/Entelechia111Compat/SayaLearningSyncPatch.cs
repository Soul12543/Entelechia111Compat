using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Entelechia111Compat;

internal static class SayaLearningSyncPatch
{
    internal const string SyncType = "figure_Saya.Scripts.Relics.EvolutionRelicMultiplayerSync";
    internal const string RelicType = "figure_Saya.Scripts.Relics.SayaEvolutionRelicBase";
    private static readonly AsyncLocal<int> ReplayDepth = new();
    internal static bool Replaying => ReplayDepth.Value > 0;
    internal static void Install(Harmony harmony,Type sync,Type relic)
    {
        var incoming = AccessTools.DeclaredMethod(sync,"HandleLearningSync") ?? throw new MissingMethodException(sync.FullName,"HandleLearningSync");
        var replay = AccessTools.DeclaredMethod(relic,"ApplySyncedLocalLearning") ?? throw new MissingMethodException(relic.FullName,"ApplySyncedLocalLearning");
        var unsafeCheck = AccessTools.DeclaredMethod(relic,"IsCombatEvolutionSyncUnsafe") ?? throw new MissingMethodException(relic.FullName,"IsCombatEvolutionSyncUnsafe");
        if (replay.ReturnType != typeof(Task) || unsafeCheck.ReturnType != typeof(bool) || !unsafeCheck.IsStatic)
            throw new InvalidOperationException("Unsupported Saya learning sync signatures.");
        harmony.Patch(unsafeCheck,prefix:new(typeof(SayaLearningSyncPatch),nameof(UnsafePrefix)));
        harmony.Patch(replay,prefix:new(typeof(SayaLearningSyncPatch),nameof(EnterReplay)),postfix:new(typeof(SayaLearningSyncPatch),nameof(ExitReplay)),
            finalizer:new(typeof(SayaLearningSyncPatch),nameof(ReplayFinalizer)));
        harmony.Patch(incoming,transpiler:new(typeof(SayaLearningSyncPatch),nameof(ReceiptTranspiler)));
    }
    private static void EnterReplay(out int __state) { __state=ReplayDepth.Value; ReplayDepth.Value=__state+1; }
    private static void ExitReplay(int __state) => ReplayDepth.Value=__state;
    private static Exception? ReplayFinalizer(Exception? __exception,int __state) { ReplayDepth.Value=__state; return __exception; }
    private static bool UnsafePrefix(ref bool __result)
    { if (!Replaying) return true; __result=false; return false; }
    private static IEnumerable<CodeInstruction> ReceiptTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int count=0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo call && call.Name=="IsCombatEvolutionSyncUnsafe" && call.ReturnType==typeof(bool) && call.GetParameters().Length==0)
            { instruction.opcode=OpCodes.Ldc_I4_0; instruction.operand=null; count++; }
            yield return instruction;
        }
        if (count!=1) throw new InvalidOperationException("Expected exactly one Saya learning receipt combat guard.");
    }
}

internal static class OptionalIntegrations
{
    private static Harmony? _harmony;
    private static readonly object Gate=new();
    private static readonly HashSet<Type> Installed=[];
    internal static void Watch(Harmony harmony)
    {
        _harmony=harmony;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) TryInstall(assembly);
        AppDomain.CurrentDomain.AssemblyLoad += (_,args)=>TryInstall(args.LoadedAssembly);
    }
    private static void TryInstall(Assembly assembly)
    {
        if (_harmony==null) return;
        lock (Gate)
        {
            var collector=assembly.GetType("DamageMeter.Scripts.CombatDataCollector",false);
            if (collector!=null && !Installed.Contains(collector))
            {
                InstallOptional(collector,"Skada",patch=>SkadaBridge.Install(patch,collector));
            }
            var sync=assembly.GetType(SayaLearningSyncPatch.SyncType,false);
            var relic=assembly.GetType(SayaLearningSyncPatch.RelicType,false);
            if (sync!=null && relic!=null && !Installed.Contains(sync))
            { InstallOptional(sync,"Saya",patch=>SayaLearningSyncPatch.Install(patch,sync,relic)); }
        }
    }
    private static void InstallOptional(Type type,string name,Action<Harmony> install)
    {
        Installed.Add(type);
        var patch=new Harmony(_harmony!.Id+"."+name);
        try { install(patch); }
        catch(Exception exception)
        {
            patch.UnpatchAll(patch.Id);
            new MegaCrit.Sts2.Core.Logging.Logger(Main.ModId,MegaCrit.Sts2.Core.Logging.LogType.Generic)
                .Warn(name+" compatibility disabled: "+exception.Message);
        }
    }}