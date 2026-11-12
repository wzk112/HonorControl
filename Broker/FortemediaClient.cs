using System.Runtime.InteropServices;

namespace HonorControl;

// Fortemedia local RPC v1. Only endpoint capability, pickup flags and session lifetime are used.
// The NDR descriptions encode the service protocol, with Windows RPC performing marshalling.
internal sealed class FortemediaClient : IDisposable
{
    private static readonly NativeProtocol Protocol = new();
    private readonly string _endpoint;
    private nint _binding;
    private nint _context;

    public FortemediaClient(string endpoint)
    {
        if (!Guid.TryParse(endpoint, out _)) throw new ArgumentException("无效的麦克风端点。");
        _endpoint = endpoint;
        try
        {
            Check(RpcStringBindingComposeW(null, "ncalrpc", null, null, null, out nint text));
            try { Check(RpcBindingFromStringBindingW(text, out _binding)); }
            finally { RpcStringFreeW(ref text); }
            Check(RpcBindingSetOption(_binding, 12, 3000)); // RPC_C_OPT_CALL_TIMEOUT
            if (Open(Protocol.Stub, Protocol.Procedure(0), _binding, out _context) != 0 || _context == 0 ||
                Supports(Protocol.Stub, Protocol.Procedure(0x9c), _context, _endpoint) != 1)
                throw new InvalidOperationException("Fortemedia 服务未确认内置麦克风支持。");
        }
        catch { Dispose(); throw; }
    }

    public int Mode()
    {
        if (Read(6)) return 19;
        if (!Read(1)) return -1;
        return Read(0) ? 2 : 3;
    }

    public int Apply(int mode)
    {
        if (mode is not (2 or 3 or 19)) throw new ArgumentException("未知的麦克风拾音模式。");
        if (Mode() == mode) return mode;
        bool[] original = Enumerable.Range(0, 7).Select(Read).ToArray();
        try
        {
            if (mode == 19) Set(6, true);
            else
            {
                Set(6, false);
                Set(0, mode == 2);
                Set(1, true);
            }
            if (Mode() != mode) throw new InvalidOperationException("麦克风拾音模式读回不一致。");
            return mode;
        }
        catch (Exception operationError)
        {
            try
            {
                Set(6, false);
                for (int flag = 0; flag < 6; flag++) Set(flag, original[flag]);
                if (original[6]) Set(6, true);
                if (!Enumerable.Range(0, 7).Select(Read).SequenceEqual(original))
                    throw new InvalidOperationException("麦克风原始处理状态读回不一致。");
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("拾音切换及恢复未完成，请重新读取状态。", operationError, restoreError);
            }
            throw;
        }
    }

    private bool Read(int flag)
    {
        if (ReadFlag(Protocol.Stub, Protocol.Procedure(0x10c), _context, _endpoint, flag, out int enabled) != 1)
            throw new InvalidOperationException("无法读取 Fortemedia 麦克风处理状态。");
        return enabled != 0;
    }

    private void Set(int flag, bool enabled)
    {
        if (SetFlag(Protocol.Stub, Protocol.Procedure(0xce), _context, _endpoint, flag, enabled ? 1 : 0) != 1)
            throw new InvalidOperationException("Fortemedia 服务拒绝拾音模式设置。");
    }

    public void Dispose()
    {
        try { if (_context != 0) Close(Protocol.Stub, Protocol.Procedure(0x24), ref _context); }
        finally
        {
            _context = 0;
            if (_binding != 0) RpcBindingFree(ref _binding);
        }
    }

