using SC2Radar.DMA.Features;
using Ctl = SC2Radar.UI.ImGui.UI;

namespace SC2Radar.SC2.Features.MemoryWrites
{
    /// <summary>
    /// Camera zoom: extends how far the camera zooms out, optionally with its own tilt and field of view. Only the
    /// farthest zoom step is changed - whenever the game's distance (c_cameraValueDistance) sits at its normal maximum
    /// (34 by default; mission start, camera reset, scrolling all the way out) the values are eased to the chosen ones.
    /// Scrolling in sets a nearer distance, which is left alone, so the game's own zoom steps (and their camera tilt)
    /// keep working. Client-side only (see SC2Memory.Camera.cs).
    /// </summary>
    public sealed class CameraZoom : MemWriteFeature<CameraZoom>
    {
        public override string Name         => "Camera Zoom";
        public override string Description  => "Lets the camera zoom out further than normal, with an optional tilt and field of view. Scroll all the way out to reach it; zooming in works as usual.";
        public override bool   Risky        => true;
        public override string RiskyWarning => "May cause drop from game with other players.";

        // c_cameraValue*: 0 field of view, 4 distance, 5 pitch
        const int Fov = 0, Pitch = 5, Distance = SC2Memory.CameraValueDistance;
        const float DefaultDistance = 34f, DefaultPitch = 56f, DefaultFov = 27.8f;
        const float Ease = 0.25f;   // per tick (~20 Hz): about half a second to settle

        static CameraZoomSettings Settings => Program.Config.MemWrites.CameraZoom;

        float _gameMax = DefaultDistance;                // the farthest distance the game itself has set
        readonly Dictionary<int, float> _written = [];   // camera value -> what we last wrote
        readonly Dictionary<int, float> _game = [];      // camera value -> the game's own value at the farthest step

        CameraZoom() { }

        protected override void Apply(SC2Memory mem, ScatterWriteHandle writes)
        {
            if (!mem.HasCamera) { Status = "Unavailable in this build"; return; }
            if (mem.ReadCameraValue(Distance) is not { } now) { Status = "Waiting for a game camera"; return; }

            // a tilt / field of view no longer wanted: back to the game's own
            if (!Settings.SetPitch) RestoreValue(mem, Pitch, writes);
            if (!Settings.SetFov)   RestoreValue(mem, Fov, writes);

            if (!Ours(Distance, now.Target))
            {
                if (now.Target > _gameMax) _gameMax = now.Target;          // a map with a farther camera
                if (now.Target < _gameMax - 0.01f) { Status = $"Zoomed in ({now.Current:0.#})"; return; }
            }

            Status = $"Distance {now.Current:0.#}";
            Drive(mem, writes, Distance, Settings.Distance, now);
            if (Settings.SetPitch) Drive(mem, writes, Pitch, Settings.Pitch);
            if (Settings.SetFov)   Drive(mem, writes, Fov, Settings.Fov);
        }

        /// <summary>Whether a value still holds what we wrote; otherwise the game set it and we forget ours.</summary>
        bool Ours(int index, float target)
        {
            if (_written.TryGetValue(index, out float w) && MathF.Abs(target - w) < 0.01f) return true;
            _written.Remove(index);
            return false;
        }

        /// <summary>Ease one camera value towards <paramref name="want"/>, remembering the game's own value first.</summary>
        void Drive(SC2Memory mem, ScatterWriteHandle writes, int index, float want, (float Target, float Current)? read = null)
        {
            if ((read ?? mem.ReadCameraValue(index)) is not { } now) return;
            if (!Ours(index, now.Target) && index != Distance) _game[index] = now.Target;
            if (MathF.Abs(now.Current - want) < 0.05f && MathF.Abs(now.Target - want) < 0.05f) return;
            float next = MathF.Abs(now.Current - want) < 0.2f ? want : now.Current + (want - now.Current) * Ease;
            if (mem.WriteCameraValue(index, next, writes)) _written[index] = next;
        }

        /// <summary>Put a value we changed back to the game's own.</summary>
        void RestoreValue(SC2Memory mem, int index, ScatterWriteHandle writes)
        {
            if (_written.Remove(index) && mem.HasCamera)
                mem.WriteCameraValue(index, index == Distance ? _gameMax : _game.GetValueOrDefault(index, index == Pitch ? DefaultPitch : DefaultFov), writes);
        }

        protected override void Revert(SC2Memory mem, ScatterWriteHandle writes)
        {
            foreach (int i in _written.Keys.ToList()) RestoreValue(mem, i, writes);
            Forget();
        }

        protected override void Forget()
        {
            _written.Clear();
            _game.Clear();
            _gameMax = DefaultDistance;
        }

        public override Action? DrawSettings => () =>
        {
            Ctl.BeginSliderGroup("Max distance", "Tilt", "Field of view");
            float d = Settings.Distance;
            if (Ctl.SliderFloat("Max distance##zoom", ref d, 34f, 90f, "%.0f", smallChange: 1f,
                    hint: "How far the camera zooms out at its farthest step. The game's normal limit is 34."))
            { Settings.Distance = d; Program.Config.SaveDebounced(); }
            float p = Settings.Pitch;
            if (Ctl.SliderFloat("Tilt##zoom_pitch", ref p, 20f, 90f, "%.0f", suffix: "°", smallChange: 1f,
                    hint: "Camera pitch at the farthest step: 90 looks straight down, the game uses 56."))
            { Settings.Pitch = p; Program.Config.SaveDebounced(); }
            float f = Settings.Fov;
            if (Ctl.SliderFloat("Field of view##zoom_fov", ref f, 15f, 60f, "%.1f", suffix: "°", smallChange: 0.5f,
                    hint: "Camera field of view at the farthest step. The game uses 27.8."))
            { Settings.Fov = f; Program.Config.SaveDebounced(); }
            Ctl.EndSliderGroup();

            bool sp = Settings.SetPitch;
            if (Ctl.Toggle("Change tilt##zoom_setpitch", ref sp, hint: "Use the Tilt slider; off leaves the game's angle."))
            { Settings.SetPitch = sp; Program.Config.SaveDebounced(); }
            bool sf = Settings.SetFov;
            if (Ctl.Toggle("Change field of view##zoom_setfov", ref sf, hint: "Use the Field of view slider; off leaves the game's."))
            { Settings.SetFov = sf; Program.Config.SaveDebounced(); }
            Ctl.KVRow("Camera", Enabled ? Status : CameraStatus());
        };

        DateTime _nextStatus;
        string _offStatus = "-";

        /// <summary>The camera distance while the feature is off (while on, Apply keeps <see cref="Status"/> fresh).</summary>
        string CameraStatus()
        {
            if (DateTime.UtcNow < _nextStatus) return _offStatus;
            _nextStatus = DateTime.UtcNow.AddMilliseconds(500);
            var mem = Memory.Instance;
            return _offStatus = mem is not { HasCamera: true } ? "-"
                              : mem.ReadCameraValue(Distance) is { } v ? $"Distance {v.Current:0.#}" : "No game camera";
        }
    }
}
