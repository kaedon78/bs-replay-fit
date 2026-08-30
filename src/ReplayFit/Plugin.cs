using IPA;
using UnityEngine;
using IPALogger = IPA.Logging.Logger;

namespace ReplayFit
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
            _host = new GameObject("ReplayFit");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<OffsetProbe>();
            _host.AddComponent<CutRecorder>();
            _host.AddComponent<SwingHarness>();
            _host.AddComponent<SettingsWatcher>();
            _host.AddComponent<SettingsMenu.Installer>();
            SearchSelfTest.Run();
            Log.Info("OnStart: probe and recorder attached");
        }

        [OnExit]
        public void OnExit()
        {
            if (_host != null)
            {
                // Flush first: Destroy would take the pending cuts with it.
                _host.GetComponent<CutRecorder>()?.Flush();
                Object.Destroy(_host);
            }
            SettingsMenu.Unregister();
            Log.Info("OnExit");
        }
    }
}
