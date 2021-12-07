using Dalamud.Configuration;

namespace OOBlugin
{
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; }

        public bool EnhancedAutoFaceTarget = false;

        public void Initialize() { }

        public void Save() => DalamudApi.PluginInterface.SavePluginConfig(this);
    }
}
