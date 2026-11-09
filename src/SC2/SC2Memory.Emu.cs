using System.Buffers.Binary;
using Iced.Intel;

namespace SC2Radar.SC2
{
    public sealed partial class SC2Memory
    {
        /// <summary>
        /// A tiny x64 interpreter for the game's small pointer-decoding routines (straight-line integer code: mov / xor /
        /// add / shifts / table lookups), run against live memory over DMA. The game picks one of several decoders at
        /// start-up, so instead of porting each one the radar runs whichever is selected.
        ///   - memory reads go to the game (DMA) unless the address was written during the run (writes stay local)
        ///   - calls are skipped (the decoders only call the anti-tamper check) and return al = 1
        ///   - conditional branches are taken (they only skip that same anti-tamper call)
        /// </summary>
        sealed class MiniEmu(SC2Memory mem, List<(uint Rva, byte[] Data)>? code)
        {
            readonly ulong[] _r = new ulong[16];
            readonly Dictionary<ulong, byte> _written = [];
            public const ulong OutBuf = 0x0000_1000_0000_0000, Stack = 0x0000_1100_0000_0000;

            static int Idx(Register r) => r.GetFullRegister() switch
            {
                Register.RAX => 0, Register.RCX => 1, Register.RDX => 2, Register.RBX => 3, Register.RSP => 4, Register.RBP => 5,
                Register.RSI => 6, Register.RDI => 7, Register.R8 => 8, Register.R9 => 9, Register.R10 => 10, Register.R11 => 11,
                Register.R12 => 12, Register.R13 => 13, Register.R14 => 14, Register.R15 => 15, _ => -1,
            };

            ulong GetReg(Register r)
            {
                int i = Idx(r); if (i < 0) throw new NotSupportedException(r.ToString());
                ulong v = _r[i];
                return r.GetSize() switch
                {
                    1 => r is >= Register.AH and <= Register.BH ? (v >> 8) & 0xFF : v & 0xFF,
                    2 => v & 0xFFFF, 4 => v & 0xFFFFFFFF, _ => v,
                };
            }

            void SetReg(Register r, ulong v)
            {
                int i = Idx(r); if (i < 0) throw new NotSupportedException(r.ToString());
                switch (r.GetSize())
                {
                    case 1:
                        if (r is >= Register.AH and <= Register.BH) _r[i] = (_r[i] & ~0xFF00UL) | ((v & 0xFF) << 8);
                        else _r[i] = (_r[i] & ~0xFFUL) | (v & 0xFF);
                        break;
                    case 2: _r[i] = (_r[i] & ~0xFFFFUL) | (v & 0xFFFF); break;
                    case 4: _r[i] = v & 0xFFFFFFFF; break;   // 32-bit writes zero the top half
                    default: _r[i] = v; break;
                }
            }

            ulong Read(ulong a, int size)
            {
                ulong v = 0;
                byte[]? live = null;
                for (int k = 0; k < size; k++)
                {
                    byte b;
                    if (!_written.TryGetValue(a + (ulong)k, out b))
                    {
                        if (live == null && (live = Preloaded(a, size)) == null)
                        {
                            live = mem.ReadData(a, size) ?? throw new InvalidOperationException($"read 0x{a:X}");
                            LiveReads?.Add((a, size));
                        }
                        b = live[k];
                    }
                    v |= (ulong)b << (8 * k);
                }
                return v;
            }

            void Write(ulong a, int size, ulong v)
            {
                for (int k = 0; k < size; k++) _written[a + (ulong)k] = (byte)(v >> (8 * k));
            }

            ulong Addr(in Instruction x) =>
                x.IsIPRelativeMemoryOperand ? x.IPRelativeMemoryAddress
                : (x.MemoryBase != Register.None ? GetReg(x.MemoryBase) : 0) + (x.MemoryIndex != Register.None ? GetReg(x.MemoryIndex) * (ulong)x.MemoryIndexScale : 0) + x.MemoryDisplacement64;

