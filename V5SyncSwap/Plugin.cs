using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Together;
using UnityEngine;

namespace V5SyncSwap;

/// <summary>
/// together's 0.211s pass overlap-queries every living player in one frame.
/// on the host that pass is skipped. untouched objects are found one quadrant
/// per frame, one living player after another, then the sweep waits.
/// drop, pickup, wear, and container moves still register that object immediately.
/// a craft into a full hotbar is parented into a bag and that parent is published.
/// a bag insert that cannot name the bag is not sent as an unload.
/// the host rewrites a stored parent that does not match the live one, so a mouth
/// slot or a floor copy on other machines follows the host.
/// the 1.9s slow pass still destroys far trackers. its overlap runs one player
/// every 0.1s instead of every player in that call.
/// </summary>
[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
[BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
	internal static Plugin Instance;
	internal static BepInEx.Logging.ManualLogSource Log;
	internal static ConfigEntry<bool> Enabled;
	internal static ConfigEntry<float> SafetyNetSeconds;
	internal static ConfigEntry<bool> ChangeSyncEnabled;

	private Harmony _harmony;
	private static bool _logged;

	private void Awake()
	{
		Instance = this;
		Log = Logger;
		Enabled = Config.Bind("General", "Enabled", true,
			"On the host, skip the all-player fast overlap. Untouched objects are found one quadrant per frame.");
		SafetyNetSeconds = Config.Bind("General", "SafetyNetSeconds", 1.9f,
			"After every living player has had all four quadrants, wait this long before the next loop.");
		ChangeSyncEnabled = Config.Bind("General", "ChangeSync", true,
			"On the host, register an item or container when it is dropped, picked up, worn, or moved. Already-registered objects keep syncing through LiteEntitySystem.");

		_harmony = new Harmony(PluginInfo.GUID);
		ChangeSync.Install(_harmony);
		BagPlace.Install(_harmony);

		Type registry = AccessTools.TypeByName("Together.NetObjectRegistry");
		MethodInfo loop = AccessTools.Method(registry, "_ObjectSyncUpdateLoopUniversal", new[] { typeof(bool) });
		if (loop == null)
		{
			Log.LogWarning("[V5SyncSwap] registry gather was not found. stock stamp stays.");
			return;
		}

		_harmony.Patch(loop,
			prefix: new HarmonyMethod(typeof(Plugin), nameof(Prefix)),
			postfix: new HarmonyMethod(typeof(Plugin), nameof(Postfix)));
		SlowStamp.Install(_harmony);
		SafetyNet.Arm();
		Log.LogInfo("[V5SyncSwap] v" + PluginInfo.Version + " safety net is quadrants");
	}

	private void Update()
	{
		SafetyNet.Tick();
		SlowStamp.Tick();
		BagPlace.Tick();
	}

	private void OnDestroy()
	{
		_harmony?.UnpatchSelf();
	}

	// false skips stock. clients run stock. the host slow pass runs stock so far
	// trackers still drop, while its overlap calls are captured and paced.
	private static bool Prefix(bool do_slow_mode)
	{
		if (Enabled == null || !Enabled.Value || !Net.IsServer)
			return true;

		if (!do_slow_mode)
		{
			if (!_logged)
			{
				_logged = true;
				Log.LogInfo("[V5SyncSwap] fast stamp skipped");
			}
			return false;
		}

		SlowStamp.BeginCapture();
		return true;
	}

	// slow pass returned. stop capturing, or later gathers would be queued too.
	private static void Postfix(bool do_slow_mode)
	{
		if (do_slow_mode)
			SlowStamp.EndCapture();
	}
}
