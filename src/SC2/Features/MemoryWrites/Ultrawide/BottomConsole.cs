using System.Text;
using System.Text.RegularExpressions;

namespace SC2Radar.SC2.Features.MemoryWrites.Ultrawide;

/// <summary>
/// Fitting the bottom console to an ultrawide window. Port of ui.py.
///
/// The bottom console is three 3D models (minimap, unit info, command card) pinned to the left
/// edge, the center and the right edge and sized by the screen height, so past 16:9 they drift
/// apart and leave gaps. Fix() widens the middle model, putting each of its ends where it would sit
/// at 16:9, measured from its own screen edge. The game rebuilds the console at every mission load,
/// so while "Keep bottom console framed" is ticked the Ultrawide feature refits it once a second (the
/// Python tool's background helper).
/// </summary>
static class BottomConsole
{
    // Struct field offsets inside the game's UI and model classes. These only shift if Blizzard
    // changes a class layout, which is rare; a change is caught by the runtime checks (a wrong offset
    // makes a name lookup or a plausibility test fail, and nothing is written).
    const ulong FrameLayoutNode = 0x08;          // UI frame -> its layout node
    const ulong NodeName = 0x58;                 // layout node -> interned name entry
    const ulong NameChars = 0x20;                // name entry -> first character of the name
    const string ConsoleMiddleFrame = "InfopanelModel";   // the model frame that draws the middle console piece
    const ulong ModelFrameFlags = 0x214;         // model frame flags dword
    const byte TransformsDirty = 0x04;           // ...its bit that makes the engine rebuild the model transform
    const ulong ModelFrameBuckets = 0x1F0;       // model list: bucket count here, bucket array pointer at +0x10
    const ulong EntryInstance = 0x20, EntryPosition = 0x28, EntryScale = 0x34;   // model entry fields
    const ulong InstanceModel = 0x150, ModelBounds = 0x88;   // instance -> model data -> min(x,y,z), max(x,y,z)
    static readonly (double PosX, double ScaleX) ConsoleDefault = (0.0, 1.0);   // middle piece in the console skins
    static readonly Regex Ident = new(@"\A[A-Za-z][A-Za-z0-9_]{1,63}\0", RegexOptions.CultureInvariant);

    record Plan(ulong Frame, ulong Entry, (double PosX, double ScaleX) Current, (double PosX, double ScaleX) Wanted);

    /// <summary>A UI frame's name from its layout node, or null.</summary>
    static string? FrameName(GameMemory game, ulong frame)
    {
        try
        {
            ulong node = game.ReadAbsU64(frame + FrameLayoutNode);
            ulong entry = node != 0 ? game.ReadAbsU64(node + NodeName) : 0;
            if (entry == 0)
                return null;
            var m = Ident.Match(Encoding.Latin1.GetString(game.ReadAbs(entry + NameChars, 0x44)));
            return m.Success ? m.Value[..^1] : null;
        }
        catch (MemoryReadException)
        {
            return null;
        }
    }

    /// <summary>
    /// Position.x and scale.x that put the middle console piece's ends where they sit at 16:9.
    /// The console models are drawn in 4:3 units (one unit is 2/3 of the screen height) at positions
    /// in normalised screen units (-1 is the left edge, +1 the right edge). Keeping each end at its
    /// 16:9 distance from its own screen edge makes the side pieces overlap it exactly as at 16:9.
    /// </summary>
    static (double PosX, double ScaleX) Target(uint width, uint height, double minX, double maxX)
    {
        if (!WideMode.WiderThanCap(width, height))
            return ConsoleDefault;
        double w16 = 16.0 / 9 * height;
        double unit = 2.0 / 3 * height;
        double left = w16 / 2 + minX * unit;
        double right = width - w16 / 2 + maxX * unit;
        double scale = (right - left) / ((maxX - minX) * unit);
        return ((left - minX * scale * unit) / (width / 2.0) - 1, scale);
    }

    /// <summary>Why the last MakePlan found no console (shown in the Debug panel), or null when it found one.</summary>
    public static string? MissReason { get; private set; }

