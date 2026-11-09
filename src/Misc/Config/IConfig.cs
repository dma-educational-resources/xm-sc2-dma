
namespace SC2Radar.Common.Misc.Config
{
    public interface IConfig
    {
        bool MemWritesEnabled { get; }

        void Save();
        Task SaveAsync();
    }
}
