using System;
using System.Runtime.InteropServices;
using Dalamud;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using static OOBlugin.OOBlugin;

namespace OOBlugin
{
    public static unsafe class Game
    {
        public delegate void ProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unused, byte a4);
        public static ProcessChatBoxDelegate ProcessChatBox;
        public static IntPtr uiModule = IntPtr.Zero;

        private static IntPtr walkingBoolPtr = IntPtr.Zero;
        public static bool IsWalking
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

        private delegate IntPtr GetModuleDelegate(IntPtr basePtr);

        public static IntPtr newGameUIPtr = IntPtr.Zero;
        public delegate IntPtr GetUnknownNGPPtrDelegate();
        public static GetUnknownNGPPtrDelegate GetUnknownNGPPtr;
        public delegate void NewGamePlusActionDelegate(IntPtr a1, IntPtr a2);
        public static NewGamePlusActionDelegate NewGamePlusAction;

        public static IntPtr emoteAgent = IntPtr.Zero;
        public delegate void DoEmoteDelegate(IntPtr agent, uint emoteID, long a3, bool a4, bool a5);
        public static DoEmoteDelegate DoEmote;

        public static IntPtr contentsFinderMenuAgent = IntPtr.Zero;
        public delegate void OpenAbandonDutyDelegate(IntPtr agent);
        public static OpenAbandonDutyDelegate OpenAbandonDuty;

        public static IntPtr itemContextMenuAgent = IntPtr.Zero;
        public delegate void UseItemDelegate(IntPtr itemContextMenuAgent, uint itemID, uint inventoryPage, uint inventorySlot, short a5);
        public static UseItemDelegate UseItem;

        public static IntPtr actionCommandRequestTypePtr = IntPtr.Zero;
        public static byte ActionCommandRequestType
        {
            get => *(byte*)actionCommandRequestTypePtr;
            set
            {
                if (actionCommandRequestTypePtr != IntPtr.Zero)
                    SafeMemory.WriteBytes(actionCommandRequestTypePtr, new[] { value });
            }
        }

        private static int* keyStates;
        private static byte* keyStateIndexArray;
        public static byte GetKeyStateIndex(int key) => key is >= 0 and < 240 ? keyStateIndexArray[key] : (byte)0;
        private static ref int GetKeyState(int key) => ref keyStates[key];

        public static bool SendKey(System.Windows.Forms.Keys key) => SendKey((int)key);
        public static bool SendKey(int key)
        {
            var stateIndex = GetKeyStateIndex(key);
            if (stateIndex <= 0) return false;

            GetKeyState(stateIndex) |= 6;
            return true;
        }
        public static bool SendKeyHold(System.Windows.Forms.Keys key) => SendKeyHold((int)key);
        public static bool SendKeyHold(int key)
        {
            var stateIndex = GetKeyStateIndex(key);
            if (stateIndex <= 0) return false;

            GetKeyState(stateIndex) |= 1;
            return true;
        }
        public static bool SendKeyRelease(System.Windows.Forms.Keys key) => SendKeyRelease((int)key);
        public static bool SendKeyRelease(int key)
        {
            var stateIndex = GetKeyStateIndex(key);
            if (stateIndex <= 0) return false;

            GetKeyState(stateIndex) &= ~1;
            return true;
        }

        // cmp byte ptr [r15+33h], 6 -> test byte ptr [r15+3Ah], 10
        public static readonly Memory.Replacer enhancedAutoFaceTarget = new("41 80 7F 33 06 75 1E 48 8D 0D", new byte[] { 0x41, 0xF6, 0x47, 0x3A, 0x10 }, Config.EnhancedAutoFaceTarget);

        public static IntPtr ActionManager;
        public static ref bool IsQueued => ref *(bool*)(ActionManager + 0x68);
        public static ref uint QueuedActionType => ref *(uint*)(ActionManager + 0x6C);
        public static ref uint QueuedAction => ref *(uint*)(ActionManager + 0x70);
        public static ref long QueuedTarget => ref *(long*)(ActionManager + 0x78);
        public static ref uint QueuedUseType => ref *(uint*)(ActionManager + 0x80);
        public static ref uint QueuedPVPAction => ref *(uint*)(ActionManager + 0x84);

        public static void Initialize()
        {
            try
            {
                uiModule = DalamudApi.GameGui.GetUIModule();
                ProcessChatBox = Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(DalamudApi.SigScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9"));
            }
            catch { PrintError("Failed to load /qexec"); }

            try { walkingBoolPtr = DalamudApi.SigScanner.GetStaticAddressFromSig("88 83 ?? ?? ?? ?? 0F B6 05 ?? ?? ?? ?? 88 83"); } // also found around g_PlayerMoveController+523
            catch { PrintError("Failed to load /walk"); }

            try
            {
                var agentModule = Framework.Instance()->GetUiModule()->GetAgentModule();

                try
                {
                    GetUnknownNGPPtr = Marshal.GetDelegateForFunctionPointer<GetUnknownNGPPtrDelegate>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 80 7B 29 01"));
                    NewGamePlusAction = Marshal.GetDelegateForFunctionPointer<NewGamePlusActionDelegate>(DalamudApi.SigScanner.ScanText("48 89 5C 24 08 48 89 74 24 18 57 48 83 EC 30 48 8B 02 48 8B DA 48 8B F9 48 8D 54 24 48 48 8B CB"));
                    newGameUIPtr = (IntPtr)agentModule->GetAgentByInternalID(335) + 0xA8;
                }
                catch { PrintError("Failed to load /ng+t"); }

                try
                {
                    DoEmote = Marshal.GetDelegateForFunctionPointer<DoEmoteDelegate>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? B8 0A 00 00 00"));
                    emoteAgent = (IntPtr)agentModule->GetAgentByInternalID(19);
                }
                catch { PrintError("Failed to load /doemote"); }

                try
                {
                    OpenAbandonDuty = Marshal.GetDelegateForFunctionPointer<OpenAbandonDutyDelegate>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? EB 90 48 8B CB"));
                    contentsFinderMenuAgent = (IntPtr)agentModule->GetAgentByInternalID(223);
                }
                catch { PrintError("Failed to load /leaveduty"); }

                try
                {
                    UseItem = Marshal.GetDelegateForFunctionPointer<UseItemDelegate>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 41 B0 01 BA 13 00 00 00"));
                    itemContextMenuAgent = (IntPtr)agentModule->GetAgentByInternalID(10);
                }
                catch { PrintError("Failed to load /useitem"); }
            }
            catch { PrintError("Failed to get agent module"); }

            try { actionCommandRequestTypePtr = DalamudApi.SigScanner.ScanText("02 00 00 00 41 8B D7 89"); } // Located 1 function deep in Client__UI__Shell__ShellCommandAction_ExecuteCommand
            catch { PrintError("Failed to load /qac"); }

            try
            {
                keyStates = (int*)DalamudApi.SigScanner.GetStaticAddressFromSig("4C 8D 05 ?? ?? ?? ?? 44 8B 0D"); // 4C 8D 05 ?? ?? ?? ?? 44 8B 1D
                keyStateIndexArray = (byte*)(DalamudApi.SigScanner.Module.BaseAddress + *(int*)(DalamudApi.SigScanner.ScanModule("0F B6 94 33 ?? ?? ?? ?? 84 D2") + 4));
            }
            catch { PrintError("Failed to load /sendkey!"); }

            try { ActionManager = (IntPtr)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance(); }
            catch { PrintError("Failed to load ActionManager!"); }
        }

        public static void Dispose()
        {

        }
    }
}
