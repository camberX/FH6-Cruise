using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Fh6Cruise;

internal static class Native
{
    public const uint ProcessRuntimeAccess = 0x0010043A;
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint MemRelease = 0x8000;
    public const uint MemCommitReserve = MemCommit | MemReserve;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint PageExecute = 0x10;
    public const uint PageExecuteRead = 0x20;
    public const uint PageExecuteWriteCopy = 0x80;
    public const uint PageReadWrite = 0x04;
    public const uint PageWriteCopy = 0x08;
    public const uint PageReadonly = 0x02;
    public const uint PageNoAccess = 0x01;
    public const uint PageGuard = 0x100;
    public const uint ListModulesAll = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInfo
    {
        public IntPtr BaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr VirtualQueryEx(IntPtr process, UIntPtr address, out MemoryBasicInformation64 information, UIntPtr length);

    [DllImport("kernel32.dll")]
    public static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetModuleInformation(IntPtr process, IntPtr module, out ModuleInfo information, uint size);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleBaseName(IntPtr process, IntPtr module, StringBuilder name, uint size);

    public static bool IsExecutable(uint protect)
    {
        if ((protect & PageGuard) != 0 || (protect & PageNoAccess) != 0)
            return false;
        var flags = protect & 0xFF;
        return flags is PageExecute or PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    public static bool IsWritable(uint protect)
    {
        if ((protect & PageGuard) != 0 || (protect & PageNoAccess) != 0)
            return false;
        var flags = protect & 0xFF;
        return flags is PageReadWrite or PageWriteCopy or PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    public static bool IsReadable(uint protect)
    {
        if ((protect & PageGuard) != 0 || (protect & PageNoAccess) != 0)
            return false;
        var flags = protect & 0xFF;
        return flags is PageReadonly or PageReadWrite or PageWriteCopy
            or PageExecute or PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
    }
}

internal sealed class ProcessMemory
{
    public IntPtr Handle { get; set; }

    public bool TryRead(ulong address, byte[] destination)
    {
        return Native.ReadProcessMemory(Handle, new IntPtr((long)address), destination, destination.Length, out var read)
            && read == destination.Length;
    }

    public byte[]? Read(ulong address, int length)
    {
        var data = new byte[length];
        return TryRead(address, data) ? data : null;
    }

    public ulong ReadU64(ulong address)
    {
        var data = Read(address, 8);
        return data == null ? 0 : BitConverter.ToUInt64(data, 0);
    }

    public int ReadI32(ulong address)
    {
        var data = Read(address, 4);
        return data == null ? 0 : BitConverter.ToInt32(data, 0);
    }

    public bool TryWrite(ulong address, byte[] data)
    {
        return Native.WriteProcessMemory(Handle, new IntPtr((long)address), data, data.Length, out var written)
            && written == data.Length;
    }

    public void Poke(ulong address, byte[] data)
    {
        if (Handle == IntPtr.Zero || address <= 0x10000 || data.Length == 0)
            throw new InvalidOperationException("Invalid protected-memory write.");
        var target = new IntPtr((long)address);
        if (!Native.VirtualProtectEx(Handle, target, (UIntPtr)data.Length, Native.PageExecuteReadWrite, out var previous))
            throw new InvalidOperationException("VirtualProtectEx failed.");
        try
        {
            if (!TryWrite(address, data))
                throw new InvalidOperationException("Protected memory write failed.");
        }
        finally
        {
            Native.VirtualProtectEx(Handle, target, (UIntPtr)data.Length, previous, out _);
        }
    }

    public bool Executable(ulong address)
    {
        return Region(address, out var info) && info.State == Native.MemCommit && Native.IsExecutable(info.Protect);
    }

    public bool Readable(ulong address, int length)
    {
        if (!Region(address, out var info) || info.State != Native.MemCommit || !Native.IsReadable(info.Protect))
            return false;
        var end = address + (ulong)length;
        return end >= address && address >= info.BaseAddress && end <= info.BaseAddress + info.RegionSize;
    }

    public bool Writable(ulong address, int length)
    {
        if (!Region(address, out var info) || info.State != Native.MemCommit || !Native.IsWritable(info.Protect))
            return false;
        var end = address + (ulong)length;
        return end >= address && address >= info.BaseAddress && end <= info.BaseAddress + info.RegionSize;
    }

    public ulong ReserveNear(ulong site, int size)
    {
        const ulong step = 0x10000;
        const ulong span = 0x70000000;
        const ulong ceiling = 0x00007FFFFFFF0000;
        var aligned = site & ~(step - 1);
        for (ulong distance = 0; distance <= span; distance += step)
        {
            if (aligned > distance)
            {
                var found = Claim(aligned - distance, size, site);
                if (found != 0)
                    return found;
            }

            var ahead = aligned + distance;
            if (ahead < ceiling)
            {
                var found = Claim(ahead, size, site);
                if (found != 0)
                    return found;
            }
        }

        throw new InvalidOperationException("Could not allocate the vehicle detour close enough to the game code.");
    }

    public void Release(ulong address)
    {
        if (address == 0 || Handle == IntPtr.Zero)
            return;
        Native.VirtualFreeEx(Handle, new IntPtr((long)address), UIntPtr.Zero, Native.MemRelease);
    }

    private ulong Claim(ulong address, int size, ulong site)
    {
        if (address == 0)
            return 0;
        var remote = Native.VirtualAllocEx(Handle, new IntPtr((long)address), (UIntPtr)Math.Max(size, 0x1000), Native.MemCommitReserve, Native.PageExecuteReadWrite);
        if (remote == IntPtr.Zero)
            return 0;
        var allocated = (ulong)remote.ToInt64();
        if (JumpFits(site, allocated) && JumpFits(allocated, site))
            return allocated;
        Native.VirtualFreeEx(Handle, remote, UIntPtr.Zero, Native.MemRelease);
        return 0;
    }

    private bool Region(ulong address, out Native.MemoryBasicInformation64 info)
    {
        var size = (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation64>();
        return Native.VirtualQueryEx(Handle, new UIntPtr(address), out info, size) != UIntPtr.Zero;
    }

    private static bool JumpFits(ulong from, ulong to)
    {
        var delta = (long)to - ((long)from + 5);
        return delta >= int.MinValue && delta <= int.MaxValue;
    }
}

internal sealed class SpeedPatch
{
    private readonly record struct Mark(byte[] Bytes, bool[] Wild, int Cut, byte[] Want, byte Save);

    private static readonly Mark[] Marks =
    {
        new(
            new byte[] { 0xF3, 0x0F, 0x10, 0x4F, 0x24, 0x49, 0x8B, 0x00, 0x0F, 0x28, 0x00, 0xF3, 0x0F, 0x5C, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF3, 0x0F, 0x10 },
            new[] { false, false, false, false, false, false, false, true, false, false, true, false, false, false, true, true, true, true, true, false, false, false },
            0,
            new byte[] { 0xF3, 0x0F, 0x10, 0x4F, 0x24 },
            0x3D),
        new(
            new byte[] { 0xF3, 0x0F, 0x10, 0x47, 0x20, 0xF3, 0x0F, 0x11, 0x45, 0xF0, 0xF3, 0x0F, 0x10, 0x4F, 0x24, 0xF3, 0x0F, 0x11, 0x4D, 0xF4, 0xF3, 0x0F, 0x10, 0x47, 0x28, 0xF3, 0x0F, 0x11, 0x45, 0xF8 },
            new bool[30],
            10,
            new byte[] { 0xF3, 0x0F, 0x10, 0x4F, 0x24 },
            0x3D)
    };

    private readonly ProcessMemory _mem;
    private ulong _site;
    private ulong _stub;
    private int _slot;
    private byte[] _stolen = Array.Empty<byte>();
    private byte[] _jump = Array.Empty<byte>();

    public SpeedPatch(ProcessMemory mem) => _mem = mem;

    public bool Installed { get; private set; }

    public ulong ReadBody() => _mem.ReadU64(_stub + (ulong)_slot);

    public void Install(byte[] image, ulong imageBase)
    {
        ulong site = 0;
        byte[]? stolen = null;
        byte save = 0x3D;
        ulong reused = 0;
        byte[]? reusedBytes = null;
        byte reusedSave = 0x3D;

        foreach (var mark in Marks)
        {
            for (var from = 0; from <= image.Length - mark.Bytes.Length;)
            {
                var hit = Find(image, mark, from);
                if (hit < 0)
                    break;
                from = hit + 1;
                var candidate = imageBase + (ulong)(hit + mark.Cut);
                var live = _mem.Read(candidate, mark.Want.Length);
                if (live == null)
                    continue;
                if (Same(live, mark.Want))
                {
                    site = candidate;
                    stolen = live;
                    save = mark.Save;
                    break;
                }

                if (live[0] == 0xE9 && reused == 0)
                {
                    reused = candidate;
                    reusedBytes = mark.Want;
                    reusedSave = mark.Save;
                }
            }

            if (site != 0)
                break;
        }

        if (site == 0 && reused != 0 && reusedBytes != null)
        {
            _mem.Poke(reused, reusedBytes);
            site = reused;
            stolen = (byte[])reusedBytes.Clone();
            save = reusedSave;
        }

        if (site == 0 || stolen == null)
            throw new InvalidOperationException("Vehicle hook signature was not found. Restart Forza, then press the cruise bind again.");

        var body = new byte[12];
        body[0] = 0x48;
        body[1] = 0x89;
        body[2] = save;
        Buffer.BlockCopy(stolen, 0, body, 7, 5);
        var slot = (body.Length + 5 + 3) & ~3;
        Buffer.BlockCopy(BitConverter.GetBytes(slot - 7), 0, body, 3, 4);

        var bytes = Math.Max(body.Length + 5, slot + 8);
        var stub = _mem.ReserveNear(site, bytes);
        _stub = stub;
        var remote = new byte[bytes];
        Buffer.BlockCopy(body, 0, remote, 0, body.Length);
        var back = Jump(stub + (ulong)body.Length, site + 5, 5);
        Buffer.BlockCopy(back, 0, remote, body.Length, back.Length);
        if (!_mem.TryWrite(stub, remote))
            throw new InvalidOperationException("Could not write the vehicle detour.");

        var jump = Jump(site, stub, 5);
        _mem.Poke(site, jump);
        Native.FlushInstructionCache(_mem.Handle, new IntPtr((long)site), (UIntPtr)jump.Length);

        _site = site;
        _slot = slot;
        _stolen = stolen;
        _jump = jump;
        Installed = true;
    }

    public void Cover()
    {
        if (Installed && _stolen.Length > 0)
            _mem.Poke(_site, _stolen);
    }

    public void Reveal()
    {
        if (Installed && _jump.Length > 0)
            _mem.Poke(_site, _jump);
    }

    public void FreeRemote()
    {
        _mem.Release(_stub);
        _stub = 0;
    }

    public void Drop()
    {
        Installed = false;
        _site = 0;
        _stub = 0;
        _slot = 0;
        _stolen = Array.Empty<byte>();
        _jump = Array.Empty<byte>();
    }

    private static int Find(byte[] image, Mark mark, int from)
    {
        var last = image.Length - mark.Bytes.Length;
        for (var i = from; i <= last; i++)
        {
            if (Hits(image, i, mark))
                return i;
        }

        return -1;
    }

    private static bool Hits(byte[] image, int offset, Mark mark)
    {
        for (var i = 0; i < mark.Bytes.Length; i++)
        {
            if (i >= mark.Cut && i < mark.Cut + mark.Want.Length)
                continue;
            if (mark.Wild[i])
                continue;
            if (image[offset + i] != mark.Bytes[i])
                return false;
        }

        return true;
    }

    private static bool Same(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static byte[] Jump(ulong from, ulong to, int length)
    {
        var delta = (long)to - ((long)from + 5);
        if (delta < int.MinValue || delta > int.MaxValue)
            throw new InvalidOperationException("Relative jump target is out of range.");
        var bytes = new byte[length];
        bytes[0] = 0xE9;
        Buffer.BlockCopy(BitConverter.GetBytes((int)delta), 0, bytes, 1, 4);
        for (var i = 5; i < bytes.Length; i++)
            bytes[i] = 0x90;
        return bytes;
    }
}

internal sealed class IntegrityGap
{
    private static readonly byte[] Shape =
    {
        0x48, 0x8B, 0xD9, 0x48, 0x8D, 0x05, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x89, 0x01, 0xE8, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0xCB, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xE9
    };

    private static readonly bool[] Wild =
    {
        false, false, false, false, false, false, true, true, true, true,
        false, false, false, false, true, true, true, true,
        false, false, false, false, false, false, false, false, false
    };

    private readonly ProcessMemory _mem;
    private ulong _slot;
    private ulong _saved;
    private ulong _standIn;

    public IntegrityGap(ProcessMemory mem) => _mem = mem;

    public bool Armed { get; private set; }

    public void Arm(byte[] image, ulong imageBase)
    {
        if (Armed)
            return;

        var ret = FirstExecutableRet(image, imageBase);
        if (ret < 0)
            throw new InvalidOperationException("CRC bypass ret stub was not found.");

        var at = Find(image, 0);
        if (at < 0)
            throw new InvalidOperationException("CRC bypass signature was not found.");
        if (image[at + 3] != 0x48 || image[at + 4] != 0x8D || image[at + 5] != 0x05)
            throw new InvalidOperationException("CRC bypass signature did not land on the expected lea.");

        var lea = imageBase + (ulong)at + 3;
        var table = (ulong)((long)lea + 7 + _mem.ReadI32(lea + 3));
        var slot = table + 0x30;
        var saved = _mem.ReadU64(slot);
        if (saved == 0 || saved <= 0x10000 || saved >= 0x00007FFFFFFFFFFFUL || !_mem.Executable(saved))
            throw new InvalidOperationException("CRC bypass function pointer could not be read.");

        var standIn = imageBase + (ulong)ret;
        _mem.Poke(slot, BitConverter.GetBytes(standIn));
        _slot = slot;
        _saved = saved;
        _standIn = standIn;
        Armed = true;
    }

    public void Disarm() => Armed = false;

    public void PutSaved()
    {
        if (_slot != 0 && _saved != 0)
            _mem.Poke(_slot, BitConverter.GetBytes(_saved));
    }

    public void PutStandIn()
    {
        if (_slot != 0 && _standIn != 0)
            _mem.Poke(_slot, BitConverter.GetBytes(_standIn));
    }

    public void Drop()
    {
        Armed = false;
        _slot = 0;
        _saved = 0;
        _standIn = 0;
    }

    private int FirstExecutableRet(byte[] image, ulong imageBase)
    {
        var from = 0;
        var seen = 0;
        while (from < image.Length && seen < 512)
        {
            var hit = Array.IndexOf(image, (byte)0xC3, from);
            if (hit < 0)
                return -1;
            seen++;
            if (_mem.Executable(imageBase + (ulong)hit))
                return hit;
            from = hit + 1;
        }

        return -1;
    }

    private static int Find(byte[] image, int from)
    {
        var last = image.Length - Shape.Length;
        for (var i = from; i <= last; i++)
        {
            if (Hits(image, i))
                return i;
        }

        return -1;
    }

    private static bool Hits(byte[] image, int offset)
    {
        for (var i = 0; i < Shape.Length; i++)
        {
            if (Wild[i])
                continue;
            if (image[offset + i] != Shape[i])
                return false;
        }

        return true;
    }
}

internal static class SpeedometerHud
{
    private const int ImageSize = 188481536;
    private const uint Settled = 6;
    private const ulong UiServiceRva = 177060008;
    private const ulong UiServiceVtableRva = 113483816;
    private const ulong DependencyVtableRva = 113403904;
    private const ulong TransitionManagerVtableRva = 113403792;
    private const ulong HudPageVtableRva = 118014312;
    private const ulong ServiceDependencyOffset = 160;
    private const ulong RootTransitionManagerOffset = 56;
    private const ulong ManagerOwnerOffset = 192;
    private const ulong ManagerCurrentPageOffset = 144;
    private const ulong ManagerStateOffset = 104;
    private const ulong PageTransitionManagerOffset = 656;
    private const ulong PageUiVisibleOffset = 964;

    private const ulong RegistryGlobalRva = 176695832;
    private const ulong RegistryKeyHash = 14601433220641418792;
    private const ulong RegistryWrapperVtableRva = 111515992;
    private const ulong RegistryContextVtableRva = 115409056;
    private const ulong RegistryContextControlVtableRva = 111519144;
    private const ulong HudVtableRva = 115445016;
    private const ulong HudControlVtableRva = 110304848;
    private const ulong HudSubobjectVtableRva = 115445096;
    private const ulong HudSubobjectSlotZeroTargetRva = 11467184;
    private const ulong HudTypeTokenRva = 179277752;
    private const ulong OuterControlVtableRva = 115427272;
    private const ulong OuterPrimaryVtableRva = 117226168;
    private const ulong OuterSecondaryVtableRva = 117226456;
    private const ulong ChildVtableRva = 114328616;
    private const ulong ChildModeOffset = 228;

    public static bool IsVisible(IntPtr process, ulong moduleBase, int moduleSize)
    {
        if (process == IntPtr.Zero || moduleBase == 0 || moduleSize != ImageSize)
            return false;
        var memory = new Mem(process);
        if (!TryDrivingHud(memory, moduleBase, out var first) || !first.Visible)
            return false;
        if (!TryDrivingHud(memory, moduleBase, out var second) || second != first)
            return false;

        var gauge = SpeedometerMount(memory, moduleBase);
        return gauge != Mount.Missing;
    }

    private static bool TryDrivingHud(Mem memory, ulong moduleBase, out HudSnapshot snapshot)
    {
        snapshot = default;
        if (!memory.U64(moduleBase + UiServiceRva, out var service) || !IsObject(service) ||
            !memory.U64(service, out var serviceVtable) || serviceVtable != moduleBase + UiServiceVtableRva ||
            !memory.U64(service + ServiceDependencyOffset, out var dependency) || !IsObject(dependency) ||
            !memory.U64(dependency, out var dependencyVtable) || dependencyVtable != moduleBase + DependencyVtableRva)
            return false;

        var manager = dependency + RootTransitionManagerOffset;
        if (!memory.U64(manager, out var managerVtable) || managerVtable != moduleBase + TransitionManagerVtableRva ||
            !memory.U64(manager + ManagerOwnerOffset, out var owner) || owner != dependency ||
            !memory.U32(manager + ManagerStateOffset, out var state) || state > Settled ||
            !memory.U64(manager + ManagerCurrentPageOffset, out var page) ||
            page != 0 && !IsObject(page))
            return false;

        var visible = false;
        if (page != 0)
        {
            if (!memory.U64(page, out var pageVtable) || !InImage(pageVtable, moduleBase))
                return false;
            if (pageVtable == moduleBase + HudPageVtableRva)
            {
                if (!memory.U8(page + PageUiVisibleOffset, out var uiVisible) || uiVisible > 1 ||
                    !TryNested(memory, moduleBase, page, dependency, out var nested))
                    return false;
                visible = state == Settled && uiVisible == 1 && nested.State == Settled && nested.Page == 0;
            }
        }

        snapshot = new HudSnapshot(service, dependency, state, page, visible);
        return true;
    }

    private static bool TryNested(Mem memory, ulong moduleBase, ulong page, ulong dependency, out NestedSnapshot snapshot)
    {
        snapshot = default;
        if (!memory.U64(page + PageTransitionManagerOffset, out var manager) || !IsObject(manager) ||
            !memory.U64(manager, out var vtable) || vtable != moduleBase + TransitionManagerVtableRva ||
            !memory.U64(manager + ManagerOwnerOffset, out var owner) || owner != dependency ||
            !memory.U32(manager + ManagerStateOffset, out var state) || state > Settled ||
            !memory.U64(manager + ManagerCurrentPageOffset, out var nestedPage) ||
            nestedPage != 0 && !IsObject(nestedPage))
            return false;
        snapshot = new NestedSnapshot(state, nestedPage);
        return true;
    }

    private static Mount SpeedometerMount(Mem memory, ulong moduleBase)
    {
        if (!memory.U64(moduleBase + RegistryGlobalRva, out var wrapper) || !IsObject(wrapper) ||
            !HasVtable(memory, wrapper, moduleBase + RegistryWrapperVtableRva) ||
            !memory.U64(wrapper + 8, out var context) || !IsObject(context) ||
            !HasVtable(memory, context, moduleBase + RegistryContextVtableRva) ||
            !memory.U64(wrapper + 16, out var contextControl) || !IsObject(contextControl) ||
            !HasVtable(memory, contextControl, moduleBase + RegistryContextControlVtableRva) ||
            contextControl + 16 != context)
            return Mount.Unknown;

        if (!memory.U64(context + 504, out var sentinel) ||
            !memory.U64(context + 512, out var entryCount) ||
            !memory.U64(context + 520, out var buckets) || !IsObject(buckets) ||
            !memory.U64(context + 528, out var bucketsEnd) ||
            !memory.U64(context + 536, out _) ||
            !memory.U64(context + 544, out var mask) ||
            !memory.U64(context + 552, out var bucketCount) ||
            entryCount is 0 or > 65536 || bucketCount is 0 or > 65536 || entryCount > bucketCount ||
            (bucketCount & (bucketCount - 1)) != 0 || mask != bucketCount - 1)
            return Mount.Unknown;

        var bucketBytes = bucketCount * 16;
        if (buckets + bucketBytes != bucketsEnd)
            return Mount.Unknown;

        var bucketIndex = RegistryKeyHash & mask;
        var bucket = buckets + bucketIndex * 16;
        if (bucket < buckets || bucket + 16 > bucketsEnd ||
            !memory.U64(bucket, out var boundary) ||
            !memory.U64(bucket + 8, out var node))
            return Mount.Unknown;

        ulong hud = 0;
        var hops = (int)Math.Min(entryCount, 64);
        for (var hop = 0; hop < hops; hop++)
        {
            if (node == sentinel)
                return Mount.Unknown;
            if (!IsObject(node) || !memory.U64(node + 16, out var hash) || (hash & mask) != bucketIndex)
                return Mount.Unknown;
            if (hash == RegistryKeyHash)
            {
                if (!memory.U64(node + 24, out hud) || !IsObject(hud) ||
                    !memory.U64(node + 32, out var hudControl) || !IsObject(hudControl) ||
                    !HasVtable(memory, hud, moduleBase + HudVtableRva) ||
                    !HasVtable(memory, hudControl, moduleBase + HudControlVtableRva) ||
                    hudControl + 16 != hud)
                    return Mount.Unknown;
                return Mounted(memory, moduleBase, hud, hudControl);
            }

            if (node == boundary)
                return Mount.Unknown;
            if (!memory.U64(node + 8, out node))
                return Mount.Unknown;
        }

        return Mount.Unknown;
    }

    private static Mount Mounted(Mem memory, ulong moduleBase, ulong hud, ulong hudControl)
    {
        if (!memory.U64(hud + 304, out var subobject) || subobject != hud + 48 ||
            !HasVtable(memory, subobject, moduleBase + HudSubobjectVtableRva) ||
            !memory.U64(moduleBase + HudSubobjectVtableRva, out var slotZero) ||
            slotZero != moduleBase + HudSubobjectSlotZeroTargetRva ||
            !memory.Bytes(slotZero, 5, out var prologue) ||
            prologue[0] != 0x48 || prologue[1] != 0x8D || prologue[2] != 0x41 || prologue[3] != 0x70 || prologue[4] != 0xC3)
            return Mount.Unknown;

        var vector = subobject + 112;
        if (!memory.U64(vector, out var begin) || !IsObject(begin) ||
            !memory.U64(vector + 8, out var end) ||
            !memory.U64(vector + 16, out var capacity) ||
            begin > end || end > capacity || (end - begin) % 32 != 0)
            return Mount.Unknown;

        var count = (end - begin) / 32;
        if (count is 0 or > 64)
            return Mount.Unknown;

        var found = false;
        for (ulong index = 0; index < count; index++)
        {
            var entry = begin + index * 32;
            if (!memory.U64(entry, out var token))
                return Mount.Unknown;
            if (token != moduleBase + HudTypeTokenRva)
                continue;
            if (found)
                return Mount.Unknown;
            found = true;
            if (!TryChild(memory, moduleBase, hud, hudControl, entry, out var child))
                return Mount.Unknown;
            if (child == 0)
                return Mount.Present;
            if (!HasVtable(memory, child, moduleBase + ChildVtableRva) ||
                !memory.U32(child + ChildModeOffset, out var mode) || mode > 4)
                return Mount.Unknown;
        }

        return found ? Mount.Present : Mount.Missing;
    }

    private static bool TryChild(Mem memory, ulong moduleBase, ulong hud, ulong hudControl, ulong entry, out ulong child)
    {
        child = 0;
        if (!memory.U64(entry + 8, out var instances) || !IsObject(instances) ||
            !memory.U64(entry + 16, out var instancesEnd) ||
            instancesEnd - instances != 16 ||
            !memory.U64(instances, out var outer) || !IsObject(outer) ||
            !memory.U64(instances + 8, out var outerControl) || !IsObject(outerControl))
            return false;

        if (!HasVtable(memory, outerControl, moduleBase + OuterControlVtableRva) ||
            !memory.U64(outerControl + 16, out var controlled) || controlled != outer ||
            !HasVtable(memory, outer, moduleBase + OuterPrimaryVtableRva) ||
            !memory.U64(outer + 8, out var secondary) || secondary != moduleBase + OuterSecondaryVtableRva ||
            !memory.U64(outer + 16, out var hudBack) || hudBack != hud ||
            !memory.U64(outer + 24, out var hudControlBack) || hudControlBack != hudControl ||
            !memory.U64(outer + 64, out var source) || !IsObject(source) ||
            !memory.U64(outer + 56, out child))
            return false;

        return child == 0 || IsObject(child);
    }

    private static bool HasVtable(Mem memory, ulong instance, ulong expected) =>
        memory.U64(instance, out var vtable) && vtable == expected;

    private static bool InImage(ulong address, ulong moduleBase) =>
        (address & 7) == 0 && address >= moduleBase && address - moduleBase < ImageSize;

    private static bool IsObject(ulong address) =>
        (address & 7) == 0 && address is >= 0x10000 and <= 0x00007FFFFFFFFFF8;

    private enum Mount
    {
        Unknown,
        Present,
        Missing
    }

    private readonly record struct HudSnapshot(ulong Service, ulong Dependency, uint State, ulong Page, bool Visible);

    private readonly record struct NestedSnapshot(uint State, ulong Page);

    private readonly struct Mem
    {
        private readonly IntPtr _process;

        public Mem(IntPtr process) => _process = process;

        public bool U64(ulong address, out ulong value)
        {
            value = 0;
            if (!Bytes(address, 8, out var bytes))
                return false;
            value = BitConverter.ToUInt64(bytes, 0);
            return true;
        }

        public bool U32(ulong address, out uint value)
        {
            value = 0;
            if (!Bytes(address, 4, out var bytes))
                return false;
            value = BitConverter.ToUInt32(bytes, 0);
            return true;
        }

        public bool U8(ulong address, out byte value)
        {
            value = 0;
            if (!Bytes(address, 1, out var bytes))
                return false;
            value = bytes[0];
            return true;
        }

        public bool Bytes(ulong address, int size, out byte[] bytes)
        {
            bytes = new byte[size];
            if (address < 0x10000 || size <= 0)
                return false;
            return Native.ReadProcessMemory(_process, new IntPtr(unchecked((long)address)), bytes, size, out var read) && read == size;
        }
    }
}

internal sealed class GameLane : IDisposable
{
    private readonly object _patch = new();
    private readonly ProcessMemory _mem = new();
    private readonly SpeedPatch _speed;
    private readonly IntegrityGap _gap;
    private int _processId;
    private ulong _mainBase;
    private int _mainSize;
    private bool _needsAdmin;
    private long _nextPollTicks;

    private volatile bool _disposed;
    private System.Threading.Timer? _gapTimer;
    private int _gapRunning;

    private ulong _vehicle;
    private ulong _identity;
    private long _settleUntilTicks;

    public GameLane()
    {
        _speed = new SpeedPatch(_mem);
        _gap = new IntegrityGap(_mem);
    }

    public bool NeedsAdmin => _needsAdmin;
    public bool IsHooked => _speed.Installed;
    public int ProcessId => _processId;

    public bool SpeedometerVisible() => SpeedometerHud.IsVisible(_mem.Handle, _mainBase, _mainSize);

    public void Poll()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now < _nextPollTicks)
            return;
        _nextPollTicks = now + TimeSpan.TicksPerMillisecond * 400;

        if (_speed.Installed)
        {
            if (!ProcessAlive(_processId))
                Abandon();
            return;
        }

        if (_mem.Handle != IntPtr.Zero)
        {
            if (!ProcessAlive(_processId))
                CloseProcess();
            return;
        }

        TryOpen();
    }

    public void EnsureHook()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GameLane));
        if (_speed.Installed && !ProcessAlive(_processId))
            Abandon();
        if (_speed.Installed && ProcessAlive(_processId))
            return;
        if (_mem.Handle == IntPtr.Zero && !TryOpen())
            throw new InvalidOperationException(_needsAdmin
                ? "Forza is running, but this app needs to be run as administrator."
                : "Forza Horizon 6 is not running.");

        var module = ReadModule();
        lock (_patch)
        {
            if (_disposed)
                return;
            if (_speed.Installed && ProcessAlive(_processId))
                return;
            try
            {
                _gap.Arm(module, _mainBase);
                _speed.Install(module, _mainBase);
                _vehicle = 0;
                _identity = 0;
                _settleUntilTicks = 0;
                StartGapTimer();
            }
            catch
            {
                RestorePatches();
                throw;
            }
        }
    }

