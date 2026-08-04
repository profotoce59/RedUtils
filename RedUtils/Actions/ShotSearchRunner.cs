using System;
using System.Threading;
using System.Threading.Tasks;
using RedUtils.Interop;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>
	/// Fait tourner <see cref="ShotSearch"/> sur un thread de fond et livre le résultat quand il
	/// est prêt.
	///
	/// <para><b>Pourquoi ce détour.</b> Une recherche complète coûte une dizaine de millisecondes,
	/// contre 8,33 ms pour un tick entier à 120 Hz : l'appeler depuis <c>GetOutput</c> ferait
	/// perdre deux ou trois ticks au bot, qui rendrait des inputs en retard. Lancée à ~0,5 s du
	/// contact, elle dispose en revanche d'une soixantaine de ticks pour en consommer deux. Voir
	/// AUDIT §7.1.</para>
	///
	/// <para><b>Règle de propriété.</b> L'instantané (voiture + balle) est pris sur le thread de
	/// jeu, dans <see cref="Request"/>, et passé par valeur. Le thread de fond ne touche jamais
	/// <c>Car</c>, <c>Ball</c> ni quoi que ce soit du paquet : il ne voit que des structures
	/// copiées et son arène, qui n'appartient qu'à lui.</para>
	/// </summary>
	public sealed class ShotSearchRunner : IDisposable
	{
		private readonly RocketSimArena _arena;
		private Task<StrikeOutcome?> _task;
		private int _busy;   // 0 = libre, 1 = recherche en cours (Interlocked)

		/// <summary>Vrai si RocketSim est disponible et l'arène utilisable.</summary>
		public bool Available => _arena != null && _arena.Valid;

		/// <summary>Vrai tant qu'une recherche tourne.</summary>
		public bool Busy => Volatile.Read(ref _busy) != 0;

		/// <summary>Durée de la dernière recherche, en millisecondes. Sert à surveiller le coût réel.</summary>
		public double LastDurationMs { get; private set; }

		public ShotSearchRunner(int team)
		{
			_arena = new RocketSimArena(team);
		}

		/// <summary>
		/// Lance une recherche si aucune ne tourne. À appeler depuis le thread de jeu.
		/// </summary>
		/// <returns>Faux si la recherche n'a pas pu démarrer (déjà en cours, RocketSim absent, ou
		/// état de voiture non reconstructible — voir <see cref="RocketSimArena.CanSeedExactly"/>).</returns>
		public bool Request(Car car, Vec3 aimPoint, float theirGoalY, int ticksToContact)
		{
			if (!Available)
				return false;

			// Une simulation partant d'un état faux vaut moins que pas de simulation du tout.
			if (!RocketSimArena.CanSeedExactly(car))
				return false;

			if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
				return false;

			// Instantané PRIS ICI, sur le thread de jeu. Les deux structures sont copiées par
			// valeur dans la closure : le thread de fond ne lira jamais l'état vivant.
			RocketSimNative.RSCarState seedCar = RocketSimArena.FromCar(car);
			RocketSimNative.RSBallState seedBall = RocketSimArena.BallNow();
			Vec3 aim = aimPoint;
			float goalY = theirGoalY;
			int ticks = ticksToContact;

			_task = Task.Run(() =>
			{
				System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
				try
				{
					return ShotSearch.Search(_arena, seedCar, seedBall, aim, goalY, ticks);
				}
				catch (Exception e)
				{
					Console.WriteLine($"[ShotSearch] échec : {e.Message}");
					return null;
				}
				finally
				{
					sw.Stop();
					LastDurationMs = sw.Elapsed.TotalMilliseconds;
					Volatile.Write(ref _busy, 0);
				}
			});

			return true;
		}

		/// <summary>
		/// Récupère le résultat s'il est prêt, et libère le créneau. Ne bloque jamais.
		/// </summary>
		public bool TryGetResult(out StrikeOutcome outcome)
		{
			outcome = default;

			if (_task == null || !_task.IsCompleted)
				return false;

			StrikeOutcome? result = _task.IsCompletedSuccessfully ? _task.Result : null;
			_task = null;

			if (result == null)
				return false;

			outcome = result.Value;
			return true;
		}

		/// <summary>Abandonne la recherche en cours : son résultat ne sera pas lu.</summary>
		public void Discard() => _task = null;

		public void Dispose()
		{
			// On attend la fin avant de libérer l'arène : le thread de fond l'utilise encore.
			try { _task?.Wait(500); } catch { /* le résultat ne nous intéresse plus */ }
			_arena?.Dispose();
		}
	}
}
