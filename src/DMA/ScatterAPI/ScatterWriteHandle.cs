using VmmSharpEx.Options;
using VmmSharpEx.Scatter;
using SC2Radar.SC2;

namespace SC2Radar.DMA.ScatterAPI
{
    /// <summary>
    /// Batches memory writes into one VmmSharpEx scatter write. Reused across ticks (<see cref="Clear"/>);
    /// executing with no entries does nothing.
    /// </summary>
    public sealed class ScatterWriteHandle : IDisposable
    {
        private readonly VmmScatter _handle;
        private int _count;

        /// <summary>Callbacks run after a successful <see cref="Execute"/>. They must handle their own exceptions.</summary>
        public Action? Callbacks { get; set; }

        /// <summary>Entries queued since the last <see cref="Clear"/>.</summary>
        public int Count => _count;

        public ScatterWriteHandle(SC2Memory mem)
        {
            _handle = mem.CreateScatter(VmmFlags.NOCACHE);
        }

        /// <summary>Queue a buffer of <typeparamref name="T"/> to write at <paramref name="va"/>.</summary>
        public void AddBufferEntry<T>(ulong va, ReadOnlySpan<T> buffer)
            where T : unmanaged
        {
            if (!_handle.PrepareWriteSpan(va, MemoryMarshal.AsBytes(buffer)))
                throw new Exception("Failed to prepare write entry.");
            _count++;
        }

        /// <summary>Queue a value of <typeparamref name="T"/> to write at <paramref name="va"/>.</summary>
        public void AddValueEntry<T>(ulong va, T value)
            where T : unmanaged
        {
            if (!_handle.PrepareWriteValue(va, in value))
                throw new Exception("Failed to prepare write entry.");
            _count++;
        }

        /// <summary>Write every queued entry, then run <see cref="Callbacks"/>.</summary>
        /// <param name="validation">Checked right before writing; false writes nothing.</param>
        public void Execute(Func<bool> validation)
        {
            if (!Program.Config.MemWritesEnabled)
                throw new Exception("Memory writing is disabled.");
            if (_count == 0)
                return;
            if (!validation())
                throw new Exception("Validation callback returned false (nothing written).");

            _handle.Execute();
            Callbacks?.Invoke();
        }

        /// <summary>Clear the queued entries for re-use.</summary>
        public void Clear()
        {
            _count = 0;
            Callbacks = null;
            _handle.Clear(VmmFlags.NOCACHE);
        }

        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _handle.Dispose();
            }
        }
    }
}
