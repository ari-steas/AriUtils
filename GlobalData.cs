using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;

namespace AriUtils
{
    public static partial class GlobalData
    {
        /// <summary>
        /// Kill switch for the entire mod
        /// </summary>
        public static bool Killswitch = true;
        public const ushort ServerNetworkId = 15289;
        public const ushort DataNetworkId = 15288;
        public const ushort ClientNetworkId = 15287;
        public static int MainThreadId = -1;
        public static double SyncRange => MyAPIGateway.Session.SessionSettings.SyncDistance;
        public static double SyncRangeSq => (double) MyAPIGateway.Session.SessionSettings.SyncDistance * MyAPIGateway.Session.SessionSettings.SyncDistance;
        public static readonly Guid LogicSettingsGuid = new Guid("b4e33a2c-0406-4aea-bf0a-d1ad04266a14");
        public static readonly Guid SensorSettingsGuid = new Guid("ed7fde7f-c8a4-4c1b-9c07-cfd31aa0226e");
        public static readonly Guid PersistentBlockIdGuid = new Guid("385ace88-f770-4241-a02c-af63e0851c06");
        public static List<IMyPlayer> Players = new List<IMyPlayer>();
        public static IMyModContext ModContext;
        public static int DebugLevel = 0;
        public static List<MyPlanet> Planets = new List<MyPlanet>();
        public static HudState HudVisible = (HudState) (MyAPIGateway.Session?.Config?.HudState ?? 1);
        public static Action<HudState> OnHudVisibleChanged = null;
        public static Random Random = new Random();


        public static readonly MyDefinitionId ElectricityId = new MyDefinitionId(typeof(MyObjectBuilder_GasProperties), "Electricity");
        public static readonly MyDefinitionId HydrogenId = new MyDefinitionId(typeof(MyObjectBuilder_GasProperties), "Hydrogen");

        #region General Config

        private static IniConfig _generalConfig = new IniConfig(
            IniConfig.FileLocation.WorldStorage,
            "config.ini",
            "General Config",
            " Skytech Engines World Settings\n\n Set config values below,\n   then restart the world.\n Delete a line to reset it to default.\n ");

        #endregion

        internal static bool CheckShouldLoad(IMyModContext myModContext, Func<string, bool> modCheck)
        {
            foreach (var mod in MyAPIGateway.Session.Mods)
            {
                if (mod.GetModContext().ModPath == myModContext.ModPath)
                    continue;

                string modIdFormatted = mod.GetModContext().ModId.RemoveChars(' ').ToLower();
                if (modCheck.Invoke(modIdFormatted))
                {
                    Killswitch = true;
                    MyLog.Default.WriteLineAndConsole($"[{GlobalData.FriendlyModName}] Found local mod version \"{mod.GetPath()}\" - cancelling init and disabling mod. My ModId: {myModContext.ModId}");
                    return false;
                }
            }

            Killswitch = false;
            return true;
        }

        public static bool IsReady = false;
        internal static void Init(IMyModContext myModContext)
        {
            Log.Info("GlobalData", "Start initialize...");
            Log.IncreaseIndent();

            if (MyAPIGateway.Session.IsServer)
            {
                _generalConfig.ReadSettings();
                _generalConfig.WriteSettings();
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(DataNetworkId, ServerMessageHandler);
                IsReady = true;
            }
            else if (!IsReady)
            {
                Log.Info("GlobalData", "Reading config data from network. Default configs will temporarily be used.");
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(DataNetworkId, ClientMessageHandler);
                MyAPIGateway.Multiplayer.SendMessageToServer(DataNetworkId, Array.Empty<byte>());
            }

            {
                ModContext = myModContext;
                string modId = ModContext.ModId.Replace(".sbm", "");
                long discard;

                Log.Info("GlobalData", "ModContext:\n" +
                                       $"\tName: {ModContext.ModName}\n" +
                                       $"\tItem: {(long.TryParse(modId, out discard) ? "https://steamcommunity.com/workshop/filedetails/?id=" : "LocalMod ")}{modId}\n" +
                                       $"\tService: {ModContext.ModServiceName} (if this isn't steam, please report the mod)");
            }

            {
                MainThreadId = Environment.CurrentManagedThreadId;
                Log.Info("GlobalData", $"Main thread ID: {MainThreadId}");
            }

            MyAPIGateway.Entities.OnEntityAdd += OnEntityAdd;
            MyAPIGateway.Entities.GetEntities(null, e =>
            {
                OnEntityAdd(e);
                return false;
            });

            Log.DecreaseIndent();
            Log.Info("GlobalData", "Initial values set.");
            IsReady = true;
        }

        private static void ServerMessageHandler(ushort channelId, byte[] serialized, ulong senderSteamId, bool isSenderServer)
        {
            try
            {
                Log.Info("GlobalData", $"Received data request from {senderSteamId}.");
                if (isSenderServer)
                    return;

                var file = _generalConfig.ReadFile();

                MyAPIGateway.Multiplayer.SendMessageTo(DataNetworkId, MyAPIGateway.Utilities.SerializeToBinary(file), senderSteamId);
            }
            catch (Exception ex)
            {
                Log.Exception("GlobalData", ex, true);
            }
        }

        private static void ClientMessageHandler(ushort channelId, byte[] serialized, ulong senderSteamId, bool isSenderServer)
        {
            if (!isSenderServer)
                return;

            try
            {
                var data = MyAPIGateway.Utilities.SerializeFromBinary<string>(serialized);
                if (data == null)
                {
                    Log.Info("GlobalData", "Null message!");
                    return;
                }

                Log.Info("GlobalData",
                    $"Reading settings data from network:\n===========================================\n\n{data}\n===========================================\n");

                var ini = new MyIni();
                if (!ini.TryParse(data))
                {
                    Log.Info("GlobalData", "Failed to read settings data!");
                    return;
                }

                foreach (var setting in _generalConfig.AllSettings)
                    setting.Read(ini, _generalConfig.SectionName);

                // Can't unregister network handlers inside a network handler call
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(DataNetworkId, ClientMessageHandler);
                });
            }
            catch (Exception ex)
            {
                Log.Exception("GlobalData", ex, true);
            }
        }

        internal static void Update()
        {
            if (MyAPIGateway.Session.GameplayFrameCounter % 10 == 0)
            {
                Players.Clear();
                MyAPIGateway.Multiplayer.Players.GetPlayers(Players);
            }

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                if (MyAPIGateway.Input.IsNewKeyPressed(MyKeys.Tab))
                {
                    UpdateVisible(MyAPIGateway.Session?.Config?.HudState ?? 1);
                }
            }
        }

        internal static void Unload()
        {
            MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdd;
            Players = null;
            Planets = null;
            if (MyAPIGateway.Session.IsServer)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(DataNetworkId, ServerMessageHandler);
            else
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(DataNetworkId, ClientMessageHandler);
            _generalConfig = null;
            Log.Info("GlobalData", "Data cleared.");
        }

        private static void OnEntityAdd(IMyEntity entity)
        {
            var planet = entity as MyPlanet;
            if (planet != null)
                Planets.Add(planet);
        }

        private static void UpdateVisible(int visible)
        {
            HudVisible = (HudState) visible;
            OnHudVisibleChanged?.Invoke(HudVisible);
        }

        public enum HudState
        {
            Hidden = 0,
            VisibleDesc = 1,
            VisibleNoDesc = 2,
        }
    }
}
