using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace OOBlugin
{
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; }

        [JsonIgnore] private DalamudPluginInterface pluginInterface;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.pluginInterface.SavePluginConfig(this);
        }
    }
}
