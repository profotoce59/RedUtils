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

		/// <summary>
		/// Vrai quand la dernière recherche terminée n'a retenu <b>aucun</b> plan — pas un seul
		/// candidat ne touche la balle.
		///
		/// <para><see cref="TryGetResult"/> renvoie faux dans ce cas comme dans celui d'une
		/// recherche encore en cours. Sans ce drapeau, un log ne peut pas distinguer « pas encore
		/// prêt » de « rien trouvé » — or la seconde situation est exactement celle qui doit
		/// alerter, et elle est silencieuse.</para>
		/// </summary>
		public bool LastSearchFoundNothing { get; private set; }

		/// <summary>
		/// Répartition du coût de la dernière recherche : ce qui est parti dans RocketSim, ce qui
		/// reste pour la loi de commande C#. C'est elle qui dit lequel des deux vaut d'être optimisé.
		/// </summary>
		public SearchStats LastStats { get; private set; } = new SearchStats();

		public ShotSearchRunner(int team)
		{
			_arena = new RocketSimArena(team);
		}

		/// <summary>
		/// Lance une recherche si aucune ne tourne. À appeler depuis le thread de jeu.
		/// </summary>
		/// <returns>Faux si la recherche n'a pas pu démarrer (déjà en cours, RocketSim absent, ou
		/// référence inexploitable).</returns>
		/// <param name="reference">Ce que le tir en cours va faire, capturé côté jeu. Sans elle, la
		/// recherche n'aurait aucune trajectoire à perturber et réinventerait des inputs.</param>
		/// <param name="seedCar">État de départ. C'est à l'appelant d'en garantir l'exactitude : elle
		/// dépend du <b>moment</b> de l'instantané, pas de la voiture seule (voir
		/// <see cref="RocketSimArena.CanSeedAtTakeoff"/>).</param>
		/// <param name="minBlockStart">Premier tick où une perturbation est encore applicable, compte
		/// tenu du temps que la recherche va mettre à répondre. Voir <see cref="ShotSearch.CandidatesFrom"/>.</param>
		/// <param name="controlPeriod">Ticks de simulation par frame de jeu — 2 à 60 Hz. Voir
		/// <see cref="ShotSearch.Search"/>.</param>
		public bool Request(ShotReference reference, RocketSimNative.RSCarState seedCar,
			Vec3 aimPoint, float theirGoalY, int ticksToContact, int minBlockStart, int controlPeriod)
		{
			if (!Available)
				return false;

			if (reference == null || !reference.Valid)
				return false;

			if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
				return false;

			// Instantané PRIS ICI, sur le thread de jeu. Les structures sont copiées par valeur dans
			// la closure : le thread de fond ne lira jamais l'état vivant. La référence, elle, est
			// immuable une fois construite — la partager est sûr.
			RocketSimNative.RSBallState seedBall = RocketSimArena.BallNow();
			ShotReference reference1 = reference;
			Vec3 aim = aimPoint;
			float goalY = theirGoalY;
			int ticks = ticksToContact;
			int minBlock = minBlockStart;
			int period = controlPeriod;

			// Neuf à chaque recherche : les compteurs sont cumulatifs, les réutiliser mélangerait
			// deux mesures. Publié avant le démarrage pour qu'il n'y ait jamais de LastStats nul.
			SearchStats stats = new SearchStats();
			LastStats = stats;

			_task = Task.Run(() =>
			{
				System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
				try
				{
					return ShotSearch.Search(_arena, reference1, seedCar, seedBall, aim, goalY,
						ticks, minBlock, period, stats);
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
			{
				LastSearchFoundNothing = true;
				return false;
			}

			LastSearchFoundNothing = false;
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
