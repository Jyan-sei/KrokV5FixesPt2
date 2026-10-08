using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace V5SyncSwap;

/// <summary>
/// the host slow pass still destroys far trackers in one call.
/// its OverlapCircleAll calls (every dead player, or every living player
/// 80 units down) are captured and run one player every 0.1s.
/// </summary>
internal static class SlowStamp
{
	private const float Gap = 0.1f;

	private struct Job
	{
		internal Vector2 Pos;
		internal Body Body;
	}

	private static readonly Queue<Job> Jobs = new Queue<Job>(16);
	private static MethodInfo _gather;
	private static bool _capture;
	private static float _wait;
	private static bool _logged;
	private static int _warns;

	// patch the gather. without it the slow pass still runs every circle in one call.
	internal static void Install(Harmony harmony)
	{
		if (harmony == null)
			return;

		_gather = AccessTools.Method(
			typeof(NetObjectRegistry),
			"_ImmidiatelyRegisterCloseObjects",
			new[] { typeof(Vector2), typeof(Body) });
		if (_gather == null)
		{
			Plugin.Log.LogWarning("[V5SyncSwap] slow stamp gather was not found. stock overlap stays.");
			return;
		}

		harmony.Patch(_gather, prefix: new HarmonyMethod(typeof(SlowStamp), nameof(Capture)));
		Plugin.Log.LogInfo("[V5SyncSwap] slow stamp one player every 0.1s");
	}

	// host slow pass started. overlap calls enqueue instead of running.
	internal static void BeginCapture()
	{
		_capture = true;
		if (_logged)
			return;
		_logged = true;
		Plugin.Log.LogInfo("[V5SyncSwap] slow overlap is paced");
	}

	// slow pass returned. queued players run from Tick, one every 0.1s.
	internal static void EndCapture()
	{
		_capture = false;
	}

	// one stored circle. not during the capture, or the prefix would enqueue the same call again.
	internal static void Tick()
	{
		if (_capture || _gather == null || Jobs.Count == 0 || !Net.IsServer)
			return;
		if (_wait > 0f)
		{
			_wait -= Time.unscaledDeltaTime;
			if (_wait > 0f)
				return;
		}

		Job job = Jobs.Dequeue();
		try
		{
			_gather.Invoke(null, new object[] { job.Pos, job.Body });
		}
		catch (Exception ex)
		{
			if (_warns < 3)
			{
				_warns++;
				string message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
				Plugin.Log.LogWarning("[V5SyncSwap] slow stamp: " + message);
			}
		}

		if (Jobs.Count > 0)
			_wait = Gap;
	}

	// false skips the overlap. the host slow pass enqueues one job per player.
	private static bool Capture(Vector2 pos, Body body)
	{
		if (!_capture)
			return true;

		Jobs.Enqueue(new Job { Pos = pos, Body = body });
		return false;
	}
}
