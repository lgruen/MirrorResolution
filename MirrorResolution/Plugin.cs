using HarmonyLib;
using IPA;
using IPA.Config.Stores;
using IPALogger = IPA.Logging.Logger;

namespace MirrorResolution
{
    [Plugin(RuntimeOptions.DynamicInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; } = null!;

        private readonly Harmony _harmony = new Harmony("MirrorResolution");

        [Init]
        public Plugin(IPALogger logger, IPA.Config.Config config)
        {
            Log = logger;
            PluginConfig.Instance = config.Generated<PluginConfig>();
        }

        [OnEnable]
        public void OnEnable()
        {
            _harmony.PatchAll(typeof(Plugin).Assembly);
        }

        [OnDisable]
        public void OnDisable()
        {
            _harmony.UnpatchSelf();
        }
    }
}
