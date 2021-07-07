using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq;
using Dalamud.Game.Internal.Network;
using ImGuiNET;
using Dalamud.Plugin;
using Dalamud.Hooking;
using OOBlugin.Attributes;

[assembly: AssemblyTitle("OOBlugin")]
[assembly: AssemblyVersion("1.1.0.2")]

namespace OOBlugin
{
    public class OOBlugin : IDalamudPlugin
    {
        public string Name => "OOBlugin";

        public static DalamudPluginInterface Interface { get; private set; }
        private PluginCommandManager<OOBlugin> commandManager;
        public static Configuration Config { get; private set; }
        public static OOBlugin Plugin { get; private set; }
        private PluginUI ui;

        private bool pluginReady = false;

        private readonly Stopwatch timer = new Stopwatch();
        private int fpsLock = 0;
        private float fpsLockTime = 0;

        // Command Execution
        private delegate void ProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unused, byte a4);
        private ProcessChatBoxDelegate ProcessChatBox;
        private static IntPtr uiModule = IntPtr.Zero;

        private readonly List<string> quickExecuteQueue = new List<string>();

        private bool sentKey = false;
        private bool sendShift = false;
        private bool sendCtrl = false;
        private bool sendAlt = false;

        private IntPtr walkingBoolPtr = IntPtr.Zero;
        private float walkTime = 0;
        private unsafe bool IsWalking
        {
            get => walkingBoolPtr != IntPtr.Zero && *(bool*)walkingBoolPtr;
            set
            {
                if (walkingBoolPtr != IntPtr.Zero)
                {
                    *(bool*)walkingBoolPtr = value;
                    *(bool*)(walkingBoolPtr - 0x10B) = value; // Autorun
                }
            }
        }

        private bool enableAlternatePhysics = false;
        private IntPtr alternatePhysicsBoolPtr = IntPtr.Zero;
        private unsafe bool IsAlternatePhysics
        {
            get => alternatePhysicsBoolPtr != IntPtr.Zero && *(bool*)alternatePhysicsBoolPtr;
            set
            {
                if (alternatePhysicsBoolPtr != IntPtr.Zero)
                    *(bool*)alternatePhysicsBoolPtr = value;
            }
        }

        private delegate IntPtr GetModuleDelegate(IntPtr basePtr);

        private IntPtr newGameUIPtr = IntPtr.Zero;
        private delegate IntPtr GetUnknownNGPPtrDelegate();
        private GetUnknownNGPPtrDelegate GetUnknownNGPPtr;
        private delegate void NewGamePlusActionDelegate(IntPtr a1, IntPtr a2);
        private NewGamePlusActionDelegate NewGamePlusAction;

        private IntPtr emoteAgent = IntPtr.Zero;
        private delegate void DoEmoteDelegate(IntPtr agent, uint emoteID, long a3, bool a4, bool a5);
        private DoEmoteDelegate DoEmote;

        private IntPtr contentsFinderMenuAgent = IntPtr.Zero;
        private delegate void OpenAbandonDutyDelegate(IntPtr agent);
        private OpenAbandonDutyDelegate OpenAbandonDuty;

        private IntPtr itemContextMenuAgent = IntPtr.Zero;
        private delegate void UseItemDelegate(IntPtr itemContextMenuAgent, uint itemID, uint inventoryPage, uint inventorySlot, short a5);
        private UseItemDelegate UseItem;
        private Dictionary<uint, string> usables;

        public void Initialize(DalamudPluginInterface p)
        {
            Plugin = this;
            Interface = p;

            Config = (Configuration)Interface.GetPluginConfig() ?? new Configuration();
            Config.Initialize(Interface);

            Interface.Framework.OnUpdateEvent += Update;

            ui = new PluginUI();
            Interface.UiBuilder.OnBuildUi += Draw;

            commandManager = new PluginCommandManager<OOBlugin>(this, Interface);

            ExtendSendKeys();
            InitializePointers();

            Task.Run(async () =>
            {
                while (!Interface.Data.IsDataReady)
                    await Task.Delay(1000);
                usables = Interface.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.Item>(Interface.ClientState.ClientLanguage).Where(i => i.ItemAction.Row > 0).ToDictionary(i => i.RowId, i => i.Name.ToString().ToLower());
            });

            pluginReady = true;
        }

        private static void ExtendSendKeys()
        {
            // Add to SendKeys using reflection
            var info = typeof(SendKeys).GetField("keywords", BindingFlags.Static | BindingFlags.NonPublic);
            var oldKeys = (Array)info.GetValue(null);
            if (oldKeys.Length == 49)
            {
                var elementType = oldKeys.GetType().GetElementType();
                var newKeys = Array.CreateInstance(elementType, oldKeys.Length + 10 + 172);
                Array.Copy(oldKeys, newKeys, oldKeys.Length);
                var ind = 0;
                for (int i = 0; i < 10; i++)
                {
                    var newItem = Activator.CreateInstance(elementType, "NUM" + i, (int)Keys.NumPad0 + i);
                    newKeys.SetValue(newItem, oldKeys.Length + ind++);
                }
                for (int i = 0; i < 255; i++)
                {
                    if (Enum.IsDefined(typeof(Keys), i))
                    {
                        var newItem = Activator.CreateInstance(elementType, i.ToString(), i);
                        newKeys.SetValue(newItem, oldKeys.Length + ind++);
                    }
                }
                info.SetValue(null, newKeys);
            }
        }