            static int OpSize(in Instruction x, int op) =>
                x.GetOpKind(op) == OpKind.Register ? x.GetOpRegister(op).GetSize() : x.GetOpKind(op) == OpKind.Memory ? x.MemorySize.GetSize() : 8;

            ulong Get(in Instruction x, int op) => x.GetOpKind(op) switch
            {
                OpKind.Register => GetReg(x.GetOpRegister(op)),
                OpKind.Memory => Read(Addr(x), x.MemorySize.GetSize()),
                _ => x.GetImmediate(op),
            };

            void Set(in Instruction x, int op, ulong v)
            {
                if (x.GetOpKind(op) == OpKind.Register) SetReg(x.GetOpRegister(op), v);
                else Write(Addr(x), x.MemorySize.GetSize(), v);
            }

            static ulong Mask(int size) => size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;

            byte[]? CodeAt(ulong va, out ulong start)
            {
                start = 0;
                ulong rva = va - mem._exeBase;
                if (code != null)
                {
                    foreach (var (r, d) in code)
                        if (rva >= r && rva < r + (ulong)d.Length) { start = mem._exeBase + r; return d; }
                    return null;
                }
                // no code image: fetch the page (and the next, for instructions crossing it) over DMA, cached (exe code per process)
                ulong page = va & ~0xFFFUL;
                if (!_pages.TryGetValue(page, out var bytes))
                {
                    bytes = mem.CodePage(page);
                    if (bytes == null) return null;
                    _pages[page] = bytes;
                }
                start = page;
                return bytes;
            }
            readonly Dictionary<ulong, byte[]> _pages = [];
            readonly List<(ulong Addr, byte[] Data)> _preload = [];

            /// <summary>Memory the caller already read (a batched read): the emulator takes it from here instead of over DMA.</summary>
            public void Preload(ulong addr, byte[] data) => _preload.Add((addr, data));

            /// <summary>When set, the memory the emulator read over DMA itself (not preloaded) - for a caller that
            /// preloads it from the next batch on.</summary>
            public List<(ulong Addr, int Size)>? LiveReads;

            byte[]? Preloaded(ulong a, int size)
            {
                foreach (var (at, d) in _preload)
                    if (a >= at && a + (ulong)size <= at + (ulong)d.Length) return d[(int)(a - at)..((int)(a - at) + size)];
                return null;
            }

