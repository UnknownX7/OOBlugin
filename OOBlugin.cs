using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin;
using ImGuiNET;

namespace OOBlugin
{
    public class OOBlugin : IDalamudPlugin
    {
        public string Name => "OOBlugin";
        public static OOBlugin Plugin { get; private set; }
        //public static Configuration Config { get; private set; }

        private readonly bool pluginReady = false;

        private readonly Stopwatch timer = new();
        private int fpsLock = 0;
        private float fpsLockTime = 0;

        private bool sentKey = false;
        private bool sentShift = false;
        private bool sentCtrl = false;
        private bool sentAlt = false;

        private static readonly List<string> quickExecuteQueue = new();
        private static Dictionary<uint, string> usables;
        private static float walkTime = 0;

        public OOBlugin(DalamudPluginInterface pluginInterface)
        {
            Plugin = this;
            DalamudApi.Initialize(this, pluginInterface);

            //Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
            //Config.Initialize();

            DalamudApi.Framework.Update += Update;

            Game.Initialize();

            Task.Run(async () =>
            {
                while (!DalamudApi.DataManager.IsDataReady)
                    await Task.Delay(1000);
                usables = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.Item>().Where(i => i.ItemAction.Row > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower())
                    .Concat(DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.EventItem>().Where(i => i.Action.Row > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower()))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            });

            pluginReady = true;
        }

        [Command("/useitem")]
        [HelpMessage("Uses an item by name or ID.")]
        private void OnUseItem(string command, string argument)
        {
            if (usables == null) return;

            if (!uint.TryParse(argument, out var id))
            {
                if (!string.IsNullOrWhiteSpace(argument))
                {
                    var name = argument.Replace("\uE03C", ""); // Remove HQ Symbol
                    var useHQ = argument != name;
                    name = name.ToLower().Trim(' ');
                    try { id = usables.First(i => i.Value == name).Key + (uint)(useHQ ? 1_000_000 : 0); }
                    catch { }
                }
            }
            else
            {
                if (!usables.ContainsKey(id is >= 1_000_000 and < 2_000_000 ? id - 1_000_000 : id))
                    id = 0;
            }

            if (id > 0)
                Game.UseItem(Game.itemContextMenuAgent, id, 9999, 0, 0);
            else
                PrintError("Invalid item.");
        }

        [Command("/freezegame")]
        [Aliases("/frz")]
        [HelpMessage("Freezes the game for the amount of time specified in seconds, up to 60. Defaults to 0.5.")]
        private void OnFreezeGame(string command, string argument)
        {
            if (!float.TryParse(argument, out var time))
                time = 0.5f;
            Thread.Sleep((int)(Math.Min(time, 60) * 1000));
        }

        [Command("/proc")]
        [HelpMessage("Starts a process at the specified path.")]
        private void OnProc(string command, string argument)
        {
            if (Regex.IsMatch(argument, @"^.:\\"))
                Process.Start(new ProcessStartInfo
                {
                    FileName = argument,
                    WorkingDirectory = Path.GetDirectoryName(argument)!,
                    UseShellExecute = true
                });
            else
                PrintError("Command must start with \"?:\\\" where ? is a drive letter.");
        }

        [Command("/capfps")]
        [HelpMessage("Caps the FPS for a specified amount of time. Usage: \"/capfps 60 2.5\" -> Lock fps to 60 for 2.5s.")]
        private void OnCapFPS(string command, string argument)
        {
            var reg = Regex.Match(argument, @"^([0-9]+) ([0-9]*\.?[0-9]+)$");
            if (reg.Success)
            {
                _ = int.TryParse(reg.Groups[1].Value, out fpsLock);
                _ = float.TryParse(reg.Groups[2].Value, out fpsLockTime);
            }
            else
                PrintError("Invalid usage.");
        }

        [Command("/qexec")]
        [HelpMessage("Executes all commands in a single frame. Usage: \"/qexec /echo Hello\" > \"/qexec /echo there!\" > \"/qexec\".")]
        private void OnQuickExecute(string command, string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                foreach (var cmd in quickExecuteQueue)
                    ExecuteCommand(cmd);
                quickExecuteQueue.Clear();
            }
            else
                quickExecuteQueue.Add(argument);
        }

        [Command("/sendkey")]
        [HelpMessage("Sends a key to the game using virtual key code or virtual key name." +
            " Example: \"/sendkey 96\" to send numpad 0, \"/sendkey +D1\" to send shift + 1, \"/sendkey ^numpad5\" to send control + numpad 5, \"/sendkey %%OemMinus\" to send alt + minus." +
            " Use \"/sendkey help\" for all possible values.")]
        private void OnSendKey(string command, string argument)
        {
            var reg = Regex.Match(argument, @"^([+^%]*)(.+)");
            if (!reg.Success) return;

            if (argument == "help")
            {
                foreach (var name in Enum.GetNames(typeof(Keys)))
                {
                    var vk = (int)Enum.Parse(typeof(Keys), name);
                    if (Game.GetKeyStateIndex(vk) > 0)
                        PrintEcho($"{name} = {vk}");
                }
                return;
            }

            sentKey = true;
            var mods = reg.Groups[1].Value;
            var keyStr = reg.Groups[2].Value;
            if (mods.Contains("+"))
            {
                sentShift = true;
                Game.SendKeyHold(Keys.ShiftKey);
            }

            if (mods.Contains("^"))
            {
                sentCtrl = true;
                Game.SendKeyHold(Keys.ControlKey);
            }

            if (mods.Contains("%"))
            {
                sentAlt = true;
                Game.SendKeyHold(Keys.Menu);
            }

            if (!byte.TryParse(keyStr, out var key) && Enum.TryParse(typeof(Keys), keyStr, true, out var keyEnum))
                key = (byte)(int)keyEnum;

            if (!Game.SendKey(key))
                PrintError("Invalid key.");
        }

