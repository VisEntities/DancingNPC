/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Dancing NPC", "VisEntities", "1.2.0")]
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

        private const int LAYER_PLAYERS = Layers.Mask.Player_Server;
        private const string PREFAB_PLAYER = "assets/prefabs/player/player.prefab";

        private Dictionary<BasePlayer, Timer> _npcTimers = new Dictionary<BasePlayer, Timer>();

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

            if (string.Compare(_config.Version, "1.2.0") < 0)
            {
                _config.GearSets = defaultConfig.GearSets;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                ChatCommand = "dance",
                Gestures = new List<string>()
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
                }
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            cmd.AddChatCommand(_config.ChatCommand, this, nameof(cmdDance));
        }

        private void Unload()
        {
            CleanupSpawnedNPCAndTimers();
            _config = null;
            _plugin = null;
        }

        #endregion Oxide Hooks

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "dancingnpc.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Commands

        private void cmdDance(BasePlayer player, string cmd, string[] args)
        {
            if (player == null)
                return;

            if (!PermissionUtil.HasPermission(player, PermissionUtil.USE))
            {
                SendMessage(player, Lang.NoPermission);
                return;
            }

            string gestureName;
            if (args.Length > 0)
                gestureName = args[0];
            else
                gestureName = GetRandomGesture();

            string gearSetName;
            if (args.Length > 1)
                gearSetName = args[1];
            else
                gearSetName = GetRandomGearSet();

            if (!GearSetExists(gearSetName))
            {
                SendMessage(player, Lang.GearSetNotFound, gearSetName);
                return;
            }

            GestureConfig gestureConfig = FindGestureByName(player, gestureName);

            if (gestureConfig != null)
            {
                BasePlayer targetNPC = GetNPCInSight(player);
                if (targetNPC != null)
                {
                    UpdateNPCGesture(targetNPC, gestureConfig);
                    EquipGearSet(targetNPC, gearSetName);
                    SendMessage(player, Lang.GestureUpdatedOnExistingNPC, gestureName, gearSetName);
                }
                else
                {
                    targetNPC = SpawnNPC(player);
                    EquipGearSet(targetNPC, gearSetName);
                    StartGestureLoop(targetNPC, gestureConfig);
                    SendMessage(player, Lang.GesturePlayedOnNewNPC, gestureName, gearSetName);
                }
            }
            else
            {
                SendMessage(player, Lang.GestureNotFound, gestureName);
            }
        }

        #endregion Commands

        #region NPC Spawning and Retrieval

        private BasePlayer GetNPCInSight(BasePlayer player)
        {
            RaycastHit raycastHit;
            if (Physics.Raycast(player.eyes.HeadRay(), out raycastHit, 10f, LAYER_PLAYERS, QueryTriggerInteraction.Ignore))
            {
                BasePlayer targetNPC = raycastHit.GetEntity() as BasePlayer;
                if (targetNPC != null && _npcTimers.ContainsKey(targetNPC))
                {
                    return targetNPC;
                }
            }

            return null;
        }

        private BasePlayer SpawnNPC(BasePlayer ownerPlayer)
        {
            BasePlayer npc = GameManager.server.CreateEntity(PREFAB_PLAYER, ownerPlayer.transform.position) as BasePlayer;
            npc.Spawn();

            _npcTimers.Add(npc, null);
            return npc;
        }

        #endregion NPC Spawning and Retrieval

        #region Gesture Execution

        private void UpdateNPCGesture(BasePlayer npc, GestureConfig gestureConfig)
        {
            Timer existingTimer;
            if (_npcTimers.TryGetValue(npc, out existingTimer) && existingTimer != null)
                existingTimer.Destroy();

            StartGestureLoop(npc, gestureConfig);
        }

        private void StartGestureLoop(BasePlayer npc, GestureConfig gestureConfig)
        {
            npc.Server_StartGesture(gestureConfig);

            Timer gestureRepeatTimer = timer.Every(gestureConfig.duration, () =>
            {
                if (npc != null)
                    npc.Server_StartGesture(gestureConfig);
            });

            _npcTimers[npc] = gestureRepeatTimer;
        }

        private string GetRandomGesture()
        {
            int index = UnityEngine.Random.Range(0, _config.Gestures.Count);
            return _config.Gestures[index];
        }

        private GestureConfig FindGestureByName(BasePlayer player, string gestureName)
        {
            GestureConfig[] allGestures = player.gestureList.AllGestures;
            if (allGestures != null)
            {
                foreach (GestureConfig gesture in allGestures)
                {
                    if (gesture.convarName.Equals(gestureName, StringComparison.OrdinalIgnoreCase))
                        return gesture;
                }
            }

            return null;
        }

        #endregion Gesture Execution

        #region NPC and Timers Cleanup

        private void CleanupSpawnedNPCAndTimers()
        {
            foreach (var kvp in _npcTimers)
            {
                BasePlayer npc = kvp.Key;
                Timer timer = kvp.Value;

                if (npc != null)
                    npc.Kill();

                if (timer != null)
                    timer.Destroy();
            }

            _npcTimers.Clear();
        }

        #endregion NPC and Timers Cleanup

        #region Gear Set Equipping

        public bool EquipGearSet(BasePlayer player, string gearSetName, bool clearInventory = true)
        {
            if (!PluginLoaded(_plugin.GearCore))
                return false;

            return _plugin.GearCore.Call<bool>("EquipGearSet", player, gearSetName, clearInventory);
        }

        public bool GearSetExists(string gearSetName)
        {
            if (!PluginLoaded(_plugin.GearCore))
                return false;

            return _plugin.GearCore.Call<bool>("GearSetExists", gearSetName);
        }

        private string GetRandomGearSet()
        {
            int index = Random.Range(0, _config.GearSets.Count);
            return _config.GearSets[index];
        }

        #endregion Gear Set Equipping

        #region Helper Functions

        public static bool PluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.IsLoaded)
                return true;
            else
                return false;
        }

        #endregion Helper Functions

        #region Localization

        private class Lang
        {
            public const string NoPermission = "NoPermission";
            public const string GesturePlayedOnNewNPC = "GesturePlayedOnNewNPC";
            public const string GestureUpdatedOnExistingNPC = "GestureUpdatedOnExistingNPC";
            public const string GestureNotFound = "GestureNotFound";
            public const string GearSetNotFound = "GearSetNotFound";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.NoPermission] = "You do not have permission to use this command.",
                [Lang.GesturePlayedOnNewNPC] = "Spawned a new npc and played gesture <color=#ADFF2F>{0}</color> with gear set <color=#ADFF2F>{1}</color>.",
                [Lang.GestureUpdatedOnExistingNPC] = "Updated gesture to <color=#ADFF2F>{0}</color> on the existing npc with gear set <color=#ADFF2F>{1}</color>.",
                [Lang.GestureNotFound] = "Gesture <color=#ADFF2F>{0}</color> not found. Please specify a valid gesture.",
                [Lang.GearSetNotFound] = "Gear set <color=#ADFF2F>{0}</color> not found. Please specify a valid gear set."
            }, this, "en");
        }

        private void SendMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string message = lang.GetMessage(messageKey, this, player.UserIDString);
            if (args.Length > 0)
                message = string.Format(message, args);

            SendReply(player, message);
        }

        #endregion Localization
    }
}