            /// <summary>Run the function at exe+<paramref name="rva"/> with rcx / rdx set (and optionally a u64 placed in local
            /// memory first, for routines reading their input through a pointer); true if it returned.</summary>
            public bool Call(ulong rva, ulong rcx, ulong rdx, int maxSteps = 400, (ulong Addr, ulong Value)? input = null)
            {
                Array.Clear(_r);
                _written.Clear();
                if (input is { } inp) Write(inp.Addr, 8, inp.Value);
                _r[1] = rcx; _r[2] = rdx; _r[4] = Stack + 0x1000;
                Write(_r[4], 8, 0xDEAD);   // return address
                ulong ip = mem._exeBase + rva;
                for (int step = 0; step < maxSteps; step++)
                {
                    var data = CodeAt(ip, out ulong secVa);
                    if (data == null) return false;
                    var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(data, (int)(ip - secVa), data.Length - (int)(ip - secVa)), ip);
                    dec.Decode(out var x);
                    ulong next = dec.IP;
                    int sz = x.OpCount > 0 ? OpSize(x, 0) : 8;
                    switch (x.Mnemonic)
                    {
                        case Mnemonic.Mov: Set(x, 0, Get(x, 1)); break;
                        case Mnemonic.Movzx: Set(x, 0, Get(x, 1)); break;
                        case Mnemonic.Movsxd:
                        case Mnemonic.Movsx:
                        {
                            int s = OpSize(x, 1);
                            long v = s switch { 1 => (sbyte)Get(x, 1), 2 => (short)Get(x, 1), _ => (int)Get(x, 1) };
                            Set(x, 0, (ulong)v);
                            break;
                        }
                        case Mnemonic.Lea: Set(x, 0, Addr(x)); break;
                        case Mnemonic.Xor: Set(x, 0, (Get(x, 0) ^ Get(x, 1)) & Mask(sz)); break;
                        case Mnemonic.And: Set(x, 0, Get(x, 0) & Get(x, 1) & Mask(sz)); break;
                        case Mnemonic.Or:  Set(x, 0, (Get(x, 0) | Get(x, 1)) & Mask(sz)); break;
                        case Mnemonic.Add: Set(x, 0, (Get(x, 0) + Get(x, 1)) & Mask(sz)); break;
                        case Mnemonic.Sub: Set(x, 0, (Get(x, 0) - Get(x, 1)) & Mask(sz)); break;
                        case Mnemonic.Imul when x.OpCount == 3: Set(x, 0, (Get(x, 1) * Get(x, 2)) & Mask(sz)); break;
                        case Mnemonic.Imul when x.OpCount == 2: Set(x, 0, (Get(x, 0) * Get(x, 1)) & Mask(sz)); break;
                        case Mnemonic.Not: Set(x, 0, ~Get(x, 0) & Mask(sz)); break;
                        case Mnemonic.Neg: Set(x, 0, (0 - Get(x, 0)) & Mask(sz)); break;
                        case Mnemonic.Inc: Set(x, 0, (Get(x, 0) + 1) & Mask(sz)); break;
                        case Mnemonic.Dec: Set(x, 0, (Get(x, 0) - 1) & Mask(sz)); break;
                        case Mnemonic.Shr: Set(x, 0, (Get(x, 0) & Mask(sz)) >> (int)(Get(x, 1) & 63)); break;
                        case Mnemonic.Shl: Set(x, 0, (Get(x, 0) << (int)(Get(x, 1) & 63)) & Mask(sz)); break;
                        case Mnemonic.Sar:
                        {
                            int n = (int)(Get(x, 1) & 63);
                            long v = sz switch { 1 => (sbyte)Get(x, 0), 2 => (short)Get(x, 0), 4 => (int)Get(x, 0), _ => (long)Get(x, 0) };
                            Set(x, 0, (ulong)(v >> n) & Mask(sz));
                            break;
                        }
                        case Mnemonic.Rol:
                        case Mnemonic.Ror:
                        {
                            int bits = sz * 8, n = (int)(Get(x, 1) % (ulong)bits);
                            ulong v = Get(x, 0) & Mask(sz);
                            ulong r = x.Mnemonic == Mnemonic.Rol ? (v << n) | (v >> (bits - n)) : (v >> n) | (v << (bits - n));
                            Set(x, 0, n == 0 ? v : r & Mask(sz));
                            break;
                        }
                        case Mnemonic.Bswap: Set(x, 0, sz == 4 ? BinaryPrimitives.ReverseEndianness((uint)Get(x, 0)) : BinaryPrimitives.ReverseEndianness(Get(x, 0))); break;
                        case Mnemonic.Xchg: { ulong a = Get(x, 0), b = Get(x, 1); Set(x, 0, b); Set(x, 1, a); break; }
                        case Mnemonic.Push: _r[4] -= 8; Write(_r[4], 8, Get(x, 0)); break;
                        case Mnemonic.Pop: Set(x, 0, Read(_r[4], 8)); _r[4] += 8; break;
                        case Mnemonic.Cmp: case Mnemonic.Test: case Mnemonic.Nop: break;
                        case Mnemonic.Cdqe: _r[0] = (ulong)(long)(int)_r[0]; break;
                        case Mnemonic.Call: SetReg(Register.RAX, 1); break;   // anti-tamper check: "passed"
                        case Mnemonic.Ret:
                            if (Read(_r[4], 8) == 0xDEAD) return true;
                            ip = Read(_r[4], 8); _r[4] += 8; continue;
                        default:
                            if (x.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch) { ip = x.NearBranchTarget; continue; }
                            throw new NotSupportedException($"{x.Mnemonic} at exe+0x{x.IP - mem._exeBase:X}");
                    }
                    ip = next;
                }
                return false;
            }

            public ulong ReadOut(ulong offset, int size) => Read(OutBuf + offset, size);
        }
    }
}