    /// <summary>The middle console piece and its current and wanted values, or null outside a mission.</summary>
    static Plan? MakePlan(GameMemory game, bool restore)
    {
        Plan? Miss(string why) { MissReason = why; return null; }
        var a = game.Addr;
        if (a is null || !a.Console)
            return Miss("console addresses unresolved");
        double minX, maxX;
        ulong frame, entry;
        (double, double) current;
        uint width, height;
        try
        {
            ulong ui = game.ReadU64(a.GameUiPointer);
            ulong panel = game.ReadAbsU64(ui + a.GameUiConsolePanel);
            frame = game.ReadAbsU64(panel + a.PanelCenterModel) & ~1UL;
            if (FrameName(game, frame) is var name && name != ConsoleMiddleFrame)
                return Miss($"middle frame is '{name ?? "?"}'");
            uint count = game.ReadAbsU32(frame + ModelFrameBuckets);
            ulong buckets = game.ReadAbsU64(frame + ModelFrameBuckets + 0x10);
            if (buckets == 0 || count is 0 or > 256)
                return Miss($"model buckets {count}");
            byte[] raw = game.ReadAbs(buckets, 8 * (int)count);
            var nodes = Enumerable.Range(0, (int)count).Select(i => BitConverter.ToUInt64(raw, 8 * i)).Where(n => n != 0).ToList();
            if (nodes.Count != 1)
                return Miss($"{nodes.Count} console models");
            entry = nodes[0] - 0x10;
            if (game.ReadAbsU64(entry) != game.Base + (ulong)a.ModelEntryVtable)
                return Miss("model entry vtable");
            ulong model = game.ReadAbsU64(game.ReadAbsU64(entry + EntryInstance) + InstanceModel);
            minX = game.ReadAbsF32(model + ModelBounds);
            maxX = game.ReadAbsF32(model + ModelBounds + 12);
            current = (game.ReadAbsF32(entry + EntryPosition), game.ReadAbsF32(entry + EntryScale));
            (width, height) = WideMode.StoredSize(game);
        }
        catch (MemoryReadException)
        {
            return Miss("console read failed");
        }
        if (!(-3 < minX && minX < 0 && 0 < maxX && maxX < 3))
            return Miss($"model bounds {minX:0.##}..{maxX:0.##}");
        MissReason = null;
        // window size unreadable: still a mission, but there is nothing to fit (wanted = current writes nothing)
        return new Plan(frame, entry, current, restore ? ConsoleDefault : height == 0 ? current : Target(width, height, minX, maxX));
    }

    /// <summary>
    /// True while a mission is loaded: the game UI's console panel exists and its middle model frame
    /// is the InfopanelModel. Menus, score screens and loading screens have no console.
    /// </summary>
    public static bool InMission(GameMemory game) => MakePlan(game, restore: false) is not null;

    static bool Same((double, double) a, (double, double) b) =>
        Math.Abs(a.Item1 - b.Item1) < 1e-4 && Math.Abs(a.Item2 - b.Item2) < 1e-4;

    /// <summary>Queue the writes that fit the middle console piece to the window, or put it back. Returns the values
    /// queued, or null when there is nothing to change.</summary>
    public static (double PosX, double ScaleX)? Fix(GameMemory game, ScatterWriteHandle writes, bool restore = false)
    {
        var plan = MakePlan(game, restore);
        if (plan is null || Same(plan.Current, plan.Wanted))
            return null;
        var (posX, scaleX) = plan.Wanted;
        byte flags;
        try { flags = game.ReadAbs(plan.Frame + ModelFrameFlags, 1)[0]; }
        catch (MemoryReadException) { return null; }
        game.QueueAbs(writes, plan.Entry + EntryScale, BitConverter.GetBytes((float)scaleX));
        game.QueueAbs(writes, plan.Entry + EntryPosition, BitConverter.GetBytes((float)posX));
        // the engine rebuilds the model transform once it sees this bit (and clears it)
        game.QueueAbs(writes, plan.Frame + ModelFrameFlags, [(byte)(flags | TransformsDirty)]);
        return plan.Wanted;
    }

    public static string State(GameMemory game)
    {
        var plan = MakePlan(game, restore: false);
        if (plan is null)
            return "not in a mission";
        if (Same(plan.Current, plan.Wanted))
            return Same(plan.Wanted, ConsoleDefault) ? "normal" : $"framed (middle piece widened {plan.Current.ScaleX:0.00}x)";
        if (Same(plan.Current, ConsoleDefault))
            return "gaps between the pieces (Apply, or tick Keep the console framed)";
        return "widened for a different window size (Apply or Revert)";
    }
}
