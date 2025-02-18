﻿using BepInEx;
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
    [BepInDependency(GUID_VENT_SPAWN_FIX, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(GUID_LOBBY_COMPATIBILITY, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PLUGIN_GUID = "butterystancakes.lethalcompany.barberfixes", PLUGIN_NAME = "Barber Fixes", PLUGIN_VERSION = "1.3.0";

        const string GUID_VENT_SPAWN_FIX = "butterystancakes.lethalcompany.ventspawnfix",
                     GUID_LOBBY_COMPATIBILITY = "BMX.LobbyCompatibility";

        internal static new ManualLogSource Logger;

        internal static ConfigEntry<bool> configDrumrollFromAll, configApplySpawningSettings;
        internal static ConfigEntry<int> configMaxCount, configSpawnInGroupsOf;

        internal static bool CAN_SPAWN_IN_GROUPS;

        void Awake()
        {
            Logger = base.Logger;

            if (Chainloader.PluginInfos.ContainsKey(GUID_VENT_SPAWN_FIX))
            {
                CAN_SPAWN_IN_GROUPS = true;
                Logger.LogInfo("CROSS-COMPATIBILITY - VentSpawnFix detected");
            }

            if (Chainloader.PluginInfos.ContainsKey(GUID_LOBBY_COMPATIBILITY))
            {
                Logger.LogInfo("CROSS-COMPATIBILITY - Lobby Compatibility detected");
                LobbyCompatibility.Init();
            }

            configApplySpawningSettings = Config.Bind(
                "Spawning",
                "ApplySpawningSettings",
                false,
                "The rest of the \"Spawning\" section's settings are only applied if this is enabled. You should enable this only if you aren't using something else to configure enemy variables! (i.e. Clay Surgeon Overhaul, Lethal Quantities)");

            configMaxCount = Config.Bind(
                "Spawning",
                "MaxCount",
                1,
                new ConfigDescription(
                    "(Host only) How many Barbers are allowed to spawn?\nIn v62+, this is set to 1. In v55-v61, this was set to 8.",
                    new AcceptableValueRange<int>(1, 20)));

            configSpawnInGroupsOf = Config.Bind(
                "Spawning",
                "SpawnInGroupsOf",
                1,
                new ConfigDescription(
                    "(Host only) When a Barber spawns, should additional Barbers attempt to spawn?\nIn v56+, this is set to 1. In v55, this was set to 2.\nNOTE: This REQUIRES VentSpawnFix to work!",
                    new AcceptableValueRange<int>(1, 4)));

            configDrumrollFromAll = Config.Bind(
                "Music",
                "DrumrollFromAll",
                false,
                "If true, all Barbers will play the drumroll audio before they \"jump\". If false, only the master Barber will drumroll.\nThis is false in vanilla, although whether that's by design or a bug is unclear.");

            // migrate old config
            bool onlyOneBarber = Config.Bind("Spawning", "OnlyOneBarber", true, "Legacy setting, doesn't work").Value;
            if (!onlyOneBarber && configMaxCount.Value == 1)
                configMaxCount.Value = 8;
            bool spawnInPairs = Config.Bind("Spawning", "SpawnInPairs", false, "Legacy setting, doesn't work").Value;
            if (spawnInPairs && configSpawnInGroupsOf.Value == 1)
                configSpawnInGroupsOf.Value = 2;
            Config.Remove(Config["Spawning", "OnlyOneBarber"].Definition);
            Config.Remove(Config["Spawning", "SpawnInPairs"].Definition);
            Config.Save();

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

            ClaySurgeonAI[] barbers = Object.FindObjectsByType<ClaySurgeonAI>(FindObjectsSortMode.None);

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
                        Plugin.Logger.LogDebug("Transpiler (ListenToMasterSurgeon): Don't destroy \"musicAudio2\" (causes NRE)");
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
                    Plugin.Logger.LogDebug("Transpiler (SyncMasterClaySurgeonClientRpc): Don't destroy \"musicAudio2\" (causes NRE)");
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

        [HarmonyPatch(typeof(RoundManager), "AdvanceHourAndSpawnNewBatchOfEnemies")]
        [HarmonyPrefix]
        static void PreAdvanceHourAndSpawnNewBatchOfEnemies(RoundManager __instance)
        {
            if (!__instance.IsServer || !Plugin.configApplySpawningSettings.Value)
                return;

            EnemyType barber = __instance.currentLevel.Enemies.FirstOrDefault(enemy => enemy.enemyType?.name == "ClaySurgeon")?.enemyType;

            // fallbacks (custom moons)
            if (barber == null)
            {
                barber = __instance.currentLevel.OutsideEnemies.FirstOrDefault(enemy => enemy.enemyType?.name == "ClaySurgeon")?.enemyType ?? __instance.currentLevel.DaytimeEnemies.FirstOrDefault(enemy => enemy.enemyType?.name == "ClaySurgeon")?.enemyType;

                if (barber != null && !Plugin.CAN_SPAWN_IN_GROUPS)
                {
                    // suppress warnings because user has a moon that spawns barbers outside
                    Plugin.CAN_SPAWN_IN_GROUPS = true;
                }
            }

            if (barber == null)
                return;

            int spawnInGroupsOf = Plugin.configSpawnInGroupsOf.Value;
            if (spawnInGroupsOf > 1 && !Plugin.CAN_SPAWN_IN_GROUPS)
                Plugin.Logger.LogWarning("Config setting \"SpawnInGroupsOf\" is greater than 1, but VentSpawnFix was not detected. Enemies spawning from vents in groups is unsupported by vanilla, so this setting won't work!");
            else if (Plugin.configMaxCount.Value < spawnInGroupsOf)
            {
                Plugin.Logger.LogWarning("Config setting \"SpawnInGroupsOf\" exceeds \"MaxCount\" and will be capped.");
                spawnInGroupsOf = Plugin.configMaxCount.Value;
            }

            if (barber.spawnInGroupsOf != spawnInGroupsOf)
            {
                Plugin.Logger.LogDebug($"ClaySurgeon.spawnInGroupsOf: {barber.spawnInGroupsOf} -> {spawnInGroupsOf}");
                barber.spawnInGroupsOf = spawnInGroupsOf;
            }
            int maxCount = Plugin.configMaxCount.Value;
            if (barber.MaxCount != maxCount)
            {
                Plugin.Logger.LogDebug($"ClaySurgeon.MaxCount: {barber.MaxCount} -> {maxCount}");
                barber.MaxCount = maxCount;
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

    public class DanceClock
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

        public static void Tick()
        {
            float currentInterval = Mathf.Lerp(startingInterval, endingInterval, (float)TimeOfDay.Instance.hour / TimeOfDay.Instance.numberOfHours);
            foreach (ClaySurgeonAI barber in Object.FindObjectsByType<ClaySurgeonAI>(FindObjectsSortMode.None))
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