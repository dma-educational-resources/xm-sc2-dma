/*  
 *  VmmSharpEx by Lone (Lone DMA)
 *  Copyright (C) 2025 AGPL-3.0
*/

/*
MIT License

Copyright (c) 2023 Metick

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */

// Thanks to Metick for the original implementation! https://github.com/Metick/DMALibrary/blob/Master/DMALibrary/Memory/InputManager.cpp

using System.Runtime.CompilerServices;

namespace VmmSharpEx.Extensions.Input
{
    /// <summary>
    /// Extension class that queries user input state via Win32 Kernel Interop (Read-Only).
    /// <para>
    /// Supports both Windows 10 and Windows 11.
    /// Win11 (build ≥ 22000): resolves gafAsyncKeyStateExport via win32k session pointer chain.
    /// Win10 (build &lt; 22000): resolves gafAsyncKeyState via win32kbase.sys EAT export or signature scan.
    /// </para>
    /// </summary>
    public sealed class VmmInputManager
    {
        private readonly Vmm _vmm;
        private readonly Action<string>? _log;

        private readonly byte[] _stateBitmap = new byte[64];
        private readonly byte[] _previousStateBitmap = new byte[256 / 8];
        private readonly ulong _gafAsyncKeyStateExport;
        private readonly uint _winLogonPid;

        /// <summary>The target machine's Windows build number detected during initialization.</summary>
        public int TargetBuildNumber { get; }

        /// <summary>The resolution method that was used to find gafAsyncKeyState.</summary>
        public string ResolutionMethod { get; private set; } = "Unknown";

        private VmmInputManager() { throw new NotImplementedException(); }

        /// <summary>
        /// Extension class that queries user input state via Win32 Kernel Interop (Read-Only).
        /// Supports both Windows 10 and Windows 11.
        /// </summary>
        /// <param name="vmm">Parent VMM Instance (must be already initialized).</param>
        /// <param name="log">Optional diagnostic logging callback for troubleshooting resolution steps.</param>
        /// <exception cref="VmmException"></exception>
        public VmmInputManager(Vmm vmm, Action<string>? log = null)
        {
            _vmm = vmm;
            _log = log;

            if (!_vmm.PidGetFromName("winlogon.exe", out _winLogonPid))
                throw new VmmException("Failed to get winlogon.exe PID");

            TargetBuildNumber = GetTargetBuildNumber();
            _log?.Invoke($"Target build: {TargetBuildNumber}, winlogon PID: {_winLogonPid}");

            // Try the expected path first based on the target OS build number,
            // then fall back to the other path. This handles misdetection and
            // edge-case builds (e.g. Win10 with win32ksgd.sys backport).
            ulong gafAsyncKeyState = 0;
            if (TargetBuildNumber >= 22000)
            {
                _log?.Invoke("Attempting Win11 resolution (build >= 22000)...");
                if (!TryResolveWin11KeyState(out gafAsyncKeyState))
                {
                    _log?.Invoke("Win11 failed, falling back to Win10...");
                    TryResolveWin10KeyState(out gafAsyncKeyState);
                }
            }
            else
            {
                _log?.Invoke($"Attempting Win10 resolution (build {TargetBuildNumber})...");
                if (!TryResolveWin10KeyState(out gafAsyncKeyState))
                {
                    _log?.Invoke("Win10 failed, falling back to Win11...");
                    TryResolveWin11KeyState(out gafAsyncKeyState);
                }
            }

            if (gafAsyncKeyState == 0)
                throw new VmmException($"Failed to resolve gafAsyncKeyState via Win11 or Win10 methods (build {TargetBuildNumber})");

            _gafAsyncKeyStateExport = gafAsyncKeyState;
            _log?.Invoke($"Resolved gafAsyncKeyState @ 0x{gafAsyncKeyState:X} via {ResolutionMethod}");
        }