        [Command("/walk")]
        [HelpMessage("Toggles RP walk, alternatively, you can specify an amount of time in seconds to walk for.")]
        private void OnWalk(string command, string argument)
        {
            if (!float.TryParse(argument, out walkTime))
                Game.IsWalking ^= true;
            else
                Game.IsWalking = true;
        }

        [Command("/ng+t")]
        [HelpMessage("Toggles New Game+.")]
        private unsafe void OnNGPT(string command, string argument)
        {
            Game.newGameUIPtr = (Game.newGameUIPtr != IntPtr.Zero) ? Game.newGameUIPtr : DalamudApi.GameGui.FindAgentInterface("QuestRedo") + 0xA8;
            if (Game.newGameUIPtr == IntPtr.Zero) { PrintError("Failed to get NG+ agent, please open the NG+ window and then use this command to initialize it."); return; }

            *(byte*)(Game.newGameUIPtr + 0x8) ^= 1;
            Game.NewGamePlusAction(Game.GetUnknownNGPPtr(), Game.newGameUIPtr);
        }

        [Command("/doemote")]
        [HelpMessage("Performs the specified emote by number.")]
        private void OnDoEmote(string command, string argument)
        {
            Game.emoteAgent = (Game.emoteAgent != IntPtr.Zero) ? Game.emoteAgent : DalamudApi.GameGui.FindAgentInterface("Emote");
            if (Game.emoteAgent == IntPtr.Zero) { PrintError("Failed to get emote agent, please open the emote window and then use this command to initialize it."); return; }

            if (uint.TryParse(argument, out var emote))
                Game.DoEmote(Game.emoteAgent, emote, 0, true, true);
            else
                PrintError("Emote must be specified by a number.");
        }

        [Command("/leaveduty")]
        [HelpMessage("Opens the abandon duty prompt, use YesAlready to make it instant.")]
        private void OnLeaveDuty(string command, string argument)
        {
            Game.contentsFinderMenuAgent = (Game.contentsFinderMenuAgent != IntPtr.Zero) ? Game.contentsFinderMenuAgent : DalamudApi.GameGui.FindAgentInterface("ContentsFinderMenu");
            if (Game.contentsFinderMenuAgent == IntPtr.Zero) { PrintError("Failed to get duty finder agent, please open the duty finder window and then use this command to initialize it."); return; }
            Game.OpenAbandonDuty(Game.contentsFinderMenuAgent);
        }

        [Command("/qac")]
        [HelpMessage("/ac but it can queue. Use it with the action ID to force normally unqueueable actions into the queue (e.g. \"/qac 3\" = Sprint), however this method does not support target arguments (<f>, <tt>, <mo>, etc).")]
        private void OnQueueAction(string command, string argument)
        {
            if (Game.actionCommandRequestTypePtr == IntPtr.Zero) return;

            if (!uint.TryParse(argument, out var id))
            {
                Game.ActionCommandRequestType = 0;
                ExecuteCommand("/ac " + argument.Replace(" ", "\"").Replace(" ", "\"")); // big mood (auto-translate)
                Game.ActionCommandRequestType = 2;
            }
            else if (!Game.IsQueued)
            {
                Game.IsQueued = true;
                Game.QueuedActionType = 1;
                Game.QueuedAction = id;
                Game.QueuedTarget = DalamudApi.TargetManager.Target is {} actor ? actor.ObjectId : 0;
                Game.QueuedUseType = 0;
                Game.QueuedPVPAction = 0;
            }
        }

        public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[OOBlugin] {message}");
        public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[OOBlugin] {message}");

        private void Update(Framework framework)
        {
            if (!pluginReady) return;

            if (!sentKey)
            {
                if (sentShift)
                {
                    Game.SendKeyRelease(Keys.ShiftKey);
                    sentShift = false;
                }
                if (sentCtrl)
                {
                    Game.SendKeyRelease(Keys.ControlKey);
                    sentCtrl = false;
                }
                if (sentAlt)
                {
                    Game.SendKeyRelease(Keys.Menu);
                    sentAlt = false;
                }
            }
            else
                sentKey = false;

            if (walkTime > 0 && (walkTime -= ImGui.GetIO().DeltaTime) <= 0)
                Game.IsWalking = false;

            if (fpsLockTime > 0 && fpsLock > 0)
            {
                var wantedMS = 1.0f / fpsLock * 1000;
                timer.Stop();
                var elapsedMS = timer.ElapsedTicks / 10000f;
                var sleepTime = Math.Max(wantedMS - elapsedMS, 0);
                Thread.Sleep((int)sleepTime);
                fpsLockTime -= (sleepTime + elapsedMS) / 1000;
            }
            timer.Restart();
        }

        private void ExecuteCommand(string command)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(command + "\0");
                var memStr = Marshal.AllocHGlobal(0x18 + bytes.Length);

                Marshal.WriteIntPtr(memStr, memStr + 0x18); // String pointer
                Marshal.WriteInt64(memStr + 0x8, bytes.Length); // Byte capacity (unused)
                Marshal.WriteInt64(memStr + 0x10, bytes.Length); // Byte length
                Marshal.Copy(bytes, 0, memStr + 0x18, bytes.Length); // String

                Game.ProcessChatBox(Game.uiModule, memStr, IntPtr.Zero, 0);

                Marshal.FreeHGlobal(memStr);
            }
            catch { PrintError("Failed injecting command"); }
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            //Config.Save();

            DalamudApi.Framework.Update -= Update;

            Game.Dispose();

            DalamudApi.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
