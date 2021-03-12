using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Dalamud.Plugin;
using OOBlugin.Attributes;

[assembly: AssemblyTitle("OOBlugin")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace OOBlugin
{
    public class OOBlugin : IDalamudPlugin
    {
        public static DalamudPluginInterface Interface { get; private set; }
        private PluginCommandManager<OOBlugin> commandManager;
        public static Configuration Config { get; private set; }
        public static OOBlugin Plugin { get; private set; }
        private PluginUI ui;

        private readonly Stopwatch timer = new Stopwatch();
        private int fpsLock = 0;
        private float fpsLockTime = 0;

        private readonly List<string> quickExecuteQueue = new List<string>();

        private bool sentKey = false;
        private bool sendShift = false;
        private bool sendCtrl = false;
        private bool sendAlt = false;

        private bool pluginReady = false;

        public string Name => "OOBlugin";

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

            InitCommands();

            // Add to SendKeys using reflection
            FieldInfo info = typeof(SendKeys).GetField("keywords", BindingFlags.Static | BindingFlags.NonPublic);
            Array oldKeys = (Array)info.GetValue(null);
            if (oldKeys.Length == 49)
            {
                Type elementType = oldKeys.GetType().GetElementType();
                Array newKeys = Array.CreateInstance(elementType, oldKeys.Length + 10 + 172);
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

            pluginReady = true;
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
        [HelpMessage("Excecutes all commands in a single frame. Usage: \"/qexec /echo Hello\" > \"/qexec /echo there!\" > \"/qexec\".")]
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

        public static void PrintEcho(string message) => Interface.Framework.Gui.Chat.Print($"[OOBlugin] {message}");
        public static void PrintError(string message) => Interface.Framework.Gui.Chat.PrintError($"[OOBlugin] {message}");

        private void Update(Dalamud.Game.Internal.Framework framework)
        {
            if (!pluginReady) return;

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

        #region Chat Injection
        private delegate IntPtr GetUIModuleDelegate(IntPtr basePtr);
        private delegate void EasierProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unused, byte a4);

        private GetUIModuleDelegate GetUIModule;
        private EasierProcessChatBoxDelegate _EasierProcessChatBox;

        public IntPtr uiModulePtr;

        private void InitCommands()
        {
            try
            {
                var getUIModulePtr = Interface.TargetModuleScanner.ScanText("E8 ?? ?? ?? ?? 48 83 7F ?? 00 48 8B F0");
                var easierProcessChatBoxPtr = Interface.TargetModuleScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9");
                uiModulePtr = Interface.TargetModuleScanner.GetStaticAddressFromSig("48 8B 0D ?? ?? ?? ?? 48 8D 54 24 ?? 48 83 C1 10 E8 ?? ?? ?? ??");

                GetUIModule = Marshal.GetDelegateForFunctionPointer<GetUIModuleDelegate>(getUIModulePtr);
                _EasierProcessChatBox = Marshal.GetDelegateForFunctionPointer<EasierProcessChatBoxDelegate>(easierProcessChatBoxPtr);
            }
            catch
            {
                PrintError("Error with loading signatures");
            }
        }

        private void ExecuteCommand(string command)
        {
            try
            {
                if (uiModulePtr == null || uiModulePtr == IntPtr.Zero)
                    InitCommands();

                var uiModule = GetUIModule(Marshal.ReadIntPtr(uiModulePtr));

                if (uiModule == IntPtr.Zero)
                {
                    throw new ApplicationException("uiModule was null");
                }

                var bytes = Encoding.UTF8.GetBytes(command);

                var mem1 = Marshal.AllocHGlobal(400);
                var mem2 = Marshal.AllocHGlobal(bytes.Length + 30);

                Marshal.Copy(bytes, 0, mem2, bytes.Length);
                Marshal.WriteByte(mem2 + bytes.Length, 0);
                Marshal.WriteInt64(mem1, mem2.ToInt64());
                Marshal.WriteInt64(mem1 + 8, 64);
                Marshal.WriteInt64(mem1 + 8 + 8, bytes.Length + 1);
                Marshal.WriteInt64(mem1 + 8 + 8 + 8, 0);

                _EasierProcessChatBox(uiModule, mem1, IntPtr.Zero, 0);

                Marshal.FreeHGlobal(mem1);
                Marshal.FreeHGlobal(mem2);
            }
            catch
            {
                PrintError("Error with injecting command");
            }
        }
        #endregion

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