    public bool TryReadSpeed(out float speed)
    {
        speed = 0;
        if (!_speed.Installed)
            return false;
        var vehicle = _speed.ReadBody();
        if (!LooksLikeVehicle(vehicle))
            return false;
        if (!TryReadVelocity(vehicle, out var vx, out _, out var vz))
            return false;
        speed = CruiseMath.HorizontalSpeed(vx, vz);
        return true;
    }

    public bool TryHold(
        float targetMps,
        float rampPerSecond,
        float catchUpPerSecond,
        float maxSpeed,
        bool brakeHeld,
        bool pauseOnImpact,
        long nowTicks,
        CruiseMemory memory,
        out float speed)
    {
        speed = 0;
        if (!_speed.Installed)
            return false;

        var vehicle = _speed.ReadBody();
        if (!IsWriteSafe(vehicle, nowTicks))
            return false;
        if (!_mem.Writable(vehicle + 0x20, 12))
            return false;
        if (!TryReadVelocity(vehicle, out var vx, out var vy, out var vz))
            return false;

        speed = CruiseMath.HorizontalSpeed(vx, vz);
        if (!CruiseMath.Step(vx, vy, vz, targetMps, rampPerSecond, catchUpPerSecond, maxSpeed, brakeHeld, pauseOnImpact, nowTicks, memory, out var newVx, out var newVz))
            return true;

        var bytes = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(newVx), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(vy), 0, bytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(newVz), 0, bytes, 8, 4);
        if (!_mem.TryWrite(vehicle + 0x20, bytes))
            return false;

