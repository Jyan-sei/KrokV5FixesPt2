using System.Collections.Generic;
using CasualtiesTogetherUtils;
using Together;
using UnityEngine;

namespace V5SyncSwap;

/// <summary>
/// host safety net for objects nobody has touched.
/// one quadrant of one living player per frame, players in order.
/// after the last player, wait SafetyNetSeconds before the next loop.
/// stock still uses an 80 unit circle, a 10 unit always-register band, and 16 farther objects.
/// </summary>
internal static class SafetyNet
{
	private const float Radius = 80f;
	private const float Close = 10f;
	private const int FarBudget = 16;

	private static readonly Vector2[] Quadrant =
	{
		new Vector2(1f, 1f),
		new Vector2(-1f, 1f),
		new Vector2(-1f, -1f),
		new Vector2(1f, -1f),
	};

	private static readonly Collider2D[] Hits = new Collider2D[256];
	private static readonly List<NetBody> Bodies = new List<NetBody>(16);

	private static bool _armed;
	private static bool _waiting;
	private static bool _logged;
	private static bool _bufferFullLogged;
	private static float _wait;
	private static int _player;
	private static int _quadrant;
	private static int _farLeft = FarBudget;

	// Tick does nothing until the plugin has finished installing.
	internal static void Arm()
	{
		_armed = true;
	}

	// host only. one quadrant, then the next, then the next living player, then the wait.
	internal static void Tick()
	{
		if (!_armed || Plugin.Enabled == null || !Plugin.Enabled.Value || !Net.IsServer)
			return;

		if (_waiting)
		{
			_wait -= Time.deltaTime;
			if (_wait > 0f)
				return;
			_waiting = false;
			_player = 0;
			_quadrant = 0;
			_farLeft = FarBudget;
		}

		IList<ScavPlayer> living;
		try
		{
			living = ScavPlayer.AllLivingPlayers;
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("[V5SyncSwap] living players: " + ex.Message);
			BeginWait();
			return;
		}

		if (living == null || living.Count == 0)
		{
			BeginWait();
			return;
		}

		if (_player >= living.Count)
			_player = 0;

		ScavPlayer plr = living[_player];
		if (plr != null)
			StampQuadrant(plr, _quadrant);

		if (!_logged)
		{
			_logged = true;
			float delay = Delay();
			Plugin.Log.LogInfo("[V5SyncSwap] safety net is one quadrant per frame, then wait " + delay.ToString("0.0") + "s");
		}

		_quadrant++;
		if (_quadrant < Quadrant.Length)
			return;

		_quadrant = 0;
		_farLeft = FarBudget;
		_player++;
		if (_player >= living.Count)
			BeginWait();
	}

	// rest after the last living player. the next loop starts at the first player again.
	private static void BeginWait()
	{
		_waiting = true;
		_wait = Delay();
		_player = 0;
		_quadrant = 0;
		_farLeft = FarBudget;
	}

	// config seconds. missing waits 1.9s, the stock slow pass. a negative value waits 0.
	private static float Delay()
	{
		if (Plugin.SafetyNetSeconds == null)
			return 1.9f;
		return Mathf.Max(0f, Plugin.SafetyNetSeconds.Value);
	}

	// box over this quarter of the 80 circle. the first quadrant also walks bags, backgrounds, and other bodies.
	private static void StampQuadrant(ScavPlayer plr, int quadrant)
	{
		Vector2 pos = plr.pos;
		Vector2 center = pos + Quadrant[quadrant] * (Radius * 0.5f);
		int count;
		try
		{
			count = Physics2D.OverlapBoxNonAlloc(center, new Vector2(Radius, Radius), 0f, Hits);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("[V5SyncSwap] quadrant: " + ex.Message);
			return;
		}

		if (count >= Hits.Length && !_bufferFullLogged)
		{
			_bufferFullLogged = true;
			Plugin.Log.LogWarning("[V5SyncSwap] quadrant hit buffer filled at " + Hits.Length);
		}

		float radiusSqr = Radius * Radius;
		for (int i = 0; i < count; i++)
		{
			Collider2D hit = Hits[i];
			Hits[i] = null;
			if (hit == null)
				continue;
			if (hit.GetComponent<Item>() == null && hit.GetComponent<BuildingEntity>() == null)
				continue;
			GameObject go = hit.gameObject;
			if (go == null)
				continue;
			if (((Vector2)go.transform.position - pos).sqrMagnitude > radiusSqr)
				continue;

			Note(go, pos);
			if (go.TryGetComponent<Container>(out Container container))
			{
				foreach (Item item in container.GetAllItems())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}
		}

		// the other three quadrants are only the box query.
		if (quadrant != 0)
			return;

		try
		{
			foreach (GameObject go in NetObjectRegistry.GetIdentifiedBackgroundsInRadius(pos, Radius))
				Note(go, pos);

			if (plr.body != null)
			{
				foreach (Item item in plr.body.GetAllItemsThorough())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}

			Bodies.Clear();
			NetBody.GetBodiesInRadius(pos, Radius, Bodies);
			if (plr.body != null && plr.body.TryGetNetBody(out NetBody self))
				Bodies.Remove(self);
			for (int i = 0; i < Bodies.Count; i++)
			{
				NetBody other = Bodies[i];
				if (other == null || other.body == null)
					continue;
				foreach (Item item in other.body.GetAllItemsThorough())
				{
					if (item != null)
						Note(item.gameObject, pos);
				}
			}
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("[V5SyncSwap] quadrant extras: " + ex.Message);
		}
	}

	// register an unregistered object the game should sync. within 10 units always. farther objects share a budget of 16.
	private static void Note(GameObject go, Vector2 pos)
	{
		if (go == null || NetObjectRegistry.IsRegistered(go) || !NetObjectRegistry.ObjectShouldBeSynced(go))
			return;

		bool close = ((Vector2)go.transform.position - pos).sqrMagnitude <= Close * Close;
		if (!close)
		{
			if (_farLeft <= 0)
				return;
			_farLeft--;
		}

		NetObjectRegistry.NewGO(go);
	}
}
