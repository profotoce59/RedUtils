using System;
using System.Collections.Generic;
using System.Diagnostics;
using RedUtils.Interop;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>
	/// Un plan de frappe : une <b>perturbation</b> de ce que le bot allait faire de toute façon.
	///
	/// <para>Le plan ne décrit plus toute la séquence d'inputs — c'est <see cref="ShotReference"/>
	/// qui la fournit. Il ne dit que ceci : sur un bloc de <see cref="ShotSearch.BlockTicks"/> ticks
	/// consécutifs, tenir cette assiette et ce boost à la place de ce que la référence aurait
	/// produit. Tout le reste du vol suit la référence.</para>
	/// </summary>
	public readonly struct StrikePlan
	{
		/// <summary>Premier tick du bloc perturbé. <b>−1 = référence pure</b>, aucune perturbation.</summary>
		public readonly int BlockStart;
		/// <summary>Braquage/lacet tenu pendant le bloc : −1, 0 ou +1.</summary>
		public readonly float Steer;
		/// <summary>Tonneau tenu pendant le bloc : −1, 0 ou +1.</summary>
		public readonly float Roll;
		/// <summary>Boost tenu pendant le bloc.</summary>
		public readonly bool Boost;

		/// <summary>Vrai si ce plan est la trajectoire de référence, laissée intacte.</summary>
		public bool IsReference => BlockStart < 0;

		public StrikePlan(int blockStart, float steer, float roll, bool boost)
		{
			BlockStart = blockStart; Steer = steer; Roll = roll; Boost = boost;
		}

		/// <summary>La référence : ce que le bot fait sans qu'on y touche.</summary>
		public static StrikePlan Reference => new StrikePlan(-1, 0f, 0f, false);

		public override string ToString() =>
			IsReference
				? "reference"
				: $"bloc@{BlockStart}+{ShotSearch.BlockTicks} steer={Steer:+0;-0;0} roll={Roll:+0;-0;0}"
				  + (Boost ? " boost" : "");
	}

	/// <summary>Résultat de l'évaluation d'un plan.</summary>
	public readonly struct StrikeOutcome
	{
		public readonly StrikePlan Plan;
		/// <summary>Vrai si la voiture a touché la balle pendant la simulation.</summary>
		public readonly bool Touched;
		/// <summary>Vitesse de la balle juste après le contact.</summary>
		public readonly Vec3 BallVelocity;
		/// <summary>Position de la balle juste après le contact.</summary>
		public readonly Vec3 BallLocation;
		/// <summary>Score : voir <see cref="ShotSearch.Score"/>. Plus grand = mieux.</summary>
		public readonly float Value;
		/// <summary>
		/// Distance minimale atteinte entre le centre de la voiture et celui de la balle.
		///
		/// <para>C'est le diagnostic d'un plan qui ne touche pas : il dit <b>de combien</b> il rate,
		/// ce qu'un simple « aucun contact » ne dit pas.</para>
		/// </summary>
		public readonly float MinCarBallDist;
		/// <summary>
		/// Note de la trajectoire de référence, pour la <b>même</b> recherche.
		///
		/// <para>C'est l'étalon : un plan ne vaut d'être appliqué que s'il fait mieux que ce que le
		/// bot allait faire tout seul. Sans ce point de comparaison, « note = 3400 » ne veut rien
		/// dire.</para>
		/// </summary>
		public readonly float ReferenceValue;
		/// <summary>Vrai si la référence n'a elle-même pas touché la balle — la comparaison est
		/// alors sans valeur, et c'est le signe que la référence ne décrit pas la réalité.</summary>
		public readonly bool ReferenceMissed;

		public StrikeOutcome(StrikePlan plan, bool touched, Vec3 ballVel, Vec3 ballLoc, float value,
			float minCarBallDist, float referenceValue = float.MinValue, bool referenceMissed = true)
		{
			Plan = plan; Touched = touched; BallVelocity = ballVel; BallLocation = ballLoc;
			Value = value; MinCarBallDist = minCarBallDist;
			ReferenceValue = referenceValue; ReferenceMissed = referenceMissed;
		}

		/// <summary>Vrai si ce plan fait mieux que la référence, et donc mérite d'être appliqué.</summary>
		public bool BeatsReference => Touched && !ReferenceMissed && Value > ReferenceValue;

		/// <summary>Recopie ce résultat en y attachant l'étalon de la recherche.</summary>
		public StrikeOutcome WithReference(float referenceValue, bool referenceMissed) =>
			new StrikeOutcome(Plan, Touched, BallVelocity, BallLocation, Value, MinCarBallDist,
				referenceValue, referenceMissed);
	}

	/// <summary>
	/// Répartition du coût d'une recherche, pour savoir quoi optimiser au lieu de le deviner.
	///
	/// <para>Deux natures de travail se partagent le temps : la <b>loi de commande</b>, du calcul
	/// flottant C# pur (trigonométrie, boucles PD), et le <b>moteur</b>, un aller-retour P/Invoke
	/// suivi d'un pas de physique Bullet. Les leviers d'optimisation n'ont rien à voir selon celui
	/// qui domine — d'un côté remplacer des <c>MathF.Pow</c>, de l'autre réduire le nombre de
	/// candidats ou de ticks simulés.</para>
	///
	/// <para>Seul le natif est chronométré : deux horodatages par tick au lieu de quatre. Le temps
	/// C# se déduit du total mesuré par <c>ShotSearchRunner</c>, et absorbe au passage le coût de
	/// l'instrumentation elle-même — il est donc légèrement surestimé, jamais l'inverse.</para>
	/// </summary>
	public sealed class SearchStats
	{
		/// <summary>Ticks de <see cref="Stopwatch"/> passés dans RocketSim (pas + lectures d'état).</summary>
		public long NativeTicks;
		/// <summary>Nombre de candidats simulés, référence comprise.</summary>
		public int Candidates;
		/// <summary>Nombre total de ticks de simulation déroulés, tous candidats confondus.</summary>
		public int SimulatedTicks;

		public double NativeMs => NativeTicks * 1000.0 / Stopwatch.Frequency;
	}

	/// <summary>
	/// Recherche par force brute d'une <b>amélioration locale</b> de la frappe en cours.
	///
	/// <para><b>Le principe.</b> On ne cherche plus une séquence d'inputs de zéro : on simule
	/// d'abord ce que le bot va réellement faire (<see cref="ShotReference"/>), on note le résultat,
	/// puis on rejoue la même trajectoire en imposant une autre assiette sur un bloc de
	/// <see cref="BlockTicks"/> ticks, balayé sur tout le vol. Un plan n'est retenu que s'il bat la
	/// référence.</para>
	///
	/// <para><b>Pourquoi par blocs et pas tick par tick.</b> Un tick modifié à 120 Hz change la
	/// trajectoire de façon indétectable : tous les candidats se ressemblent et la note ne les
	/// départage pas. Un bloc de 8 ticks produit un écart lisible, et divise d'autant le nombre de
	/// candidats.</para>
	///
	/// <para><b>Deux limites structurelles.</b></para>
	/// <list type="number">
	/// <item><b>Simulation coupée au contact.</b> Sans les meshes de collision (AUDIT §7.6), les
	/// coins arrondis et la géométrie des buts n'existent pas. On lit la vitesse de balle à la fin
	/// du contact et on teste soi-même le franchissement du plan de but.</item>
	/// <item><b>Phase aérienne uniquement.</b> La référence ne sait rejouer que la branche
	/// <c>_jumped</c> de <c>JumpShot</c> et <c>DoubleJumpShot</c>. Hors de là — au sol, ou sur un
	/// <c>AerialShot</c> — <see cref="Search"/> refuse plutôt que de simuler une trajectoire
	/// inventée.</item>
	/// </list>
	///
	/// <para><b>Coût.</b> À exécuter sur un thread de fond, jamais depuis <c>GetOutput</c> : le
	/// budget d'un tick est de 8,33 ms à 120 Hz, une recherche complète coûte davantage. Voir
	/// AUDIT §7.1 et la mesure <c>duree</c> dans les logs.</para>
	/// </summary>
	public static class ShotSearch
	{
		/// <summary>Braquages/lacets explorés.</summary>
		public static readonly float[] Steers = { -1f, 0f, 1f };
		/// <summary>Tonneaux explorés.</summary>
		public static readonly float[] Rolls = { -1f, 0f, 1f };
		/// <summary>Avec et sans boost.</summary>
		public static readonly bool[] Boosts = { false, true };

		/// <summary>
		/// Longueur du bloc perturbé, en ticks. C'est lui qui borne le coût : le nombre de candidats
		/// vaut <c>1 + 18 × horizon / BlockTicks</c>.
		/// </summary>
		public const int BlockTicks = 8;

		/// <summary>Pas de temps de la simulation — l'arène tourne à 120 Hz.</summary>
		public const float Dt = 1f / 120f;

		/// <summary>Nombre maximal de ticks simulés par candidat (1,5 s). Borne le coût.</summary>
		public const int MaxTicks = 180;

		/// <summary>
		/// Fenêtre, en ticks de simulation, avant le contact dans laquelle un bloc perturbé a le
		/// droit de commencer. <b>On ne perturbe que la FIN du vol.</b>
		///
		/// <para>Tout ce qui précède cette fenêtre est <b>commun</b> à tous les candidats (la
		/// référence, non perturbée) : on le simule une seule fois, on snapshote l'arène à l'entrée
		/// de la fenêtre, et chaque candidat repart de ce snapshot. Le coût passe de
		/// <c>N × horizon</c> à <c>horizon + N × fenêtre</c>.</para>
		///
		/// <para>Effet de bord voulu : un bloc tardif est loin dans le futur quand la réponse
		/// asynchrone revient, donc encore entièrement applicable — contrairement aux blocs
		/// précoces, souvent déjà écoulés à la lecture. 60 ticks ≈ 0,5 s.</para>
		/// </summary>
		public const int SweepWindowTicks = 60;

		/// <summary>
		/// Instantané de l'arène à l'entrée de la fenêtre de balayage : de quoi faire repartir un
		/// candidat sans rejouer le préfixe. <c>Car</c> et <c>Ball</c> portent l'état physique
		/// complet (RocketSim les repose intégralement, compteurs de saut/flip compris) ;
		/// <c>RefState</c> porte les compteurs de la loi de commande, côté C#.
		/// </summary>
		private struct SimSnapshot
		{
			public RocketSimNative.RSCarState Car;
			public RocketSimNative.RSBallState Ball;
			public ReferenceState RefState;
			public float MinDist;
		}

		/// <summary>Ticks simulés après la <b>fin</b> du contact — de quoi lire une vitesse stabilisée.</summary>
		private const int TicksAfterTouch = 2;

		/// <summary>
		/// Durée maximale d'un contact continu avant qu'on ne lise de force.
		///
		/// <para>Une voiture qui pousse la balle devant elle la « touche » indéfiniment : sans ce
		/// plafond, le candidat emmènerait la balle vers un coin — qui n'existe pas sans les meshes
		/// (AUDIT §7.6).</para>
		/// </summary>
		private const int MaxContactTicks = 24;

		/// <summary>Demi-largeur utile du but, marge de rayon de balle comprise.</summary>
		private const float GoalHalfWidth = Goal.Width / 2f - Ball.Radius;
		/// <summary>Hauteur utile du but, marge de rayon de balle comprise.</summary>
		private const float GoalTop = Goal.Height - Ball.Radius;

		/// <summary>
		/// Énumère les blocs perturbés à tester, tous situés <b>dans la fenêtre finale</b> à partir
		/// de <paramref name="branch"/>. La référence n'y figure pas : elle est simulée à part, car
		/// c'est elle qui fournit le préfixe partagé.
		/// </summary>
		/// <param name="branch">Premier tick où un bloc peut commencer. C'est aussi le point de
		/// branche : tout ce qui précède est commun et n'est simulé qu'une fois. Voir
		/// <see cref="SweepWindowTicks"/> et le calcul dans <see cref="Search"/>.</param>
		public static List<StrikePlan> CandidatesFrom(int branch, int horizon)
		{
			List<StrikePlan> plans = new List<StrikePlan>();

			for (int start = System.Math.Max(branch, 0); start + BlockTicks <= horizon; start += BlockTicks)
				foreach (bool boost in Boosts)
					foreach (float steer in Steers)
						foreach (float roll in Rolls)
							plans.Add(new StrikePlan(start, steer, roll, boost));

			return plans;
		}

		/// <summary>
		/// Simule tous les candidats et renvoie le meilleur, avec la note de la référence attachée.
		/// Null si la référence n'est pas exploitable ou l'arène indisponible.
		///
		/// <para>À exécuter sur un thread de fond — voir la note de coût en tête de classe. Les deux
		/// instantanés doivent avoir été pris côté jeu et passés par valeur : <c>Car</c> et
		/// <c>Ball</c> sont rafraîchis à chaque tick, les lire ici donnerait un état incohérent.</para>
		/// </summary>
		/// <param name="minBlockStart">Premier tick applicable — voir <see cref="Candidates"/>.</param>
		/// <param name="controlPeriod">
		/// Nombre de ticks de simulation pendant lesquels un même input est tenu, c'est-à-dire le
		/// rapport entre la cadence de l'arène (120 Hz) et celle à laquelle le jeu appelle le bot.
		///
		/// <para><b>Vaut 2 quand RLBot tourne à 60 Hz</b>, le cas courant. L'ignorer donnerait à la
		/// voiture simulée deux fois plus d'occasions de corriger son assiette qu'elle n'en a
		/// réellement — une voiture plus agile que la vraie, dont les plans ne sont pas jouables. Ça
		/// désynchronise aussi les compteurs de frames du double saut : « relâcher 3 frames » vaut
		/// 6 ticks à 60 Hz, pas 3.</para>
		/// </param>
		/// <param name="stats">Répartition du coût, remplie au fil de la recherche.</param>
		public static StrikeOutcome? Search(RocketSimArena arena, ShotReference reference,
			RocketSimNative.RSCarState seedCar, RocketSimNative.RSBallState seedBall,
			Vec3 aimPoint, float theirGoalY, int ticksToContact, int minBlockStart,
			int controlPeriod, SearchStats stats)
		{
			stats ??= new SearchStats();
			int period = Utils.Cap(controlPeriod, 1, 4);

			if (arena == null || !arena.Valid)
				return null;

			// Refus explicite : sans référence, on ne saurait que réinventer une trajectoire.
			if (reference == null || !reference.Valid)
				return null;

			int horizon = Utils.Cap(ticksToContact + TicksAfterTouch, 1, MaxTicks);

			// --- Point de branche : on ne perturbe que la fenêtre finale ---
			// Tout ce qui précède `branch` est identique pour tous les candidats (la référence, non
			// perturbée) : on le simule UNE fois et on snapshote l'arène à l'entrée de `branch`.
			// `branch` est aligné sur une frontière de contrôle (multiple de `period`) pour qu'AUCUN
			// input tenu entre deux frontières n'ait à être sauvegardé : au tick `branch`, la loi de
			// commande recalcule de zéro. Borné avant le contact pour que le snapshot précède la frappe.
			int window = System.Math.Min(SweepWindowTicks, horizon);
			int first = System.Math.Max(minBlockStart, horizon - window);
			first = System.Math.Min(first, System.Math.Max(ticksToContact - 1, 0));
			int branch = first / period * period;

			// La référence d'abord : c'est l'étalon de tout le reste, ET le porteur du préfixe. On la
			// simule en entier depuis le décollage, en capturant l'arène au passage à `branch`.
			SimSnapshot snap = default;
			bool captured = false;
			SimSnapshot discard = default;
			bool discardFlag = false;

			StrikeOutcome referenceOutcome = Run(arena, reference, StrikePlan.Reference,
				seedCar, seedBall, default, 0, horizon, ticksToContact, aimPoint, theirGoalY,
				period, float.MaxValue, branch, ref snap, ref captured, stats);
			stats.Candidates++;

			// Auto-vérification : rejouer la référence DEPUIS le snapshot doit reproduire le run
			// direct. Sinon, la restauration d'arène n'est pas fidèle et TOUS les scores de candidats
			// sont faux sans que rien ne le signale — le piège classique d'un état de départ inexact.
			if (Fixes.ShotSearchValidateBranch && captured)
			{
				StrikeOutcome check = Run(arena, reference, StrikePlan.Reference,
					snap.Car, snap.Ball, snap.RefState, branch, horizon, ticksToContact,
					aimPoint, theirGoalY, period, snap.MinDist, -1, ref discard, ref discardFlag, stats);
				stats.Candidates++;

				if (referenceOutcome.Touched != check.Touched)
					Console.WriteLine($"[ShotSearch] DERIVE BRANCHE : contact direct={referenceOutcome.Touched} " +
						$"vs branche={check.Touched} — la restauration d'arene n'est pas fidele");
				else if (referenceOutcome.Touched)
				{
					float locDrift = referenceOutcome.BallLocation.Dist(check.BallLocation);
					float velDrift = (referenceOutcome.BallVelocity - check.BallVelocity).Length();
					if (locDrift > 5f || velDrift > 20f)
						Console.WriteLine($"[ShotSearch] DERIVE BRANCHE loc={locDrift:F1}uu vel={velDrift:F0} " +
							$"branch={branch} horizon={horizon} — le prefixe partage ne reproduit pas le run direct");
				}
			}

			StrikeOutcome? best = null;      // meilleur plan qui TOUCHE
			StrikeOutcome? closest = null;   // à défaut, celui qui rate de le moins possible

			foreach (StrikePlan plan in CandidatesFrom(branch, horizon))
			{
				// Chemin normal : repartir du snapshot, ne simuler que le suffixe [branch, horizon).
				// Repli : si le préfixe n'a pas pu être capturé (vol si court que la référence touche
				// avant `branch`), on rejoue tout depuis le décollage, comme avant l'optimisation.
				StrikeOutcome outcome = captured
					? Run(arena, reference, plan, snap.Car, snap.Ball, snap.RefState,
						branch, horizon, ticksToContact, aimPoint, theirGoalY, period, snap.MinDist,
						-1, ref discard, ref discardFlag, stats)
					: Run(arena, reference, plan, seedCar, seedBall, default,
						0, horizon, ticksToContact, aimPoint, theirGoalY, period, float.MaxValue,
						-1, ref discard, ref discardFlag, stats);
				stats.Candidates++;

				if (outcome.Touched)
				{
					if (best == null || outcome.Value > best.Value.Value)
						best = outcome;
				}
				else if (closest == null || outcome.MinCarBallDist < closest.Value.MinCarBallDist)
				{
					closest = outcome;
				}
			}

			// Si la référence bat tous les candidats, c'est elle qu'on renvoie : le bon plan est
			// alors « ne rien changer », et c'est une réponse en soi.
			StrikeOutcome winner = best ?? closest ?? referenceOutcome;
			if (referenceOutcome.Touched && (!winner.Touched || referenceOutcome.Value >= winner.Value))
				winner = referenceOutcome;

			// Quand rien ne touche, on renvoie quand même le moins mauvais : « aucun contact » ne dit
			// pas si on rate de 80 uu ou de 3000, et c'est ce qu'il faut savoir pour trancher entre un
			// réglage à ajuster et une référence hors sujet.
			// TOUT consommateur doit tester Touched — et BeatsReference — avant d'appliquer quoi que ce soit.
			return winner.WithReference(referenceOutcome.Value, !referenceOutcome.Touched);
		}

		/// <summary>
		/// Déroule un plan sur <c>[startTick, horizon)</c> et lit la trajectoire de balle qui en sort.
		///
		/// <para>Deux usages selon <paramref name="startTick"/> :</para>
		/// <list type="bullet">
		/// <item><b>Référence, depuis le décollage</b> (<c>startTick = 0</c>, état de départ = seed) :
		/// simule tout le vol et, si <paramref name="captureTick"/> ≥ 0, <b>capture</b> l'arène et les
		/// compteurs C# à l'entrée de ce tick dans <paramref name="snap"/> — le préfixe partagé.</item>
		/// <item><b>Candidat, depuis le snapshot</b> (<c>startTick = branch</c>, état de départ =
		/// snapshot) : ne resimule que le suffixe. La restauration repose l'état physique COMPLET (la
		/// voiture est en l'air, sans contact de roue, donc pos/vel/orientation/compteurs suffisent à
		/// reproduire le run direct) et les compteurs de la loi de commande.</item>
		/// </list>
		///
		/// <para>La capture est prise <b>en tête d'itération</b>, avant tout pas : <paramref name="snap"/>
		/// décrit alors l'état ENTRANT dans <paramref name="captureTick"/>, exactement ce dont un
		/// candidat qui démarre à ce tick a besoin.</para>
		/// </summary>
		private static StrikeOutcome Run(RocketSimArena arena, ShotReference reference,
			StrikePlan plan, in RocketSimNative.RSCarState startCar,
			in RocketSimNative.RSBallState startBall, ReferenceState startRef,
			int startTick, int horizon, int ticksToContact, Vec3 aimPoint, float theirGoalY,
			int controlPeriod, float startMinDist, int captureTick,
			ref SimSnapshot snap, ref bool captured, SearchStats stats)
		{
			long tNative = Stopwatch.GetTimestamp();
			arena.Seed(in startCar, in startBall);

			// Un SEUL GetCar par tick : l'état lu après le pas du tick N est celui dont la loi de
			// commande a besoin au tick N+1. Le relire en tête de boucle doublait les allers-retours
			// P/Invoke pour rien.
			RocketSimNative.RSCarState state = arena.GetCar();
			stats.NativeTicks += Stopwatch.GetTimestamp() - tNative;

			// Compteurs de frames du contrôleur de référence : propres à CE candidat. Repartent du
			// snapshot pour un suffixe, de zéro pour un run complet.
			ReferenceState refState = startRef;

			ulong lastHitTick = state.BallHitTick;

			bool touched = false;
			int contactTicks = 0;   // ticks où le contact était encore actif
			int quietTicks = 0;     // ticks consécutifs SANS contact, une fois la balle touchée
			float minDist = startMinDist;

			// Un input n'est recalculé qu'à la cadence où le jeu appelle le bot ; entre deux, il est
			// TENU. C'est ce que fait le moteur du jeu, et c'est ce qui borne l'autorité réelle du
			// pilote. Recalculer à chaque tick simulerait une voiture plus réactive que la vraie.
			RocketSimNative.RSCarControls controls = default;
			float frameDt = controlPeriod * Dt;

			for (int tick = startTick; tick < horizon; tick++)
			{
				// Capture du préfixe : état ENTRANT dans `captureTick`, avant tout pas de ce tick.
				// `branch` étant un multiple de `controlPeriod`, aucun input tenu n'est à sauvegarder :
				// le tick `captureTick` recalculera la commande de zéro.
				if (captureTick >= 0 && tick == captureTick && !captured)
				{
					tNative = Stopwatch.GetTimestamp();
					snap.Ball = arena.GetBall();
					stats.NativeTicks += Stopwatch.GetTimestamp() - tNative;
					snap.Car = state;
					snap.RefState = refState;
					snap.MinDist = minDist;
					captured = true;
				}

				// --- C# : la loi de commande, à partir de l'état courant ---
				if (tick % controlPeriod == 0)
				{
					float timeRemaining = (ticksToContact - tick) * Dt;
					controls = Controls(reference, plan, in state, tick, timeRemaining, frameDt, ref refState);
				}

				// --- Natif : le pas de physique et les lectures d'état ---
				tNative = Stopwatch.GetTimestamp();
				arena.Step(controls);
				state = arena.GetCar();
				RocketSimNative.RSBallState ballNow = touched ? default : arena.GetBall();
				stats.NativeTicks += Stopwatch.GetTimestamp() - tNative;

				stats.SimulatedTicks++;

				bool hitNow = state.BallHitValid != 0 && state.BallHitTick != lastHitTick;

				// Tant qu'on n'a pas touché, on suit de combien on rate. C'est la seule mesure qui
				// distingue « trajectoire à peine fausse » de « voiture jamais dans le coup ».
				if (!touched)
				{
					float d = RocketSimArena.ToRU(state.Pos).Dist(RocketSimArena.ToRU(ballNow.Pos));
					if (d < minDist)
						minDist = d;
				}

				if (hitNow)
				{
					// Le contact dure souvent plusieurs ticks (un flip pousse la balle au lieu de la
					// percuter). Lire dès le premier donnerait une vitesse encore en construction,
					// donc une note fausse — on attend que la balle se soit détachée.
					touched = true;
					lastHitTick = state.BallHitTick;
					contactTicks++;
					quietTicks = 0;

					if (contactTicks >= MaxContactTicks)
						break;   // la voiture porte la balle : on lit là où on en est
				}
				else if (touched)
				{
					quietTicks++;
					if (quietTicks >= TicksAfterTouch)
						break;   // contact terminé et impulsion intégrée : c'est le moment de lire
				}
			}

			if (!touched)
				return new StrikeOutcome(plan, false, Vec3.Zero, Vec3.Zero, float.MinValue, minDist);

			tNative = Stopwatch.GetTimestamp();
			RocketSimNative.RSBallState ball = arena.GetBall();
			stats.NativeTicks += Stopwatch.GetTimestamp() - tNative;

			Vec3 loc = RocketSimArena.ToRU(ball.Pos);
			Vec3 vel = RocketSimArena.ToRU(ball.Vel);
			return new StrikeOutcome(plan, true, vel, loc, Score(loc, vel, aimPoint, theirGoalY), minDist);
		}

		/// <summary>
		/// Inputs d'un tick : ceux de la référence, éventuellement écrasés pendant le bloc perturbé.
		///
		/// <para>La <b>mécanique de saut reste toujours à la référence</b>, même dans le bloc. La
		/// contrarier désynchroniserait ses compteurs de frames — un saut coupé au mauvais moment
		/// n'est pas une variante de frappe, c'est une frappe annulée.</para>
		/// </summary>
		private static RocketSimNative.RSCarControls Controls(ShotReference reference, StrikePlan plan,
			in RocketSimNative.RSCarState car, int tick, float timeRemaining, float frameDt,
			ref ReferenceState st)
		{
			// frameDt et non Dt : les compteurs de la référence (JumpElapsed, Step) comptent des
			// FRAMES de jeu, pas des ticks de simulation. « Relâcher 3 frames » vaut 6 ticks à 60 Hz.
			RocketSimNative.RSCarControls c = reference.Controls(in car, timeRemaining, frameDt, ref st);

			if (plan.IsReference || tick < plan.BlockStart || tick >= plan.BlockStart + BlockTicks)
				return c;

			c.Steer = plan.Steer;
			c.Yaw = plan.Steer;
			c.Roll = plan.Roll;
			c.Boost = plan.Boost ? 1 : 0;

			return c;
		}

		/// <summary>
		/// Note une frappe. Positif = la balle franchit le plan de but entre les poteaux, et la note
		/// est alors sa vitesse le long de l'axe visé — un tir cadré et puissant gagne. Négatif =
		/// elle rate, et la note est l'opposé de l'écart au cadre, pour que « rater de peu » reste
		/// préférable à « rater de loin ».
		///
		/// <para>Le franchissement est calculé <b>analytiquement</b> (balistique pure, gravité
		/// seule), pas simulé : sans les meshes, la simulation ne connaît ni les coins ni le but.
		/// C'est la même vérification que <c>Ball.Prediction.FindGoal</c>, faite à la main.</para>
		/// </summary>
		public static float Score(Vec3 ballLoc, Vec3 ballVel, Vec3 aimPoint, float theirGoalY)
		{
			Vec3 toAim = (aimPoint - ballLoc).Normalize();
			float speedAlong = ballVel.Dot(toAim);

			// La balle part-elle vers leur but ?
			float dy = theirGoalY - ballLoc.y;
			if (MathF.Abs(ballVel.y) < 1f || MathF.Sign(dy) != MathF.Sign(ballVel.y))
				return -1e6f + speedAlong;

			float t = dy / ballVel.y;
			float x = ballLoc.x + ballVel.x * t;
			float z = ballLoc.z + ballVel.z * t - 0.5f * 650f * t * t;

			float missX = MathF.Max(MathF.Abs(x) - GoalHalfWidth, 0f);
			float missZ = MathF.Max(z - GoalTop, 0f) + MathF.Max(-z, 0f);
			float miss = missX + missZ;

			return miss > 0f ? -miss : speedAlong;
		}
	}
}