        speed = CruiseMath.HorizontalSpeed(newVx, newVz);
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        StopTimer();
        lock (_patch)
            RestorePatches();
        CloseProcess();
    }

    private bool TryOpen()
    {
        Process? game = null;
        try
        {
            var found = Process.GetProcessesByName("ForzaHorizon6");
            if (found.Length == 0)
                found = Process.GetProcessesByName("forzahorizon6");
            game = found.FirstOrDefault();
            foreach (var extra in found)
            {
                if (!ReferenceEquals(extra, game))
                    extra.Dispose();
            }
        }
        catch
        {
            return false;
        }

        if (game == null)
            return false;

        _mem.Handle = Native.OpenProcess(Native.ProcessRuntimeAccess, false, (uint)game.Id);
        var id = game.Id;
        game.Dispose();
        if (_mem.Handle == IntPtr.Zero)
        {
            _needsAdmin = Marshal.GetLastWin32Error() == 5;
            return false;
        }

        _needsAdmin = false;
        _processId = id;
        if (!TryFindModule())
        {
            CloseProcess();
            return false;
        }

        return true;
    }

    private bool TryFindModule()
    {
        var modules = new IntPtr[512];
        if (!Native.EnumProcessModulesEx(_mem.Handle, modules, (uint)(modules.Length * IntPtr.Size), out var needed, Native.ListModulesAll))
            return false;
        var count = (int)(needed / (uint)IntPtr.Size);
        if (count > modules.Length)
        {
            modules = new IntPtr[count];
            if (!Native.EnumProcessModulesEx(_mem.Handle, modules, (uint)(modules.Length * IntPtr.Size), out needed, Native.ListModulesAll))
                return false;
            count = (int)(needed / (uint)IntPtr.Size);
        }

        var name = new System.Text.StringBuilder(260);
        for (var i = 0; i < count; i++)
        {
            name.Clear();
            if (Native.GetModuleBaseName(_mem.Handle, modules[i], name, (uint)name.Capacity) == 0)
                continue;
            if (!name.ToString().Equals("ForzaHorizon6.exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Native.GetModuleInformation(_mem.Handle, modules[i], out var info, (uint)Marshal.SizeOf<Native.ModuleInfo>()))
                return false;
            _mainBase = (ulong)info.BaseOfDll.ToInt64();
            _mainSize = (int)info.SizeOfImage;
            return _mainBase != 0 && _mainSize > 0;
        }

        return false;
    }

    private byte[] ReadModule()
    {
        if (_mainSize <= 0)
            throw new InvalidOperationException("Main module was not captured.");
        var module = new byte[_mainSize];
        const int chunk = 1024 * 1024;
        for (var offset = 0; offset < _mainSize; offset += chunk)
        {
            var size = Math.Min(chunk, _mainSize - offset);
            var piece = new byte[size];
            if (!Native.ReadProcessMemory(_mem.Handle, new IntPtr((long)(_mainBase + (ulong)offset)), piece, size, out var read) || read <= 0)
            {
                Array.Fill(module, (byte)0xFF, offset, size);
                continue;
            }
            Buffer.BlockCopy(piece, 0, module, offset, read);
            if (read < size)
                Array.Fill(module, (byte)0xFF, offset + read, size - read);
        }
        return module;
    }

    private void StartGapTimer()
    {
        _gapTimer ??= new System.Threading.Timer(GapTick, null, 10000, 10000);
    }

    private void GapTick(object? state)
    {
        if (Interlocked.Exchange(ref _gapRunning, 1) == 1)
            return;
        try
        {
            lock (_patch)
            {
                if (!_gap.Armed || _mem.Handle == IntPtr.Zero)
                    return;
                _speed.Cover();
                _gap.PutSaved();
            }

            Thread.Sleep(1000);

            lock (_patch)
            {
                if (!_gap.Armed || _mem.Handle == IntPtr.Zero)
                    return;
                _gap.PutStandIn();
                _speed.Reveal();
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _gapRunning, 0);
        }
    }

    private void StopTimer()
    {
        _gap.Disarm();
        var timer = _gapTimer;
        _gapTimer = null;
        timer?.Change(Timeout.Infinite, Timeout.Infinite);
        var waitUntil = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref _gapRunning) == 1 && DateTime.UtcNow < waitUntil)
            Thread.Sleep(10);
        timer?.Dispose();
    }

    private void RestorePatches()
    {
        _gap.Disarm();
        if (_mem.Handle != IntPtr.Zero && ProcessAlive(_processId))
        {
            try
            {
                _speed.Cover();
                _gap.PutSaved();
            }
            catch
            {
            }
        }

        _speed.FreeRemote();
        _speed.Drop();
        _gap.Drop();
        _vehicle = 0;
    }

    private void Abandon()
    {
        _speed.Drop();
        _gap.Drop();
        _gapTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _gapTimer?.Dispose();
        _gapTimer = null;
        CloseProcess();
    }

    private void CloseProcess()
    {
        if (_mem.Handle != IntPtr.Zero)
            Native.CloseHandle(_mem.Handle);
        _mem.Handle = IntPtr.Zero;
        _processId = 0;
        _mainBase = 0;
        _mainSize = 0;
    }

    private bool IsWriteSafe(ulong vehicle, long nowTicks)
    {
        if (!LooksLikeVehicle(vehicle))
        {
            _vehicle = 0;
            return false;
        }

        ulong identity;
        try { identity = _mem.ReadU64(vehicle); }
        catch { return false; }
        if (identity <= 0x10000 || identity >= 0x00007FFFFFFFFFFFUL)
        {
            _vehicle = 0;
            return false;
        }

        if (_vehicle == 0)
        {
            _vehicle = vehicle;
            _identity = identity;
            _settleUntilTicks = 0;
            return true;
        }

        if (vehicle != _vehicle || identity != _identity)
        {
            _vehicle = vehicle;
            _identity = identity;
            _settleUntilTicks = nowTicks + TimeSpan.TicksPerMillisecond * 400;
            return false;
        }

        return nowTicks >= _settleUntilTicks;
    }

    private bool LooksLikeVehicle(ulong vehicle)
    {
        return vehicle > 0x10000 && vehicle < 0x00007FFFFFFFFFFFUL && _mem.Readable(vehicle, 8);
    }

    private bool TryReadVelocity(ulong vehicle, out float vx, out float vy, out float vz)
    {
        vx = vy = vz = 0;
        var data = _mem.Read(vehicle + 0x20, 12);
        if (data == null)
            return false;
        vx = BitConverter.ToSingle(data, 0);
        vy = BitConverter.ToSingle(data, 4);
        vz = BitConverter.ToSingle(data, 8);
        if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(vz))
            return false;
        return (vx * vx) + (vz * vz) <= 2000f * 2000f;
    }

    private static bool ProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
