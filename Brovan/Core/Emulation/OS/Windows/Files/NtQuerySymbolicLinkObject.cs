using System.Runtime.InteropServices;
using System.Text;
using static Brovan.Core.Helpers.BinaryHelpers;

namespace Brovan.Core.Emulation.OS.Windows
{
    internal class NtQuerySymbolicLinkObject : IWinSyscall
    {
        public NTSTATUS Handle(BinaryEmulator Instance)
        {
            if (Instance._binary.Architecture != BinaryArchitecture.x64)
                return Instance.WinUnimplemented;

            ulong LinkHandle = Instance.WinHelper.GetArg64(0);
            ulong LinkTargetPtr = Instance.WinHelper.GetArg64(1);
            ulong ReturnedLengthPtr = Instance.WinHelper.GetArg64(2);

            if (LinkHandle == 0 || LinkTargetPtr == 0)
                return NTSTATUS.STATUS_INVALID_PARAMETER;

            uint UsSize = (uint)Marshal.SizeOf<UNICODE_STRING64>();
            if (!Instance.IsRegionMapped(LinkTargetPtr, UsSize))
                return NTSTATUS.STATUS_ACCESS_VIOLATION;

            if (ReturnedLengthPtr != 0)
            {
                if (!Instance.IsRegionMapped(ReturnedLengthPtr, 4))
                    return NTSTATUS.STATUS_ACCESS_VIOLATION;
            }

            IHandleObject Obj = Instance.WinHelper.HandleManager.GetObjectByHandle(LinkHandle);
            if (Obj == null)
                return NTSTATUS.STATUS_INVALID_HANDLE;

            string Target = TryGetTargetString(Obj);
            if (Target == null)
                return NTSTATUS.STATUS_NOT_SUPPORTED;
            
            if (!StructSerializer.ParseStruct(Instance, LinkTargetPtr, out UNICODE_STRING64 Out))
                return NTSTATUS.STATUS_ACCESS_VIOLATION;

            if (Out.MaximumLength == 0)
                return NTSTATUS.STATUS_BUFFER_TOO_SMALL;

            if (Out.Buffer == 0)
                return NTSTATUS.STATUS_INVALID_PARAMETER;

            if (!Instance.IsRegionMapped(Out.Buffer, Out.MaximumLength))
                return NTSTATUS.STATUS_ACCESS_VIOLATION;

            uint RequiredBytes = (uint)(Target.Length * 2);

            if (ReturnedLengthPtr != 0)
                Instance._emulator.WriteMemory(ReturnedLengthPtr, RequiredBytes);

            Instance._emulator.WriteMemory(LinkTargetPtr + 0, (ushort)Math.Min(RequiredBytes, 0xFFFF), 2);

            if (Out.MaximumLength < RequiredBytes)
                return NTSTATUS.STATUS_BUFFER_TOO_SMALL;

            Instance._emulator.WriteMemory(Out.Buffer, Target, Encoding.Unicode);

            if (Out.MaximumLength >= RequiredBytes + 2)
                Instance._emulator.WriteMemory(Out.Buffer + RequiredBytes, (ushort)0, 2);

            Instance.TriggerEventMessage($"[+] NtQuerySymbolicLinkObject: Handle=0x{LinkHandle:X}, Target=\"{Target}\" (Len={RequiredBytes}).", LogFlags.Syscall);

            return NTSTATUS.STATUS_SUCCESS;
        }

        private static string TryGetTargetString(IHandleObject Obj)
        {
            // Direct, AOT-safe access. Reflection (GetField/GetProperty) is trimmed away under
            // NativeAOT and would return null here, making this syscall wrongly fail with NOT_SUPPORTED.
            return Obj is WinSymbolicLink Link ? Link.Target : null;
        }
    }
}
