/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Dancing NPC", "VisEntities", "1.4.1")]
    [Description("Allows players to spawn an npc that performs various dance gestures.")]
    public class DancingNPC : RustPlugin
    {
        #region 3rd Party Dependencies

        [PluginReference]
        private readonly Plugin GearCore;

        #endregion 3rd Party Dependencies

        #region Fields

        private static DancingNPC _plugin;
        private static Configuration _config;
        private StoredData _storedData;

        private const int LAYER_PLAYERS = Layers.Mask.Player_Server;
        private const string PREFAB_PLAYER = "assets/prefabs/player/player.prefab";

        private readonly Dictionary<BasePlayer, Timer> _npcGestureLoopTimers = new Dictionary<BasePlayer, Timer>();
        private readonly Dictionary<BasePlayer, NPCData> _spawnedNpcs = new Dictionary<BasePlayer, NPCData>();

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Chat Command")]
            public string ChatCommand { get; set; }

            [JsonProperty("Gestures")]
            public List<string> Gestures { get; set; }

            [JsonProperty("Gear Sets")]
            public List<string> GearSets { get; set; }

            [JsonProperty("Maximum Dancers Per Player")]
            public int MaximumDancersPerPlayer { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                ChatCommand = "mydancer",
                Gestures = new List<string>
                {
                    "shrug",
                    "victory",
                    "wave",
                    "cabbagepatch"
                },
                GearSets = new List<string>
                {
                    "hazmat suit",
                    "egg suit"
                },
                MaximumDancersPerPlayer = 3
            };
        }

        #endregion Configuration

        #region Stored Data

        private class StoredData
        {
            [JsonProperty("Player Npcs")]
            public Dictionary<ulong, List<NPCData>> PlayerNpcs { get; set; } = new Dictionary<ulong, List<NPCData>>();
        }

        private class NPCData
        {
            [JsonProperty("Owner Id")]
            public ulong OwnerId { get; set; }

            [JsonProperty("Position")]
            public SerializableVector3 Position { get; set; }

            [JsonProperty("Yaw")]
            public float Yaw { get; set; }

            [JsonProperty("Gesture")]
            public string GestureName { get; set; }

            [JsonProperty("Gear Set")]
            public string GearSetName { get; set; }
        }

        private class SerializableVector3
        {
            [JsonProperty("x")]
            public float X { get; set; }

            [JsonProperty("y")]
            public float Y { get; set; }

            [JsonProperty("z")]
            public float Z { get; set; }

            public SerializableVector3(Vector3 vector)
            {
                X = vector.x;
                Y = vector.y;
                Z = vector.z;
            }

            public Vector3 ToVector3()
            {
                return new Vector3(X, Y, Z);
            }
        }

        private void LoadData()
        {
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());

            if (_storedData == null)
                _storedData = new StoredData();

            if (_storedData.PlayerNpcs == null)
                _storedData.PlayerNpcs = new Dictionary<ulong, List<NPCData>>();
        }

        private void SaveData()
        {
            DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);
        }

        private void RespawnSavedNpcs()
        {
            if (_storedData == null || _storedData.PlayerNpcs == null || _storedData.PlayerNpcs.Count == 0)
                return;

            foreach (KeyValuePair<ulong, List<NPCData>> playerEntry in _storedData.PlayerNpcs)
            {
                ulong ownerId = playerEntry.Key;
                List<NPCData> npcList = playerEntry.Value;

                if (npcList == null || npcList.Count == 0)
                    continue;

                for (int i = 0; i < npcList.Count; i++)
                {
                    NPCData npcData = npcList[i];
                    if (npcData == null || npcData.Position == null)
                        continue;

                    if (npcData.OwnerId == 0)
                        npcData.OwnerId = ownerId;

                    Vector3 position = npcData.Position.ToVector3();

                    BasePlayer npc = GameManager.server.CreateEntity(PREFAB_PLAYER, position) as BasePlayer;
                    if (npc == null)
                        continue;

                    npc.Spawn();

                    _npcGestureLoopTimers[npc] = null;
                    _spawnedNpcs[npc] = npcData;

                    if (!float.IsNaN(npcData.Yaw))
                        SetNPCYaw(npc, npcData.Yaw);

                    if (!string.IsNullOrEmpty(npcData.GearSetName))
                        GearCoreUtil.EquipGearSet(npc, npcData.GearSetName);

                    if (!string.IsNullOrEmpty(npcData.GestureName))
                    {
                        GestureConfig gestureConfig = FindGestureByName(npcData.GestureName);
                        if (gestureConfig != null)
                            BeginGestureLoop(npc, gestureConfig);
                    }
                }
            }
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            LoadData();
            PermissionUtil.RegisterPermissions();

            if (!string.IsNullOrEmpty(_config.ChatCommand))
                cmd.AddChatCommand(_config.ChatCommand, this, nameof(cmdDance));
        }

        private void OnServerInitialized()
        {
            _plugin = this;

            CheckDependencies(false);
            RespawnSavedNpcs();
        }

        private void OnEntityKill(BaseNetworkable baseNetworkable)
        {
            BasePlayer npcPlayer = baseNetworkable as BasePlayer;
            if (npcPlayer == null || !npcPlayer.IsNpc)
                return;

            NPCData npcData;
            if (!_spawnedNpcs.TryGetValue(npcPlayer, out npcData) || npcData == null)
                return;

            Timer existingGestureTimer;
            if (_npcGestureLoopTimers.TryGetValue(npcPlayer, out existingGestureTimer))
            {
                if (existingGestureTimer != null)
                    existingGestureTimer.Destroy();

                _npcGestureLoopTimers.Remove(npcPlayer);
            }

            if (_storedData != null && _storedData.PlayerNpcs != null && npcData.OwnerId != 0)
            {
                List<NPCData> playerNpcList;
                if (_storedData.PlayerNpcs.TryGetValue(npcData.OwnerId, out playerNpcList) && playerNpcList != null)
                {
                    playerNpcList.Remove(npcData);

                    if (playerNpcList.Count == 0)
                        _storedData.PlayerNpcs.Remove(npcData.OwnerId);

                    SaveData();
                }
            }

            _spawnedNpcs.Remove(npcPlayer);
        }

        private void Unload()
        {
            CleanupSpawnedNPCAndTimers();
            SaveData();

            _config = null;
            _plugin = null;
        }

        #endregion Oxide Hooks

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "dancingnpc.use";

            private static readonly List<string> _all = new List<string>
            {
                USE
            };

            public static void RegisterPermissions()
            {
                for (int i = 0; i < _all.Count; i++)
                {
                    string perm = _all[i];
                    _plugin.permission.RegisterPermission(perm, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permission)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permission);
            }
        }

        #endregion Permissions

        #region Commands

        private void cmdDance(BasePlayer player, string cmd, string[] cmdArgs)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
            {
                ReplyToPlayer(player, Lang.Error_NoPermission);
                return;
            }

            if (cmdArgs == null || cmdArgs.Length == 0)
            {
                ShowHelp(player);
                return;
            }

            string firstArgument = cmdArgs[0].ToLower();

            if (firstArgument == "help")
            {
                ShowHelp(player);
                return;
            }

            if (firstArgument == "add")
            {
                HandleAddDanceCommand(player, cmdArgs);
                return;
            }

            if (firstArgument == "setdance")
            {
                HandleSetDanceCommand(player, cmdArgs);
                return;
            }

            if (firstArgument == "setgear")
            {
                HandleSetGearCommand(player, cmdArgs);
                return;
            }

            if (firstArgument == "remove")
            {
                RemoveSingleNPC(player);
                return;
            }

            if (firstArgument == "clear")
            {
                RemoveAllPlayerNPCs(player);
                return;
            }

            if (firstArgument == "dances")
            {
                ListGestures(player);
                return;
            }

            if (firstArgument == "gear")
            {
                ListGearSets(player);
                return;
            }

            ShowHelp(player);
        }

        #endregion Commands

        #region Command Helpers

        private void ShowHelp(BasePlayer player)
        {
            string baseCommand = "/" + _config.ChatCommand;

            string header = GetMessage(player, Lang.Info_HelpHeader, baseCommand);
            string usageAdd = GetMessage(player, Lang.Info_HelpUsageBase, baseCommand);
            string usageSetDance = GetMessage(player, Lang.Info_HelpUsageSetDance, baseCommand);
            string usageSetGear = GetMessage(player, Lang.Info_HelpUsageSetGear, baseCommand);
            string usageRemove = GetMessage(player, Lang.Info_HelpUsageRemove, baseCommand);
            string usageClear = GetMessage(player, Lang.Info_HelpUsageClear, baseCommand);
            string usageDances = GetMessage(player, Lang.Info_HelpUsageDances, baseCommand);
            string usageGear = GetMessage(player, Lang.Info_HelpUsageGear, baseCommand);

            string helpMessage = header + "\n"
                + " - " + usageAdd + "\n"
                + " - " + usageSetDance + "\n"
                + " - " + usageSetGear + "\n"
                + " - " + usageRemove + "\n"
                + " - " + usageClear + "\n"
                + " - " + usageDances + "\n"
                + " - " + usageGear;

            _plugin.SendReply(player, helpMessage);
        }

        private int GetPlayerDancingNpcCount(BasePlayer player)
        {
            if (player == null)
                return 0;

            ulong ownerId = player.userID;
            int count = 0;

            foreach (KeyValuePair<BasePlayer, NPCData> kvp in _spawnedNpcs)
            {
                NPCData npcData = kvp.Value;
                if (npcData != null && npcData.OwnerId == ownerId)
                    count++;
            }

            return count;
        }

        private void HandleAddDanceCommand(BasePlayer player, string[] cmdArgs)
        {
            if (_config != null && _config.MaximumDancersPerPlayer > 0)
            {
                int currentCount = GetPlayerDancingNpcCount(player);
                if (currentCount >= _config.MaximumDancersPerPlayer)
                {
                    ReplyToPlayer(player, Lang.Error_MaximumDancingNpcsReached, _config.MaximumDancersPerPlayer);
                    return;
                }
            }

            string danceName = null;
            string danceArgument = null;

            if (cmdArgs != null && cmdArgs.Length > 1)
            {
                danceArgument = cmdArgs[1];
                danceName = ResolveGestureNameFromArgument(danceArgument);
            }
            else
            {
                danceName = GetRandomGesture();
            }

            if (string.IsNullOrEmpty(danceName))
            {
                if (cmdArgs == null || cmdArgs.Length <= 1)
                {
                    ReplyToPlayer(player, Lang.Error_NoDancesInConfig);
                    return;
                }

                ReplyToPlayer(player, Lang.Error_DanceNotFound, danceArgument);
                return;
            }

            GestureConfig gestureConfig = FindGestureByName(danceName);
            if (gestureConfig == null)
            {
                ReplyToPlayer(player, Lang.Error_DanceNotFound, danceName);
                return;
            }

            string gearSetName = null;
            if (cmdArgs != null && cmdArgs.Length > 2)
                gearSetName = cmdArgs[2];
            else
                gearSetName = GearCoreUtil.GetRandomGearSet();

            BasePlayer newNpcPlayer = SpawnNPC(player, danceName, gearSetName);
            if (newNpcPlayer == null)
                return;

            FaceNPCTowardsPlayer(newNpcPlayer, player);

            if (!string.IsNullOrEmpty(gearSetName))
                GearCoreUtil.EquipGearSet(newNpcPlayer, gearSetName);

            BeginGestureLoop(newNpcPlayer, gestureConfig);
            ReplyToPlayer(player, Lang.Info_DancePlayedOnNewNPC, danceName);
        }

        private void HandleSetDanceCommand(BasePlayer player, string[] cmdArgs)
        {
            string baseCommand = "/" + _config.ChatCommand;

            if (cmdArgs == null || cmdArgs.Length <= 1)
            {
                ReplyToPlayer(player, Lang.Error_MissingDanceArgument, baseCommand);
                return;
            }

            string danceArgument = cmdArgs[1];
            string danceName = ResolveGestureNameFromArgument(danceArgument);

            if (string.IsNullOrEmpty(danceName))
            {
                ReplyToPlayer(player, Lang.Error_DanceNotFound, danceArgument);
                return;
            }

            GestureConfig gestureConfig = FindGestureByName(danceName);
            if (gestureConfig == null)
            {
                ReplyToPlayer(player, Lang.Error_DanceNotFound, danceName);
                return;
            }

            BasePlayer targetNpcPlayer = GetNPCInSight(player);
            if (targetNpcPlayer == null)
            {
                ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                return;
            }

            NPCData npcData;
            if (_spawnedNpcs.TryGetValue(targetNpcPlayer, out npcData) && npcData != null)
            {
                if (npcData.OwnerId != player.userID)
                {
                    ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                    return;
                }
            }

            ChangeGesture(targetNpcPlayer, gestureConfig);
            FaceNPCTowardsPlayer(targetNpcPlayer, player);
            ReplyToPlayer(player, Lang.Info_DanceUpdatedOnExistingNPC, danceName);
        }

        private void HandleSetGearCommand(BasePlayer player, string[] cmdArgs)
        {
            string baseCommand = "/" + _config.ChatCommand;

            if (cmdArgs == null || cmdArgs.Length <= 1)
            {
                ReplyToPlayer(player, Lang.Error_MissingGearSetArgument, baseCommand);
                return;
            }

            string gearSetName = cmdArgs[1];

            if (!GearCoreUtil.GearSetExists(gearSetName))
            {
                ReplyToPlayer(player, Lang.Error_GearSetNotFound, gearSetName);
                return;
            }

            BasePlayer targetNpcPlayer = GetNPCInSight(player);
            if (targetNpcPlayer == null)
            {
                ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                return;
            }

            NPCData npcData;
            if (_spawnedNpcs.TryGetValue(targetNpcPlayer, out npcData) && npcData != null)
            {
                if (npcData.OwnerId != player.userID)
                {
                    ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                    return;
                }
            }

            GearCoreUtil.EquipGearSet(targetNpcPlayer, gearSetName);
            FaceNPCTowardsPlayer(targetNpcPlayer, player);

            if (npcData != null)
            {
                npcData.GearSetName = gearSetName;
                SaveData();
            }

            ReplyToPlayer(player, Lang.Info_GearSetUpdatedOnExistingNPC, gearSetName);
        }

        private void RemoveSingleNPC(BasePlayer player)
        {
            BasePlayer targetNPC = GetNPCInSight(player);
            if (targetNPC == null)
            {
                ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                return;
            }

            NPCData npcData;
            if (_spawnedNpcs.TryGetValue(targetNPC, out npcData) && npcData != null)
            {
                if (npcData.OwnerId != player.userID)
                {
                    ReplyToPlayer(player, Lang.Error_NoNPCInSight);
                    return;
                }
            }

            RemoveNPC(targetNPC, true);
            ReplyToPlayer(player, Lang.Info_NPCRemoved);
        }

        private void RemoveAllPlayerNPCs(BasePlayer player)
        {
            List<BasePlayer> npcsToRemove = new List<BasePlayer>();
            bool hasAny = false;

            foreach (KeyValuePair<BasePlayer, NPCData> kvp in _spawnedNpcs)
            {
                BasePlayer npc = kvp.Key;
                NPCData npcData = kvp.Value;

                if (npcData != null && npcData.OwnerId == player.userID)
                {
                    npcsToRemove.Add(npc);
                }
            }

            for (int i = 0; i < npcsToRemove.Count; i++)
            {
                RemoveNPC(npcsToRemove[i], true);
                hasAny = true;
            }

            if (hasAny)
            {
                ReplyToPlayer(player, Lang.Info_AllNpcsRemoved);
            }
            else
            {
                ReplyToPlayer(player, Lang.Error_NoNpcsToRemove);
            }
        }

        private void ListGestures(BasePlayer player)
        {
            if (_config.Gestures == null || _config.Gestures.Count == 0)
            {
                ReplyToPlayer(player, Lang.Error_NoDancesInConfig);
                return;
            }

            List<string> formattedGestures = new List<string>();

            for (int i = 0; i < _config.Gestures.Count; i++)
            {
                int gestureNumber = i + 1;
                string gestureName = _config.Gestures[i];
                string formattedEntry = "- " + gestureNumber + " = " + gestureName;
                formattedGestures.Add(formattedEntry);
            }

            string gesturesText = string.Join("\n", formattedGestures.ToArray());
            ReplyToPlayer(player, Lang.Info_ConfigDances, gesturesText);
        }

        private void ListGearSets(BasePlayer player)
        {
            if (_config == null || _config.GearSets == null || _config.GearSets.Count == 0)
            {
                ReplyToPlayer(player, Lang.Error_NoGearSetsInConfig);
                return;
            }

            List<string> formattedGearSets = new List<string>();

            for (int i = 0; i < _config.GearSets.Count; i++)
            {
                string gearSetName = _config.GearSets[i];
                string formattedEntry = "- " + gearSetName;
                formattedGearSets.Add(formattedEntry);
            }

            string gearSetsText = string.Join("\n", formattedGearSets.ToArray());
            ReplyToPlayer(player, Lang.Info_ConfigGearSets, gearSetsText);
        }

        #endregion Command Helpers

        #region NPC Lookup and Orientation

        private BasePlayer GetNPCInSight(BasePlayer sourcePlayer)
        {
            RaycastHit raycastHit;

            bool hitPlayer = Physics.Raycast(sourcePlayer.eyes.HeadRay(), out raycastHit, 10f, LAYER_PLAYERS, QueryTriggerInteraction.Ignore);
            if (!hitPlayer)
                return null;

            BasePlayer targetNpcPlayer = raycastHit.GetEntity() as BasePlayer;
            if (targetNpcPlayer != null && _npcGestureLoopTimers.ContainsKey(targetNpcPlayer))
                return targetNpcPlayer;

            return null;
        }

        private void FaceNPCTowardsPlayer(BasePlayer npcPlayer, BasePlayer referencePlayer)
        {
            Vector3 directionToReferencePlayer = (referencePlayer.transform.position - npcPlayer.transform.position).normalized;
            Quaternion lookRotation = Quaternion.LookRotation(directionToReferencePlayer);
            Vector3 viewAngles = lookRotation.eulerAngles;

            npcPlayer.OverrideViewAngles(viewAngles);
            npcPlayer.SendNetworkUpdateImmediate();

            NPCData npcData;
            if (_spawnedNpcs.TryGetValue(npcPlayer, out npcData) && npcData != null)
            {
                npcData.Yaw = viewAngles.y;
                npcData.Position = new SerializableVector3(npcPlayer.transform.position);
                SaveData();
            }
        }

        private void SetNPCYaw(BasePlayer npcPlayer, float yawRotation)
        {
            Vector3 viewAngles = new Vector3(0f, yawRotation, 0f);
            npcPlayer.OverrideViewAngles(viewAngles);
            npcPlayer.SendNetworkUpdateImmediate();
        }

        #endregion NPC Lookup and Orientation

        #region Gesture Selection and Looping

        private void ChangeGesture(BasePlayer npcPlayer, GestureConfig gestureConfig)
        {
            Timer existingGestureTimer;
            if (_npcGestureLoopTimers.TryGetValue(npcPlayer, out existingGestureTimer) && existingGestureTimer != null)
                existingGestureTimer.Destroy();

            BeginGestureLoop(npcPlayer, gestureConfig);

            NPCData npcData;
            if (_spawnedNpcs.TryGetValue(npcPlayer, out npcData) && npcData != null)
            {
                npcData.GestureName = gestureConfig.convarName;
                SaveData();
            }
        }

        private void BeginGestureLoop(BasePlayer npcPlayer, GestureConfig gestureConfig)
        {
            npcPlayer.Server_StartGesture(gestureConfig);

            Timer gestureRepeatTimer = timer.Every(gestureConfig.duration, delegate
            {
                if (npcPlayer != null)
                    npcPlayer.Server_StartGesture(gestureConfig);
            });

            _npcGestureLoopTimers[npcPlayer] = gestureRepeatTimer;
        }

        private string GetRandomGesture()
        {
            if (_config == null || _config.Gestures == null || _config.Gestures.Count == 0)
                return null;

            int gestureIndex = UnityEngine.Random.Range(0, _config.Gestures.Count);
            return _config.Gestures[gestureIndex];
        }

        private string ResolveGestureNameFromArgument(string gestureArgument)
        {
            if (string.IsNullOrEmpty(gestureArgument))
                return null;

            int gestureIndex;
            bool parsedSuccessfully = int.TryParse(gestureArgument, out gestureIndex);
            if (parsedSuccessfully)
            {
                if (_config == null)
                    return null;

                if (_config.Gestures == null)
                    return null;

                if (gestureIndex < 1 || gestureIndex > _config.Gestures.Count)
                    return null;

                return _config.Gestures[gestureIndex - 1];
            }

            return gestureArgument;
        }

        private GestureConfig FindGestureByName(string gestureName)
        {
            if (string.IsNullOrEmpty(gestureName))
                return null;

            GestureConfig[] availableGestures = GestureCollection.Instance.AllGestures;
            if (availableGestures == null)
                return null;

            for (int gestureIndex = 0; gestureIndex < availableGestures.Length; gestureIndex++)
            {
                GestureConfig gestureConfig = availableGestures[gestureIndex];
                if (gestureConfig != null && gestureConfig.convarName.Equals(gestureName, StringComparison.OrdinalIgnoreCase))
                    return gestureConfig;
            }

            return null;
        }

        #endregion Gesture Selection and Looping

        #region NPC Spawning and Lifecycle

        private BasePlayer SpawnNPC(BasePlayer ownerPlayer, string gestureName, string gearSetName)
        {
            BasePlayer npcPlayer = GameManager.server.CreateEntity(PREFAB_PLAYER, ownerPlayer.transform.position) as BasePlayer;
            if (npcPlayer == null)
                return null;

            npcPlayer.Spawn();

            _npcGestureLoopTimers[npcPlayer] = null;

            if (_storedData == null)
                _storedData = new StoredData();

            if (_storedData.PlayerNpcs == null)
                _storedData.PlayerNpcs = new Dictionary<ulong, List<NPCData>>();

            List<NPCData> playerNpcList;
            if (!_storedData.PlayerNpcs.TryGetValue(ownerPlayer.userID, out playerNpcList) || playerNpcList == null)
            {
                playerNpcList = new List<NPCData>();
                _storedData.PlayerNpcs[ownerPlayer.userID] = playerNpcList;
            }

            NPCData npcData = new NPCData
            {
                OwnerId = ownerPlayer.userID,
                Position = new SerializableVector3(npcPlayer.transform.position),
                Yaw = npcPlayer.transform.rotation.eulerAngles.y,
                GestureName = gestureName,
                GearSetName = gearSetName
            };

            playerNpcList.Add(npcData);
            _spawnedNpcs[npcPlayer] = npcData;

            SaveData();
            return npcPlayer;
        }

        private void RemoveNPC(BasePlayer npcPlayer, bool removeFromStoredData)
        {
            if (npcPlayer == null)
                return;

            Timer existingGestureTimer;
            if (_npcGestureLoopTimers.TryGetValue(npcPlayer, out existingGestureTimer))
            {
                if (existingGestureTimer != null)
                    existingGestureTimer.Destroy();

                _npcGestureLoopTimers.Remove(npcPlayer);
            }

            NPCData npcData;
            if (removeFromStoredData && _spawnedNpcs.TryGetValue(npcPlayer, out npcData) && npcData != null)
            {
                if (_storedData != null && _storedData.PlayerNpcs != null && npcData.OwnerId != 0)
                {
                    List<NPCData> playerNpcList;
                    if (_storedData.PlayerNpcs.TryGetValue(npcData.OwnerId, out playerNpcList) && playerNpcList != null)
                    {
                        playerNpcList.Remove(npcData);

                        if (playerNpcList.Count == 0)
                            _storedData.PlayerNpcs.Remove(npcData.OwnerId);

                        SaveData();
                    }
                }
            }

            _spawnedNpcs.Remove(npcPlayer);

            if (!npcPlayer.IsDestroyed)
                npcPlayer.Kill();
        }

        private void CleanupSpawnedNPCAndTimers()
        {
            List<BasePlayer> npcPlayersToRemove = new List<BasePlayer>();

            foreach (KeyValuePair<BasePlayer, Timer> npcTimerPair in _npcGestureLoopTimers)
            {
                BasePlayer npcPlayer = npcTimerPair.Key;
                if (npcPlayer != null)
                    npcPlayersToRemove.Add(npcPlayer);
            }

            for (int npcIndex = 0; npcIndex < npcPlayersToRemove.Count; npcIndex++)
                RemoveNPC(npcPlayersToRemove[npcIndex], false);

            _npcGestureLoopTimers.Clear();
            _spawnedNpcs.Clear();
        }

        #endregion NPC Spawning and Lifecycle

        #region 3rd Party Integration

        public static class GearCoreUtil
        {
            private static bool Loaded
            {
                get
                {
                    return _plugin != null &&
                           _plugin.GearCore != null &&
                           _plugin.GearCore.IsLoaded;
                }
            }

            public static bool GearSetExists(string gearSetName)
            {
                if (!Loaded)
                    return false;

                return _plugin.GearCore.Call<bool>("GearSetExists", gearSetName);
            }

            public static bool EquipGearSet(BasePlayer player, string gearSetName, bool clearInventory = true)
            {
                if (!Loaded)
                    return false;

                return _plugin.GearCore.Call<bool>("EquipGearSet", player, gearSetName, clearInventory);
            }

            public static string GetRandomGearSet()
            {
                if (_config.GearSets == null || _config.GearSets.Count == 0)
                    return null;

                int index = UnityEngine.Random.Range(0, _config.GearSets.Count);
                return _config.GearSets[index];
            }
        }

        #endregion 3rd Party Integration

        #region Helper Functions

        private bool CheckDependencies(bool unloadIfNotFound = false)
        {
            if (!PluginLoaded(GearCore))
            {
                Puts("Gear Core is not loaded. Download it from https://game4freak.io.");

                if (unloadIfNotFound)
                    rust.RunServerCommand("oxide.unload", nameof(DancingNPC));

                return false;
            }

            return true;
        }

        public static bool PluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.IsLoaded)
                return true;

            return false;
        }

        #endregion Helper Functions

        #region Helper Classes

        public static class DataFileUtil
        {
            private const string FOLDER = "";

            public static void EnsureFolderCreated()
            {
                string path = Path.Combine(Interface.Oxide.DataDirectory, FOLDER);

                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths()
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);
                for (int i = 0; i < filePaths.Length; i++)
                {
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);
                }

                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Helper Classess

        #region Localization

        private class Lang
        {
            public const string Error_NoPermission = "Error.NoPermission";
            public const string Info_DancePlayedOnNewNPC = "Info.DancePlayedOnNewNPC";
            public const string Info_DanceUpdatedOnExistingNPC = "Info.DanceUpdatedOnExistingNPC";
            public const string Info_GearSetUpdatedOnExistingNPC = "Info.GearSetUpdatedOnExistingNPC";
            public const string Error_DanceNotFound = "Error.DanceNotFound";
            public const string Error_NoNPCInSight = "Error.NoNPCInSight";
            public const string Info_NPCRemoved = "Info.NPCRemoved";
            public const string Info_AllNpcsRemoved = "Info.AllNPCsRemoved";
            public const string Error_NoNpcsToRemove = "Error.NoNPCsToRemove";
            public const string Info_HelpHeader = "Info.HelpHeader";
            public const string Info_HelpUsageBase = "Info.HelpUsageBase";
            public const string Info_HelpUsageSetDance = "Info.HelpUsageSetDance";
            public const string Info_HelpUsageSetGear = "Info.HelpUsageSetGear";
            public const string Info_HelpUsageRemove = "Info.HelpUsageRemove";
            public const string Info_HelpUsageClear = "Info.HelpUsageClear";
            public const string Info_HelpUsageDances = "Info.HelpUsageDances";
            public const string Info_HelpUsageGear = "Info.HelpUsageGear";
            public const string Info_ConfigDances = "Info.ConfigDances";
            public const string Info_ConfigGearSets = "Info_ConfigGearSets";
            public const string Error_NoDancesInConfig = "Error_NoDancesInConfig";
            public const string Error_NoGearSetsInConfig = "Error_NoGearSetsInConfig";
            public const string Error_MissingDanceArgument = "Error_MissingDanceArgument";
            public const string Error_MissingGearSetArgument = "Error_MissingGearSetArgument";
            public const string Error_GearSetNotFound = "Error_GearSetNotFound";
            public const string Error_MaximumDancingNpcsReached = "Error_MaximumDancingNpcsReached";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.Error_NoPermission] = "You do not have permission to use this command.",
                [Lang.Info_DancePlayedOnNewNPC] = "Spawned a new dancing npc and started dance '{0}'.",
                [Lang.Info_DanceUpdatedOnExistingNPC] = "Updated the npc's dance to '{0}'.",
                [Lang.Info_GearSetUpdatedOnExistingNPC] = "Updated the npc's gear set to '{0}'.",
                [Lang.Error_DanceNotFound] = "Dance '{0}' was not found. Please specify a valid dance name or number.",
                [Lang.Error_NoNPCInSight] = "No dancing npc found. Look directly at one that belongs to you and try again.",
                [Lang.Info_NPCRemoved] = "Removed the selected dancing npc.",
                [Lang.Info_AllNpcsRemoved] = "Removed all of your dancing npcs.",
                [Lang.Error_NoNpcsToRemove] = "You do not have any active dancing npcs to remove.",
                [Lang.Info_HelpHeader] = "Dancing NPC commands ({0}):",
                [Lang.Info_HelpUsageBase] = "{0} add [dance or number] [gear set] - Spawn a new dancing npc near you with an optional dance and gear set.",
                [Lang.Info_HelpUsageSetDance] = "{0} setdance <dance or number> - Change the dance of the dancing npc you are currently looking at.",
                [Lang.Info_HelpUsageSetGear] = "{0} setgear <gear set> - Change the gear set of the dancing npc you are currently looking at.",
                [Lang.Info_HelpUsageRemove] = "{0} remove - Remove the dancing npc you are currently looking at.",
                [Lang.Info_HelpUsageClear] = "{0} clear - Remove all dancing npcs that belong to you.",
                [Lang.Info_HelpUsageDances] = "{0} dances - Show the list of available dances with their numbers.",
                [Lang.Info_HelpUsageGear] = "{0} gear - Show the list of configured gear set names.",
                [Lang.Info_ConfigDances] = "Available dances (you can use the number or name):\n{0}",
                [Lang.Info_ConfigGearSets] = "Configured gear sets:\n{0}",
                [Lang.Error_NoDancesInConfig] = "No dances are configured. Please add dance names to the plugin configuration.",
                [Lang.Error_NoGearSetsInConfig] = "No gear sets are configured. Please add gear set names to the plugin configuration.",
                [Lang.Error_MissingDanceArgument] = "Please specify which dance you want to use. Type {0} dances to see the list.",
                [Lang.Error_MissingGearSetArgument] = "Please specify which gear set you want to use. Type {0} gear to see the list.",
                [Lang.Error_GearSetNotFound] = "Gear set '{0}' was not found. Please specify a valid gear set name.",
                [Lang.Error_MaximumDancingNpcsReached] = "You already have {0} dancing npcs. Remove one before spawning another."
            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string userId;

            if (player != null)
                userId = player.UserIDString;
            else
                userId = null;

            string message = _plugin.lang.GetMessage(messageKey, _plugin, userId);

            if (args != null && args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void ReplyToPlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}