    private static void Check(uint error)
    {
        if (error != 0) throw new System.ComponentModel.Win32Exception(unchecked((int)error), "无法连接 Fortemedia 音频服务。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StubDescription
    {
        public nint Interface, Allocate, Free, AutoHandle, Rundown, Generic, Expressions, Transmit, Types;
        public uint CheckBounds, Version;
        public nint MallocFree;
        public uint MidlVersion;
        public nint Communication, UserMarshal, Notify, Flags, CodeSets, Proxy, ExpressionInfo;
    }

    private sealed class NativeProtocol
    {
        private readonly nint _procedures;
        public nint Stub { get; }
        private readonly Allocate _allocate = Marshal.AllocHGlobal;
        private readonly Free _free = Marshal.FreeHGlobal;

        public NativeProtocol()
        {
            // RPC_CLIENT_INTERFACE, protocol UUID v1 and the standard NDR transfer syntax v2.
            byte[] rpcInterface = new byte[96];
            BitConverter.GetBytes(96).CopyTo(rpcInterface, 0);
            new Guid("944be8cf-bd2b-46e4-90a9-2b947b152a6d").ToByteArray().CopyTo(rpcInterface, 4);
            rpcInterface[20] = 1;
            new Guid("8a885d04-1ceb-11c9-9fe8-08002b104860").ToByteArray().CopyTo(rpcInterface, 24);
            rpcInterface[40] = 2;
            var description = new StubDescription
            {
                Interface = Copy(rpcInterface),
                Allocate = Marshal.GetFunctionPointerForDelegate(_allocate),
                Free = Marshal.GetFunctionPointerForDelegate(_free),
                Types = Copy(Convert.FromHexString(
                    "00001104020030a400001104020030e50000304500001108255c110c085c110002001b01020040000002010000000000000000000000055bb7080000000000000040110002001b00010029001800110001000000000000000040025bb7080000000000000040110002001c0001002900280011000100000000000000004029543000010000000000000000000000025bb7080000000000000040110002001c0102002900200011000100000000000000004029542800010000000000000000000000055b0000")),
                CheckBounds = 1, Version = 0x60001, MidlVersion = 0x8010274, Flags = 1
            };
            _procedures = Copy(Convert.FromHexString(
                "00480000000000001000320000000000380040010a4100000000000000001001080006000048000000000100080030e4000000003800380040010a410000000000000000180100000e00004800000000020010003044000000003400000040020a410000000000000000080000001200480008000b00004800000000030008003044000000002400000040010a410000000000000000080000001200004800000000040018003044000000002400080046030a4100000000000000000800000012000b0108001800700010000800004800000000050028003044000000003400080046050a4100000000000000000800000012000b0108001800480010000800480018000800700020000800004800000000060028003044000000002c00240046050a4100000000000000000800000012000b0108001800480010000800502118000800700020000800"));
            Stub = Marshal.AllocHGlobal(Marshal.SizeOf<StubDescription>());
            Marshal.StructureToPtr(description, Stub, false);
        }

        public nint Procedure(int offset) => _procedures + offset;
        private static nint Copy(byte[] bytes)
        {
            nint memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            return memory;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint Allocate(nint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Free(nint memory);
    [DllImport("rpcrt4.dll", CharSet = CharSet.Unicode)] private static extern uint RpcStringBindingComposeW(string? uuid, string protocol, string? address, string? endpoint, string? options, out nint binding);
    [DllImport("rpcrt4.dll", CharSet = CharSet.Unicode)] private static extern uint RpcBindingFromStringBindingW(nint text, out nint binding);
    [DllImport("rpcrt4.dll")] private static extern uint RpcBindingSetOption(nint binding, uint option, nuint value);
    [DllImport("rpcrt4.dll")] private static extern uint RpcStringFreeW(ref nint text);
    [DllImport("rpcrt4.dll")] private static extern uint RpcBindingFree(ref nint binding);
    [DllImport("rpcrt4.dll", EntryPoint = "NdrClientCall2")] private static extern nint Open(nint stub, nint format, nint binding, out nint context);
    [DllImport("rpcrt4.dll", EntryPoint = "NdrClientCall2")] private static extern nint Close(nint stub, nint format, ref nint context);
    [DllImport("rpcrt4.dll", EntryPoint = "NdrClientCall2", CharSet = CharSet.Unicode)] private static extern nint Supports(nint stub, nint format, nint context, string endpoint);
    [DllImport("rpcrt4.dll", EntryPoint = "NdrClientCall2", CharSet = CharSet.Unicode)] private static extern nint ReadFlag(nint stub, nint format, nint context, string endpoint, int flag, out int value);
    [DllImport("rpcrt4.dll", EntryPoint = "NdrClientCall2", CharSet = CharSet.Unicode)] private static extern nint SetFlag(nint stub, nint format, nint context, string endpoint, int flag, int enabled);
}