        private unsafe void InitializePointers()
        {
            try
            {
                uiModule = Interface.Framework.Gui.GetUIModule();
                ProcessChatBox = Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(Interface.TargetModuleScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9"));
            }
            catch { PrintError("Failed to load /qexec"); }

            try { walkingBoolPtr = Interface.TargetModuleScanner.GetStaticAddressFromSig("88 83 33 05 00 00"); } // also found at g_PlayerMoveController+523
            catch { PrintError("Failed to load /walk"); }

            try
            {
                var GetAgentModule = Marshal.GetDelegateForFunctionPointer<GetModuleDelegate>(*((IntPtr*)(*(IntPtr*)uiModule) + 34));
                var agentModule = GetAgentModule(uiModule);
                IntPtr GetAgentByInternalID(int id) => *(IntPtr*) (agentModule + 0x20 + id * 0x8); // Client::UI::Agent::AgentModule_GetAgentByInternalID, not going to sig a function this small

                try
                {
                    GetUnknownNGPPtr = Marshal.GetDelegateForFunctionPointer<GetUnknownNGPPtrDelegate>(Interface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? 80 7B 29 01"));
                    NewGamePlusAction = Marshal.GetDelegateForFunctionPointer<NewGamePlusActionDelegate>(Interface.TargetModuleScanner.ScanText("48 89 5C 24 08 48 89 74 24 18 57 48 83 EC 30 48 8B 02 48 8B DA 48 8B F9 48 8D 54 24 48 48 8B CB"));
                    newGameUIPtr = GetAgentByInternalID(333) + 0xA8;
                }
                catch { PrintError("Failed to load /ng+t"); }

                try
                {
                    DoEmote = Marshal.GetDelegateForFunctionPointer<DoEmoteDelegate>(Interface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? B8 0A 00 00 00"));
                    emoteAgent = GetAgentByInternalID(19);
                }
                catch { PrintError("Failed to load /doemote"); }

                try
                {
                    OpenAbandonDuty = Marshal.GetDelegateForFunctionPointer<OpenAbandonDutyDelegate>(Interface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? EB 90 48 8B CB"));
                    contentsFinderMenuAgent = GetAgentByInternalID(222);
                }
                catch { PrintError("Failed to load /leaveduty"); }

                try
                {
                    UseItem = Marshal.GetDelegateForFunctionPointer<UseItemDelegate>(Interface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 41 B0 01 BA 13 00 00 00"));
                    itemContextMenuAgent = GetAgentByInternalID(10);
                }
                catch { PrintError("Failed to load /useitem"); }
            }
            catch { PrintError("Failed to get agent module"); }

            try { alternatePhysicsBoolPtr = Interface.TargetModuleScanner.GetStaticAddressFromSig("40 38 35 ?? ?? ?? ?? 74 16"); } // 75 44 40 38 35 ?? ?? ?? ??
            catch { PrintError("Failed to load /alternatephysics"); }
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
                if (!usables.ContainsKey(id > 1_000_000 ? id - 1_000_000 : id))
                    id = 0;
            }

            if (id > 0)
                UseItem(itemContextMenuAgent, id, 9999, 0, 0);
            else
                PrintError("Invalid item.");
        }

        [Command("/freezegame")]
        [HelpMessage("Freezes the game for the amount of time specified in seconds, up to 60. Defaults to 0.5.")]
        private void OnFreezeGame(string command, string argument)
        {
            if (!float.TryParse(argument, out var time))
                time = 0.5f;
            Thread.Sleep((int)(Math.Min(time, 60) * 1000));
        }

        [Command("/frz")]
        [HelpMessage("Alias of \"/freezegame\".")]
        private void OnFrz(string command, string argument) => OnFreezeGame(command, argument);

        [Command("/proc")]
        [HelpMessage("Starts a process at the specified path.")]
        private void OnProc(string command, string argument)
        {
            if (Regex.IsMatch(argument, @"^.:\\"))
                Process.Start(argument);
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
                int.TryParse(reg.Groups[1].Value, out fpsLock);
                float.TryParse(reg.Groups[2].Value, out fpsLockTime);
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
        [HelpMessage("Sends a keypress to the game. Example: \"/sendkey {num0}\" to send numpad 0, see https://docs.microsoft.com/en-us/dotnet/api/system.windows.forms.sendkeys.send for more information on format. Allows for the virtual key code instead of a key name as well, so that \"/sendkey {96}\" will also send numpad 0.")]
        private void OnSendKey(string command, string argument)
        {
            var reg = Regex.Match(argument, @"^([+^%]*)(.+)");
            if (reg.Success)
            {
                sentKey = true;
                var mods = reg.Groups[1].Value;
                if (mods.Contains("+"))
                    sendShift = true;
                if (mods.Contains("^"))
                    sendCtrl = true;
                if (mods.Contains("%"))
                    sendAlt = true;

                if (sendShift)
                    Interface.ClientState.KeyState[(int)Keys.ShiftKey] = true;
                if (sendCtrl)
                    Interface.ClientState.KeyState[(int)Keys.ControlKey] = true;
                if (sendAlt)
                    Interface.ClientState.KeyState[(int)Keys.Menu] = true;

                SendKeys.SendWait(reg.Groups[2].Value);
            }
        }

        [Command("/walk")]
        [HelpMessage("Toggles RP walk, alternatively, you can specify an amount of time in seconds to walk for.")]
        private void OnWalk(string command, string argument)
        {
            if (!float.TryParse(argument, out walkTime))
                IsWalking = !IsWalking;
            else
                IsWalking = true;
        }

        [Command("/ng+t")]
        [HelpMessage("Toggles New Game+.")]
        private unsafe void OnNGPT(string command, string argument)
        {
            newGameUIPtr = (newGameUIPtr != IntPtr.Zero) ? newGameUIPtr : Interface.Framework.Gui.FindAgentInterface("QuestRedo") + 0xA8;
            if (newGameUIPtr == IntPtr.Zero) { PrintError("Failed to get NG+ agent, please open the NG+ window and then use this command to initialize it."); return; }

            *(byte*)(newGameUIPtr + 0x8) ^= 1;
            NewGamePlusAction(GetUnknownNGPPtr(), newGameUIPtr);
        }

        [Command("/doemote")]
        [HelpMessage("Performs the specified emote by number.")]
        private void OnDoEmote(string command, string argument)
        {
            emoteAgent = (emoteAgent != IntPtr.Zero) ? emoteAgent : Interface.Framework.Gui.FindAgentInterface("Emote");
            if (emoteAgent == IntPtr.Zero) { PrintError("Failed to get emote agent, please open the emote window and then use this command to initialize it."); return; }

            if (uint.TryParse(argument, out var emote))
                DoEmote(emoteAgent, emote, 0, true, true);
            else
                PrintError("Emote must be specified by a number.");
        }

        [Command("/leaveduty")]
        [HelpMessage("Opens the abandon duty prompt, use YesAlready to make it instant.")]
        private void OnLeaveDuty(string command, string argument)
        {
            contentsFinderMenuAgent = (contentsFinderMenuAgent != IntPtr.Zero) ? contentsFinderMenuAgent : Interface.Framework.Gui.FindAgentInterface("ContentsFinderMenu");
            if (contentsFinderMenuAgent == IntPtr.Zero) { PrintError("Failed to get duty finder agent, please open the duty finder window and then use this command to initialize it."); return; }
            OpenAbandonDuty(contentsFinderMenuAgent);
        }

        [Command("/altphysics")]
        [Aliases("/oobshop", "/set0to1instead")]
        [HelpMessage("Enables alternate physics (used in housing / duties), which allows for void crossing and freeze jumps.")]
        private void OnAltPhysics(string command, string argument)
        {
            enableAlternatePhysics = !enableAlternatePhysics;
            IsAlternatePhysics = enableAlternatePhysics;
            PrintEcho("Alternate physics are now " + (enableAlternatePhysics ? "enabled!" : "disabled!"));
        }

        public static void PrintEcho(string message) => Interface.Framework.Gui.Chat.Print($"[OOBlugin] {message}");
        public static void PrintError(string message) => Interface.Framework.Gui.Chat.PrintError($"[OOBlugin] {message}");

        private void Update(Dalamud.Game.Internal.Framework framework)
        {
            if (!pluginReady) return;

            if (enableAlternatePhysics && !IsAlternatePhysics)
                IsAlternatePhysics = true;

            if (!sentKey)
            {
                if (sendShift)
                {
                    Interface.ClientState.KeyState[(int)Keys.ShiftKey] = false;
                    sendShift = false;
                }
                if (sendCtrl)
                {
                    Interface.ClientState.KeyState[(int)Keys.ControlKey] = false;
                    sendCtrl = false;
                }
                if (sendAlt)
                {
                    Interface.ClientState.KeyState[(int)Keys.Menu] = false;
                    sendAlt = false;
                }
            }
            else
                sentKey = false;

            if (walkTime > 0 && (walkTime -= ImGui.GetIO().DeltaTime) <= 0)
                IsWalking = false;

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

        private void Draw()
        {
            if (!pluginReady) return;
            ui.Draw();
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

                ProcessChatBox(uiModule, memStr, IntPtr.Zero, 0);

                Marshal.FreeHGlobal(memStr);
            }
            catch { PrintError("Failed injecting command"); }
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            commandManager.Dispose();

            Interface.SavePluginConfig(Config);

            Interface.Framework.OnUpdateEvent -= Update;

            Interface.UiBuilder.OnBuildUi -= Draw;

            Interface.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