        /// <summary>
        /// Updates the internal key state bitmap. Call this method periodically to refresh key states.
        /// </summary>
        /// <remarks>
        /// Typically you would call this before you make <see cref="IsKeyDown(uint)"/> calls.
        /// </remarks>
        /// <exception cref="VmmException"></exception>
        public bool UpdateKeys()
        {
            Span<byte> previousKeyStateBitmap = stackalloc byte[64];
            _stateBitmap.CopyTo(previousKeyStateBitmap);

            // Read 64 bytes from gafAsyncKeyStateExport - silently skip on transient DMA failure
            // (matches WPF behaviour: bad reads return false and the tick is skipped, no throw).
            if (!_vmm.MemReadSpan(_winLogonPid | Vmm.PID_PROCESS_WITH_KERNELMEMORY, _gafAsyncKeyStateExport, _stateBitmap, VmmSharpEx.Options.VmmFlags.NOCACHE))
                return false;

            for (int vk = 0; vk < 256; ++vk)
                if ((_stateBitmap[(vk * 2 / 8)] & (1 << ((vk % 4) * 2))) != 0 && (previousKeyStateBitmap[(vk * 2 / 8)] & (1 << ((vk % 4) * 2))) == 0)
                    _previousStateBitmap[vk / 8] |= (byte)(1 << (vk % 8));
            return true;
        }

        /// <summary>
        /// Checks if a given Virtual Key is currently down.
        /// See: <see href="https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes"/>
        /// </summary>
        /// <remarks>
        /// Recommend calling <see cref="UpdateKeys"/> before calling this method to ensure the key states are up-to-date.
        /// </remarks>
        /// <param name="vkey">Windows virtual key.</param>
        /// <returns><see langword="true"/> if key is down, otherwise <see langword="false"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsKeyDown(Win32VirtualKey vkey) => IsKeyDown((uint)vkey);

        /// <summary>
        /// Checks if a given Virtual Key is currently down.
        /// See: <see href="https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes"/>
        /// </summary>
        /// <remarks>
        /// Recommend calling <see cref="UpdateKeys"/> before calling this method to ensure the key states are up-to-date.
        /// </remarks>
        /// <param name="vkeyCode">Windows virtual key code.</param>
        /// <returns><see langword="true"/> if key is down, otherwise <see langword="false"/>.</returns>
        public bool IsKeyDown(uint vkeyCode)
        {
            if (!_gafAsyncKeyStateExport.IsValidKernelVA())
                return false;
            int idx = (int)(vkeyCode * 2 / 8);
            int bit = 1 << ((int)vkeyCode % 4 * 2);
            return (_stateBitmap[idx] & bit) != 0;
        }

        #region Signature scanning

        /// <summary>
        /// Find a byte pattern within a PID's memory range.
        /// Tries VMMDLL_MemSearch first (fast native scan), then falls back to a
        /// manual chunked MemRead + pattern scan - the same approach the WPF version
        /// uses, which works on FPGA configs where VMMDLL_MemSearch returns no results.
        /// </summary>
        private ulong FindSig(uint pid, string pattern, ulong addrMin, ulong addrMax)
        {
            ulong native = _vmm.FindSignature(pid, pattern, addrMin, addrMax);
            if (native != 0) return native;
            return ManualFindSig(pid, pattern, addrMin, addrMax);
        }

        private ulong ManualFindSig(uint pid, string pattern, ulong addrMin, ulong addrMax)
        {
            // Parse the IDA-style pattern into a nullable-byte array.
            var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pat   = new byte?[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                pat[i] = parts[i].StartsWith('?')
                    ? (byte?)null
                    : byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber);

            const uint CHUNK   = 0x200000u; // 2 MB
            int        overlap = Math.Max(pat.Length - 1, 0);

            for (ulong addr = addrMin; addr < addrMax;)
            {
                ulong chunkEnd = Math.Min(addr + CHUNK, addrMax);
                uint  size     = (uint)(chunkEnd - addr);

                byte[]? buf = _vmm.MemRead(pid, addr, size, out _, VmmSharpEx.Options.VmmFlags.NOCACHE);
                if (buf != null && buf.Length >= pat.Length)
                {
                    int last = buf.Length - pat.Length;
                    for (int i = 0; i <= last; i++)
                    {
                        bool ok = true;
                        for (int j = 0; j < pat.Length; j++)
                        {
                            if (pat[j].HasValue && buf[i + j] != pat[j].Value) { ok = false; break; }
                        }
                        if (ok) return addr + (ulong)i;
                    }
                }

                // Advance with overlap so patterns that straddle a chunk boundary aren't missed.
                ulong next = chunkEnd > (ulong)overlap ? chunkEnd - (ulong)overlap : chunkEnd;
                if (next <= addr) break; // safety: avoid infinite loop
                addr = next;
            }
            return 0;
        }

