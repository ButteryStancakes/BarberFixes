using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
//using UnityEngine.Events;

namespace BarberFixes
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    //[BepInDependency(LETHAL_FIXES, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(VENT_SPAWN_FIX, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.barberfixes", PLUGIN_NAME = "Barber Fixes", PLUGIN_VERSION = "1.2.0", /*LETHAL_FIXES = "Dev1A3.LethalFixes",*/ VENT_SPAWN_FIX = "butterystancakes.lethalcompany.ventspawnfix";
        internal static new ManualLogSource Logger;

        internal static ConfigEntry<bool>configSpawnInPairs, configDrumrollFromAll, configApplySpawningSettings, configOnlyOneBarber;

        internal static bool CAN_SPAWN_IN_GROUPS;

        void Awake()
        {
            Logger = base.Logger;

            /*if (Chainloader.PluginInfos.ContainsKey(LETHAL_FIXES))
            {
                CAN_SPAWN_IN_GROUPS = true;
                Logger.LogInfo("CROSS-COMPATIBILITY - LethalFixes detected");
            }
            else*/ if (Chainloader.PluginInfos.ContainsKey(VENT_SPAWN_FIX))
            {
                CAN_SPAWN_IN_GROUPS = true;
                Logger.LogInfo("CROSS-COMPATIBILITY - VentSpawnFix detected");
            }

            configApplySpawningSettings = Config.Bind(
                "Spawning",
                "ApplySpawningSettings",
                false,
                "The rest of the \"Spawning\" section's settings are only applied if this is enabled. You should disable this if you are using something else to configure enemy variables! (i.e. LethalQuantities)");

            configOnlyOneBarber = Config.Bind(
                "Spawning",
                "OnlyOneBarber",
                true,
                "(Host only) Only allow 1 Barber to spawn each day. Disabling this will raise the limit to 8 per day, as it was before v62.");

            configSpawnInPairs = Config.Bind(
                "Spawning",
                "SpawnInPairs",
                false,
                "(Host only) Spawns Barbers in groups of 2, like in beta v55. This does nothing when \"OnlyOneBarber\" is enabled.\nNOTE: This REQUIRES VentSpawnFix to work!");

            configDrumrollFromAll = Config.Bind(
                "Music",
                "DrumrollFromAll",
                false,
                "If true, all Barbers will play the drumroll audio before they \"jump\". If false, only the master Barber will drumroll.\nThis is false in vanilla, although whether that's by design or a bug is unclear.");

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class BarberFixesPatches
    {
        static readonly MethodInfo OBJECT_DESTROY = AccessTools.Method(typeof(Object), nameof(Object.Destroy), [typeof(Object)]);
        static readonly FieldInfo MUSIC_AUDIO_2 = AccessTools.Field(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.musicAudio2));
        static readonly FieldInfo ON_HOUR_CHANGED = AccessTools.Field(typeof(TimeOfDay), nameof(TimeOfDay.onHourChanged));

        [HarmonyPatch(typeof(ClaySurgeonAI), "ChooseMasterSurgeon")]
        [HarmonyPrefix]
        static bool PreChooseMasterSurgeon(ClaySurgeonAI __instance, ref bool ___isMaster)
        {
            if (!__instance.IsServer)
                return false;

            ClaySurgeonAI[] barbers = Object.FindObjectsOfType<ClaySurgeonAI>();

            // only barber on the map; become the master
            if (barbers.Length == 1)
                __instance.master = __instance;
            // find whatever barber is already the master
            else
            {
                __instance.master = barbers.FirstOrDefault(barber => barber.master == barber);

                // no master has been assigned yet, means 2 barbers spawned at the same time
                if (__instance.master == null)
                    __instance.master = __instance;
            }

            if (__instance.master == __instance)
            {
                ___isMaster = true;
                DanceClock.Start(__instance.startingInterval, __instance.endingInterval);
            }

            __instance.master.SendDanceBeat = new();
            for (int i = 0; i < barbers.Length; i++)
            {
                if (!__instance.master.allClaySurgeons.Contains(barbers[i]))
                    __instance.master.allClaySurgeons.Add(barbers[i]);

                if (barbers[i] != __instance.master)
                {
                    barbers[i].master = __instance.master;
                    barbers[i].ListenToMasterSurgeon();
                }
            }

            __instance.master.SyncMasterClaySurgeonClientRpc();

            // vanilla's function for this is just entirely too buggy
            return false;
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.ListenToMasterSurgeon))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransListenToMasterSurgeon(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            Label? ret = null;
            for (int i = 0; i < codes.Count; i++)
            {
                if (ret == null)
                {
                    // store return's label for later
                    if (codes[i].opcode == OpCodes.Brtrue)
                    {
                        ret = (Label)codes[i].operand;
                        Plugin.Logger.LogDebug("Transpiler (ListenToMasterSurgeon): Allow when \"listeningToMasterSurgeon\" is already true");
                    }

                    // remove this instruction and move on to the next
                    codes.RemoveAt(i--);
                }
                else
                {
                    if (codes[i].opcode == OpCodes.Call && (MethodInfo)codes[i].operand == OBJECT_DESTROY && codes[i - 2].opcode == OpCodes.Ldfld && (FieldInfo)codes[i - 2].operand == MUSIC_AUDIO_2)
                    {
                        codes.RemoveRange(i - 3, 4);
                        Plugin.Logger.LogDebug("Transpiler (ListenToMasterSurgeon): Don't destroy \"musicAudio2\" (causes NullReferenceException)");
                        i -= 3;
                    }

                    // remove return label to prevent compilation error
                    if (codes[i].labels.Contains((Label)ret))
                        codes[i].labels.Remove((Label)ret);
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.SyncMasterClaySurgeonClientRpc))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransSyncMasterClaySurgeonClientRpc(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand.ToString().Contains("AddListener") && codes[i - 4].opcode == OpCodes.Ldfld && (FieldInfo)codes[i - 4].operand == ON_HOUR_CHANGED)
                {
                    codes.RemoveRange(i - 5, 6);
                    Plugin.Logger.LogDebug("Transpiler (SyncMasterClaySurgeonClientRpc): Don't add listener to \"onHourChanged\"");
                    i -= 5;

                    codes.InsertRange(i, [
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.startingInterval))),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.endingInterval))),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DanceClock), nameof(DanceClock.Start)))
                    ]);
                    Plugin.Logger.LogDebug("Transpiler (SyncMasterClaySurgeonClientRpc): Use new \"DanceClock\"");
                }
                else if (codes[i].opcode == OpCodes.Call && (MethodInfo)codes[i].operand == OBJECT_DESTROY && codes[i - 2].opcode == OpCodes.Ldfld && (FieldInfo)codes[i - 2].operand == MUSIC_AUDIO_2)
                {
                    codes.RemoveRange(i - 6, 7);
                    Plugin.Logger.LogDebug("Transpiler (SyncMasterClaySurgeonClientRpc): Don't destroy \"musicAudio2\" (causes NullReferenceException)");
                    i -= 6;
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.OnDestroy))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ClaySurgeonAITransOnDestroy(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand.ToString().Contains("RemoveListener") && codes[i - 4].opcode == OpCodes.Ldfld && (FieldInfo)codes[i - 4].operand == ON_HOUR_CHANGED)
                {
                    codes.RemoveRange(i - 5, 6);
                    Plugin.Logger.LogDebug("Transpiler (ClaySurgeonAI.OnDestroy): Don't remove listener from \"onHourChanged\"");
                    i -= 5;

                    codes.Insert(i, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DanceClock), nameof(DanceClock.Stop))));
                    Plugin.Logger.LogDebug("Transpiler (ClaySurgeonAI.OnDestroy): Use new \"DanceClock\"");
                }
            }

            return codes;
        }

        /*[HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.KillPlayerClientRpc))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ClaySurgeonAITransKillPlayerClientRpc(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            FieldInfo snareIntervalTimer = AccessTools.Field(typeof(ClaySurgeonAI), "snareIntervalTimer");
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Stfld && (FieldInfo)codes[i].operand == snareIntervalTimer)
                {
                    codes.RemoveRange(i - 2, 2);
                    Plugin.Logger.LogDebug("Transpiler (ClaySurgeonAI.KillPlayerClientRpc): Don't cap \"snareIntervalTimer\"");
                    break; //i -= 2;
                }
             }

            return codes;
        }*/

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.KillPlayerClientRpc))]
        [HarmonyPostfix]
        static void ClaySurgeonAIPostKillPlayerClientRpc(ClaySurgeonAI __instance, ref bool ___isMaster, ref float ___beatTimer, ref float ___snareIntervalTimer)
        {
            if (__instance.IsOwner && ___isMaster)
            {
                ___beatTimer = Mathf.Min(___beatTimer, 4f + __instance.snareOffset);
                ___snareIntervalTimer = ___beatTimer - __instance.snareOffset;
            }
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), "PlayMusic")]
        [HarmonyPostfix]
        static void ClaySurgeonAIPostPlayMusic(ClaySurgeonAI __instance, float ___snareIntervalTimer)
        {
            if (___snareIntervalTimer == 100f && Plugin.configDrumrollFromAll.Value)
            {
                foreach (ClaySurgeonAI barber in __instance.allClaySurgeons)
                {
                    if (barber != __instance)
                    {
                        barber.musicAudio.PlayOneShot(barber.snareDrum);
                        WalkieTalkie.TransmitOneShotAudio(barber.musicAudio, barber.snareDrum, 1f);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.PlotOutEnemiesForNextHour))]
        [HarmonyPrefix]
        static void PrePlotOutEnemiesForNextHour(RoundManager __instance)
        {
            if (!__instance.IsServer || !Plugin.configApplySpawningSettings.Value)
                return;

            int spawnInGroupsOf = 1;
            if (Plugin.configSpawnInPairs.Value)
            {
                if (Plugin.CAN_SPAWN_IN_GROUPS)
                {
                    if (Plugin.configOnlyOneBarber.Value)
                        Plugin.Logger.LogWarning("Config setting \"SpawnInPairs\" has been enabled, but will be ignored as \"OnlyOneBarber\" is also enabled.");
                    else
                        spawnInGroupsOf = 2;
                }
                else
                    Plugin.Logger.LogWarning("Config setting \"SpawnInPairs\" has been enabled, but VentSpawnFix was not detected. Enemies spawning from vents in groups is unsupported by vanilla, so this setting won't work!");
            }

            EnemyType barber = __instance.currentLevel.Enemies.FirstOrDefault(enemy => enemy.enemyType?.name == "ClaySurgeon")?.enemyType;
            if (barber != null)
            {
                if (barber.spawnInGroupsOf != spawnInGroupsOf)
                {
                    Plugin.Logger.LogDebug($"ClaySurgeon.spawnInGroupsOf: {barber.spawnInGroupsOf} -> {spawnInGroupsOf}");
                    barber.spawnInGroupsOf = spawnInGroupsOf;
                }
                int maxCount = Plugin.configOnlyOneBarber.Value ? 1 : 8;
                if (barber.MaxCount != maxCount)
                {
                    Plugin.Logger.LogDebug($"ClaySurgeon.MaxCount: {barber.MaxCount} -> {maxCount}");
                    barber.MaxCount = maxCount;
                }
            }
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.Start))]
        [HarmonyPostfix]
        static void ClaySurgeonAIPostStart(ClaySurgeonAI __instance, ref float ___snareIntervalTimer)
        {
            __instance.agent.speed = 0f;
            ___snareIntervalTimer = 100f;

            GameObject meshContainer = __instance.GetComponentInChildren<EnemyAICollisionDetect>()?.gameObject;
            if (meshContainer != null && !meshContainer.GetComponent<Rigidbody>())
            {
                Rigidbody rb = meshContainer.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }

        [HarmonyPatch(typeof(ClaySurgeonAI), nameof(ClaySurgeonAI.OnCollideWithPlayer))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ClaySurgeonAITransOnCollideWithPlayer(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            codes.InsertRange(codes.Count - 1, [
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldc_R4, 0f),
                new CodeInstruction(OpCodes.Stfld, AccessTools.Field(typeof(ClaySurgeonAI), "timeSinceSnip"))
            ]);
            Plugin.Logger.LogDebug("Transpiler (ClaySurgeonAI.OnCollideWithPlayer): Set cooldown for local client immediately");

            return codes;
        }
    }

    internal class DanceClock
    {
        static bool ticking;
        static float startingInterval = 2.75f, endingInterval = 1.25f;

        internal static void Start(float start, float end)
        {
            if (ticking)
                return;

            ticking = true;
            startingInterval = start;
            endingInterval = end;
            TimeOfDay.Instance.onHourChanged.AddListener(Tick);
        }

        static void Tick()
        {
            float currentInterval = Mathf.Lerp(startingInterval, endingInterval, (float)TimeOfDay.Instance.hour / TimeOfDay.Instance.numberOfHours);
            foreach (ClaySurgeonAI barber in Object.FindObjectsOfType<ClaySurgeonAI>())
                barber.currentInterval = currentInterval;
        }

        internal static void Stop()
        {
            if (ticking)
            {
                ticking = false;
                TimeOfDay.Instance.onHourChanged.RemoveListener(Tick);
            }
        }
    }
}