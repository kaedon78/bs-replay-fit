using IPA;
using UnityEngine;
using IPALogger = IPA.Logging.Logger;

namespace ControllerAutoAdjust
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }

        private GameObject _host;

        [Init]
        public Plugin(IPALogger logger)
        {
            Log = logger;
            Log.Info("Init");
        }

        [OnStart]
        public void OnStart()
        {
            // A DontDestroyOnLoad host, because the probe has to outlive the scene change
            // from menu to gameplay and back.
            _host = new GameObject("ControllerAutoAdjust");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<OffsetProbe>();
            Log.Info("OnStart: probe attached");
        }

        [OnExit]
        public void OnExit()
        {
            if (_host != null)
            {
                Object.Destroy(_host);
            }
            Log.Info("OnExit");
        }
    }
}