        #endregion

        #region Win11 Resolution

        // Patterns that locate the gSessionGlobalSlots pointer in win32ksgd.sys / win32k.sys.
        // These are the same two signatures used by the Pub version which is known to work.
        private static readonly string[] _win11SessionPtrSigs =
        [
            "48 8B 05 ?? ?? ?? ?? 48 8B 04 C8",   // 21H2–23H2
            "48 8B 05 ?? ?? ?? ?? FF C9",           // some 22H2 builds
        ];

        // Pattern that encodes the gafAsyncKeyState offset within the session state structure.
        private static readonly string[] _win11SessionOffsetSigs =
        [
            "48 8D 90 ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F 57 C0",   // 21H2–23H2
        ];

        private bool TryResolveWin11KeyState(out ulong result)
        {
            result = 0;

            uint[]? csrssPids;
            try { csrssPids = _vmm.PidGetAllFromName("csrss.exe"); }
            catch { return false; }
            if (csrssPids is null || csrssPids.Length == 0)
                return false;

            foreach (uint pid in csrssPids)
            {
                try
                {
                    // Session-pointer chain (the Win11-specific path).
                    // Try win32ksgd.sys first, then win32k.sys.
                    Vmm.ModuleEntry win32kModuleInfo = default;
                    bool foundWin32k =
                        _vmm.Map_GetModuleFromName(pid, "win32ksgd.sys", out win32kModuleInfo) ||
                        _vmm.Map_GetModuleFromName(pid, "win32k.sys",    out win32kModuleInfo);
                    if (!foundWin32k)
                        continue;

                    ulong win32kBase = win32kModuleInfo.vaBase;
                    ulong win32kEnd  = win32kBase + win32kModuleInfo.cbImageSize;

                    // Try every known gSessionGlobalSlots signature in priority order.
                    ulong gSessionPtr = 0;
                    foreach (var sig in _win11SessionPtrSigs)
                    {
                        gSessionPtr = FindSig(pid, sig, win32kBase, win32kEnd);
                        if (gSessionPtr != 0) break;
                    }
                    if (gSessionPtr == 0)
                        continue;

                    if (!_vmm.MemReadValue<int>(pid, gSessionPtr + 3, out int relative))
                        continue;
                    ulong gSessionGlobalSlots = gSessionPtr + 7 + (ulong)relative;

                    // Walk session slots - triple dereference: gSessionGlobalSlots → slot array → slot entry → session state.
                    // slot1 is an intermediate kernel pointer, not the session state itself, so we always dereference it.
                    ulong userSessionState = 0;
                    if (!_vmm.MemReadValue<ulong>(pid, gSessionGlobalSlots, out ulong slot0) || slot0 == 0)
                        continue;

                    for (int i = 0; i < 16 && userSessionState == 0; i++)
                    {
                        if (!_vmm.MemReadValue<ulong>(pid, slot0 + (ulong)(8 * i), out ulong slot1) || slot1 == 0)
                            continue;
                        if (_vmm.MemReadValue<ulong>(pid, slot1, out ulong slotVal) && slotVal.IsValidKernelVA())
                            userSessionState = slotVal;
                    }

                    if (userSessionState == 0)
                    {
                        _log?.Invoke($"Win11: csrss PID {pid} - slot walk found no valid session state");
                        continue;
                    }

                    if (!_vmm.Map_GetModuleFromName(pid, "win32kbase.sys", out var win32kbaseModule))
                        continue;
                    ulong win32kbaseBase = win32kbaseModule.vaBase;
                    ulong win32kbaseEnd  = win32kbaseBase + win32kbaseModule.cbImageSize;

                    // Try every known sessionOffset signature.
                    foreach (var sig in _win11SessionOffsetSigs)
                    {
                        ulong ptr = FindSig(pid, sig, win32kbaseBase, win32kbaseEnd);
                        if (ptr == 0) continue;

                        if (!_vmm.MemReadValue<uint>(pid, ptr + 3, out uint sessionOffset))
                            continue;

                        ulong candidate = userSessionState + sessionOffset;
                        if (candidate.IsValidKernelVA())
                        {
                            _log?.Invoke($"Win11: resolved via session pointer chain (csrss PID {pid}), sessionOffset 0x{sessionOffset:X} @ 0x{candidate:X}");
                            ResolutionMethod = "Win11-SessionPointer";
                            result = candidate;
                            return true;
                        }
                    }
                }
                catch
                {
                    // Try next csrss PID
                }
            }

            _log?.Invoke("Win11: all csrss PIDs exhausted without finding gafAsyncKeyState");
            return false;
        }

