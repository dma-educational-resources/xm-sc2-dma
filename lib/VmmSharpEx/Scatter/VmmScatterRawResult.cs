/*
 *  VmmSharpEx by Lone (Lone DMA)
 *  Copyright (C) 2025 AGPL-3.0
*/

using VmmSharpEx.Internal;

namespace VmmSharpEx.Scatter;

/// <summary>
/// Result of <see cref="Vmm.MemReadScatterRaw"/>. Wraps the native scatter allocation directly
/// -- no per-page managed copy is made. <see cref="TryGet"/> returns spans backed by native
/// memory that are only valid until this handle is disposed; read out of them before disposing.
/// </summary>
public sealed unsafe class VmmScatterRawResult : IDisposable
{
    private readonly IntPtr _pppMEMs;
    private readonly int _count;
    private bool _disposed;

    internal VmmScatterRawResult(IntPtr pppMEMs, int count)
    {
        _pppMEMs = pppMEMs;
        _count = count;
    }

    /// <summary>Number of results (one per requested page, in request order).</summary>
    public int Count => _count;

    /// <summary>
    /// Gets the i-th result. <paramref name="pb"/>/<paramref name="cb"/> describe a native
    /// buffer that is only valid until this handle is disposed -- copy out of it before then.
    /// </summary>
    public bool TryGet(int index, out ulong qwA, out bool success, out IntPtr pb, out uint cb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ppMEMs = (LeechCore.MEM_SCATTER_NATIVE**)_pppMEMs.ToPointer();
        var pMEM = ppMEMs[index];
        if (pMEM is null)
        {
            qwA = 0;
            success = false;
            pb = IntPtr.Zero;
            cb = 0;
            return false;
        }

        qwA = pMEM->qwA;
        success = pMEM->f;
        pb = success ? pMEM->pb : IntPtr.Zero;
        cb = success ? pMEM->cb : 0;
        return true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Lci.LcMemFree(_pppMEMs.ToPointer());
        }
    }
}
