namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

/// <summary>
/// Widening the game past its 16:9 limit. Port of game.py.
///
/// StarCraft II caps its window and back buffer at a 1366:768 (~1.7786) aspect ratio. The
/// window-size clamp that enforces the cap skips clamping while the engine's client-API render
/// path is active, which two writable flags gate (the `listen` option and `gameStateRender`).
///
/// The Python tool sets those flags, resizes the window itself with SetWindowPos, and restores them
/// within milliseconds. Over DMA there is no way to call SetWindowPos on the game PC, so the Ultrawide
/// feature holds the flags on while you make the game re-apply its display mode on the game PC
/// (Options > Graphics > Display Mode, to Windowed and back to Windowed (Fullscreen)). With the
/// flags on, the game sizes its window to the full monitor; once the engine's stored size has been
/// wide for a moment, the flags are restored. The window size itself is read from the engine
/// (its stored width and height), not from the window, which DMA cannot see.
/// </summary>
static class WideMode
{
    public static readonly float OriginalMaxAspect = BitConverter.Int32BitsToSingle(0x3FE3AAAB); // 1366 / 768
    public const uint DisplayWindowed = 1;   // displaymode value for Windowed (Fullscreen)

    /// <summary>The client-API flags as the game had them, put back by <see cref="QueueBypassOff"/>.</summary>
    public sealed record Bypass(byte[] Listen, byte[] Render);

    /// <summary>True if a width x height window is wider than the game's own aspect limit.</summary>
    public static bool WiderThanCap(uint width, uint height) =>
        height != 0 && (double)width / height > OriginalMaxAspect + 0.01;

    /// <summary>The size the engine has stored for its window.</summary>
    public static (uint Width, uint Height) StoredSize(GameMemory game) =>
        (game.ReadU32(game.Addr!.StoredWidth), game.ReadU32(game.Addr.StoredHeight));

    public static bool IsWide(GameMemory game)
    {
        var (w, h) = StoredSize(game);
        return WiderThanCap(w, h);
    }

    /// <summary>The game sits in Windowed (Fullscreen) at a wide size; <paramref name="size"/> is its stored size.</summary>
    public static bool IsWideWindowed(GameMemory game, out (uint Width, uint Height) size)
    {
        size = StoredSize(game);
        return game.ReadU32(game.Addr!.DisplayMode) == DisplayWindowed && WiderThanCap(size.Width, size.Height);
    }

    /// <summary>Queue the clamp bypass on; returns the flags as they were, for <see cref="QueueBypassOff"/>.</summary>
    public static Bypass QueueBypassOn(GameMemory game, ScatterWriteHandle writes)
    {
        var a = game.Addr!;
        var original = new Bypass(game.Read(a.ListenValue, 16), game.Read(a.RenderValue, 1));
        game.Queue(writes, a.ListenValue + 8, "x\0"u8.ToArray());
        game.Queue(writes, a.ListenValue, BitConverter.GetBytes(0x05u));
        game.Queue(writes, a.RenderValue, [0x01]);
        return original;
    }

    /// <summary>Queue the client-API flags back to what they were.</summary>
    public static void QueueBypassOff(GameMemory game, ScatterWriteHandle writes, Bypass original)
    {
        var a = game.Addr!;
        game.Queue(writes, a.ListenValue, original.Listen[..4]);
        game.Queue(writes, a.ListenValue + 8, original.Listen[8..]);
        game.Queue(writes, a.RenderValue, original.Render);
    }
}