        #endregion

        #region Win10 Resolution

        private bool TryResolveWin10KeyState(out ulong result)
        {
            result = 0;
            try
            {
                uint kernelPid = _winLogonPid | Vmm.PID_PROCESS_WITH_KERNELMEMORY;

                // 1. EAT export lookup via winlogon kernel PID (fastest, works on most Win10 builds).
                _log?.Invoke("Win10: attempting EAT export lookup (winlogon kernel PID)...");
                if (TryEatLookup(kernelPid, out result))
                {
                    _log?.Invoke($"Win10: resolved via EAT @ 0x{result:X}");
                    ResolutionMethod = "Win10-EAT";
                    return true;
                }

                // 2. EAT export lookup via csrss PIDs - some FPGA/board configs don't expose
                //    win32kbase.sys through the winlogon kernel PID context but do via csrss.
                _log?.Invoke("Win10: trying EAT via csrss PIDs...");
                var csrssPids = _vmm.PidGetAllFromName("csrss.exe");
                if (csrssPids != null)
                {
                    foreach (uint cPid in csrssPids)
                    {
                        uint cKernelPid = cPid | Vmm.PID_PROCESS_WITH_KERNELMEMORY;
                        if (TryEatLookup(cKernelPid, out result))
                        {
                            _log?.Invoke($"Win10: resolved via csrss EAT (PID {cPid}) @ 0x{result:X}");
                            ResolutionMethod = "Win10-EAT-csrss";
                            return true;
                        }
                    }
                }

                // 3. PDB symbol lookup - fast and reliable when cached (matches Pub method order).
                _log?.Invoke("Win10: attempting PDB symbol resolution...");
                try
                {
                    ulong win32kbaseBase = 0;
                    if (_vmm.Map_GetModuleFromName(kernelPid, "win32kbase.sys", out var pdbMod))
                    {
                        win32kbaseBase = pdbMod.vaBase;
                        _log?.Invoke($"Win10: win32kbase.sys found @ 0x{win32kbaseBase:X}");
                    }
                    else
                    {
                        _log?.Invoke("Win10: win32kbase.sys not found via Map_GetModuleFromName");
                    }

                    if (win32kbaseBase != 0)
                    {
                        bool pdbLoaded = _vmm.PdbLoad(kernelPid, win32kbaseBase, out string? pdbName);
                        _log?.Invoke($"Win10: PdbLoad returned {pdbLoaded}, name='{pdbName}'");
                        if (pdbLoaded)
                        {
                            // Use the name returned by PdbLoad; fall back to "win32kbase" if empty.
                            string modName = string.IsNullOrEmpty(pdbName) ? "win32kbase" : pdbName;
                            bool symFound = _vmm.PdbSymbolAddress(modName, "gafAsyncKeyState", out ulong pdbAddr);
                            _log?.Invoke($"Win10: PdbSymbolAddress('{modName}') returned {symFound}, addr=0x{pdbAddr:X}");
                            if (symFound && pdbAddr.IsValidKernelVA())
                            {
                                _log?.Invoke($"Win10: resolved via PDB @ 0x{pdbAddr:X}");
                                ResolutionMethod = "Win10-PDB";
                                result = pdbAddr;
                                return true;
                            }
                            // Also try the hardcoded name if the dynamic name didn't work.
                            if (!symFound && modName != "win32kbase")
                            {
                                symFound = _vmm.PdbSymbolAddress("win32kbase", "gafAsyncKeyState", out pdbAddr);
                                _log?.Invoke($"Win10: PdbSymbolAddress('win32kbase') fallback returned {symFound}, addr=0x{pdbAddr:X}");
                                if (symFound && pdbAddr.IsValidKernelVA())
                                {
                                    _log?.Invoke($"Win10: resolved via PDB (fallback name) @ 0x{pdbAddr:X}");
                                    ResolutionMethod = "Win10-PDB";
                                    result = pdbAddr;
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Win10: PDB resolution threw: {ex.GetType().Name}: {ex.Message}");
                }

                // 4. Signature scan fallback - broadest coverage across builds.
                _log?.Invoke("Win10: attempting signature scan...");
                if (TryResolveWin10KeyStateScan(kernelPid, out result) && result.IsValidKernelVA())
                {
                    _log?.Invoke($"Win10: resolved via sig scan @ 0x{result:X}");
                    ResolutionMethod = "Win10-SigScan";
                    return true;
                }

                _log?.Invoke("Win10: all methods exhausted");
                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool TryEatLookup(uint pid, out ulong result)
        {
            result = 0;
            try
            {
                var entries = _vmm.Map_GetEAT(pid, "win32kbase.sys", out _);
                if (entries == null) return false;
                foreach (var e in entries)
                {
                    if (e.sFunction == "gafAsyncKeyState" && e.vaFunction.IsValidKernelVA())
                    {
                        result = e.vaFunction;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool TryResolveWin10KeyStateScan(uint kernelPid, out ulong result)
        {
            result = 0;
            ulong baseStart = 0, baseEnd = 0;
            uint scanPid = kernelPid;

            if (_vmm.Map_GetModuleFromName(kernelPid, "win32kbase.sys", out var kMod))
            {
                baseStart = kMod.vaBase;
                baseEnd = baseStart + kMod.cbImageSize;
            }
            else
            {
                var csrssPids = _vmm.PidGetAllFromName("csrss.exe");
                if (csrssPids != null)
                {
                    foreach (uint pid in csrssPids)
                    {
                        if (_vmm.Map_GetModuleFromName(pid, "win32kbase.sys", out var mod))
                        {
                            scanPid = pid;
                            baseStart = mod.vaBase;
                            baseEnd = baseStart + mod.cbImageSize;
                            break;
                        }
                    }
                }
            }

            if (baseStart == 0)
                return false;

            ulong ptr = FindSig(scanPid, "48 8B 05 ?? ?? ?? ?? 48 63 D1 0F B6 44 10 08", baseStart, baseEnd);
            if (ptr == 0)
                ptr = FindSig(scanPid, "48 8B 05 ?? ?? ?? ?? 48 63 D1 0F B6 44 10 00", baseStart, baseEnd);
            if (ptr == 0)
                ptr = FindSig(scanPid, "48 8B 05 ?? ?? ?? ?? 48 8B 04 C8 0F B6 04 10 83 E0 01", baseStart, baseEnd);
            if (ptr == 0)
                return false;

            if (!_vmm.MemReadValue<int>(kernelPid, ptr + 3, out int relative))
                return false;
            ulong candidate = ptr + 7 + (ulong)relative;
            if (candidate.IsValidKernelVA())
            {
                result = candidate;
                return true;
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Reads the Windows build number from the target machine's registry
        /// via DMA. Falls back to the local host registry if the DMA read fails.
        /// </summary>
        private int GetTargetBuildNumber()
        {
            // Prefer reading the target's registry through VMM.
            try
            {
                const string regPath = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\CurrentBuild";
                var data = _vmm.WinReg_QueryValue(regPath, out _);
                if (data is { Length: > 0 })
                {
                    // REG_SZ comes back as a null-terminated UTF-16 byte array.
                    string val = System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
                    if (int.TryParse(val, out int build))
                        return build;
                }
            }
            catch { }

            // Fallback: read the local (host) registry.
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
                var val = key?.GetValue("CurrentBuild") as string;
                if (int.TryParse(val, out int build))
                    return build;
            }
            catch { }

            // Default to Win11 so the more robust Win11 path is tried first.
            return 22000;
        }
    }
}
