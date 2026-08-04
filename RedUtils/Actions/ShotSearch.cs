using System;
using System.Collections.Generic;
using RedUtils.Interop;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>Un plan de frappe : ce qu'on fait pendant les derniers dixièmes avant le contact.</summary>
	public readonly struct StrikePlan
	{
		/// <summary>Tick (à 120 Hz) auquel appuyer sur saut, compté depuis le début de la recherche.</summary>
		public readonly int FlipTick;
		/// <summary>Braquage tenu jusqu'au saut : −1 gauche, 0 tout droit, +1 droite.</summary>
		public readonly float Steer;
		/// <summary>Tonneau tenu une fois en l'air : −1, 0 ou +1.</summary>
		public readonly float Roll;
		/// <summary>Faux = on ne saute pas du tout (frappe au sol).</summary>
		public readonly bool Flip;

		public StrikePlan(bool flip, int flipTick, float steer, float roll)
		{
			Flip = flip; FlipTick = flipTick; Steer = steer; Roll = roll;
		}

		public override string ToString() =>
			Flip ? $"flip@{FlipTick} steer={Steer:+0;-0;0} roll={Roll:+0;-0;0}"
			     : $"sol steer={Steer:+0;-0;0}";
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

		public StrikeOutcome(StrikePlan plan, bool touched, Vec3 ballVel, Vec3 ballLoc, float value)
		{
			Plan = plan; Touched = touched; BallVelocity = ballVel; BallLocation = ballLoc; Value = value;
		}
	}

	/// <summary>
	/// Recherche par force brute du meilleur plan de frappe, juste avant le contact.
	///
	/// <para>On garde la logique d'approche existante — c'est elle qui amène la voiture au bon
	/// endroit au bon moment. La recherche ne porte que sur les derniers dixièmes de seconde :
	/// à quel tick déclencher la frappe, de quel côté braquer au dernier moment, et dans quel sens
	/// vriller une fois en l'air. Chaque candidat est simulé dans RocketSim et jugé sur la
	/// trajectoire de balle qui en sort.</para>
	///
	/// <para><b>Deux limites structurelles à garder en tête.</b></para>
	/// <list type="number">
	/// <item><b>Simulation coupée au contact.</b> Sans les meshes de collision (AUDIT §7.6), les
	/// coins arrondis et la géométrie des buts n'existent pas. On lit donc la vitesse de balle
	/// juste après la frappe et on teste soi-même le franchissement du plan de but — jamais on ne
	/// laisse la simulation porter la balle jusqu'au fond du terrain.</item>
	/// <item><b>Voiture au sol uniquement.</b> Les compteurs de saut et de flip ne figurent pas
	/// dans le paquet RLBot (AUDIT §7.2). Ils ne valent zéro — leur vraie valeur — que pour une
	/// voiture au sol avec son flip disponible. <see cref="Search"/> refuse tout autre cas plutôt
	/// que de simuler une voiture qui n'existe pas.</item>
	/// </list>
	///
	/// <para><b>Coût.</b> ~5 µs par tick simulé, donc ~300 µs pour un candidat de 0,5 s, soit une
	/// dizaine de millisecondes pour l'ensemble — davantage qu'un tick entier (8,33 ms à 120 Hz).
	/// <see cref="Search"/> ne doit donc <b>jamais</b> être appelée depuis <c>GetOutput</c> :
	/// elle est faite pour tourner sur un thread de fond, lancée assez tôt pour que son résultat
	/// arrive avant le contact. Voir AUDIT §7.1.</para>
	/// </summary>
	public static class ShotSearch
	{
		/// <summary>Ticks de déclenchement de la frappe explorés, relatifs au début de la recherche.</summary>
		public static readonly int[] FlipTicks = { 0, 2, 4, 6 };
		/// <summary>Braquages explorés au dernier moment.</summary>
		public static readonly float[] Steers = { -1f, 0f, 1f };
		/// <summary>Tonneaux explorés une fois en l'air.</summary>
		public static readonly float[] Rolls = { -1f, 0f, 1f };

		/// <summary>Nombre maximal de ticks simulés par candidat (0,75 s). Borne le coût.</summary>
		public const int MaxTicks = 90;
		/// <summary>Ticks simulés après le contact — juste de quoi lire une vitesse stabilisée.</summary>
		private const int TicksAfterTouch = 2;

		/// <summary>Demi-largeur utile du but, marge de rayon de balle comprise.</summary>
		private const float GoalHalfWidth = Goal.Width / 2f - Ball.Radius;
		/// <summary>Hauteur utile du but, marge de rayon de balle comprise.</summary>
		private const float GoalTop = Goal.Height - Ball.Radius;

		/// <summary>Énumère les plans candidats. Au sol on ne vrille pas ; sans flip on ne fait que braquer.</summary>
		public static List<StrikePlan> Candidates(bool allowFlip)
		{
			List<StrikePlan> plans = new List<StrikePlan>();

			// Frappe au sol : pas de saut, on ne joue que sur le braquage.
			foreach (float steer in Steers)
				plans.Add(new StrikePlan(false, 0, steer, 0f));

			if (!allowFlip)
				return plans;

			foreach (int tick in FlipTicks)
				foreach (float steer in Steers)
					foreach (float roll in Rolls)
						plans.Add(new StrikePlan(true, tick, steer, roll));

			return plans;
		}

		/// <summary>
		/// Simule tous les candidats et renvoie le meilleur, ou null si aucun ne touche la balle.
		///
		/// <para>À exécuter sur un thread de fond — voir la note de coût en tête de classe.</para>
		/// </summary>
		/// <param name="arena">Arène réutilisée entre les appels (allouer une fois, pas par tick).</param>
		/// <param name="car">La voiture, telle qu'elle est maintenant.</param>
		/// <param name="aimPoint">Le point qu'on veut atteindre — en général le centre du but adverse.</param>
		/// <param name="theirGoalY">Ordonnée du plan de but adverse (<c>TheirGoal.Location.y</c>).</param>
		/// <param name="ticksToContact">Horizon de simulation, en ticks. Borné par <see cref="MaxTicks"/>.</param>
		public static StrikeOutcome? Search(RocketSimArena arena, Car car, Vec3 aimPoint,
			float theirGoalY, int ticksToContact)
		{
			// Refus explicite plutôt que simulation silencieusement fausse (AUDIT §7.2)
			if (!RocketSimArena.CanSeedExactly(car))
				return null;

			return Search(arena, RocketSimArena.FromCar(car), RocketSimArena.BallNow(),
				aimPoint, theirGoalY, ticksToContact);
		}

		/// <summary>
		/// Même recherche, à partir d'un instantané déjà pris.
		///
		/// <para>C'est la forme à utiliser depuis un thread de fond : <c>Car</c> et <c>Ball</c> sont
		/// rafraîchis par le thread de jeu à chaque tick, les lire ailleurs donnerait un état
		/// incohérent. L'instantané doit donc être pris côté jeu et passé par valeur — ce que
		/// permettent ces deux structures.</para>
		/// </summary>
		public static StrikeOutcome? Search(RocketSimArena arena,
			RocketSimNative.RSCarState seedCar, RocketSimNative.RSBallState seedBall,
			Vec3 aimPoint, float theirGoalY, int ticksToContact)
		{
			if (arena == null || !arena.Valid)
				return null;

			int horizon = Utils.Cap(ticksToContact + TicksAfterTouch, 1, MaxTicks);

			StrikeOutcome? best = null;

			foreach (StrikePlan plan in Candidates(allowFlip: true))
			{
				StrikeOutcome outcome = Simulate(arena, seedCar, seedBall, plan, horizon, aimPoint, theirGoalY);
				if (!outcome.Touched)
					continue;
				if (best == null || outcome.Value > best.Value.Value)
					best = outcome;
			}

			return best;
		}

		/// <summary>Déroule un plan et lit la trajectoire de balle qui en sort.</summary>
		private static StrikeOutcome Simulate(RocketSimArena arena,
			in RocketSimNative.RSCarState seedCar, in RocketSimNative.RSBallState seedBall,
			StrikePlan plan, int horizon, Vec3 aimPoint, float theirGoalY)
		{
			arena.Seed(in seedCar, in seedBall);

			ulong hitTickBefore = arena.GetCar().BallHitTick;

			for (int tick = 0; tick < horizon; tick++)
			{
				arena.Step(Controls(plan, tick));

				RocketSimNative.RSCarState state = arena.GetCar();
				if (state.BallHitValid != 0 && state.BallHitTick != hitTickBefore)
				{
					// Contact : quelques ticks de plus pour que l'impulsion soit intégrée, puis on
					// LIT et on s'arrête. Au-delà, la balle finirait par atteindre un coin — qui
					// n'existe pas sans les meshes (AUDIT §7.6).
					for (int after = 0; after < TicksAfterTouch; after++)
						arena.Step(Controls(plan, tick + 1 + after));

					RocketSimNative.RSBallState ball = arena.GetBall();
					Vec3 loc = RocketSimArena.ToRU(ball.Pos);
					Vec3 vel = RocketSimArena.ToRU(ball.Vel);
					return new StrikeOutcome(plan, true, vel, loc, Score(loc, vel, aimPoint, theirGoalY));
				}
			}

			return new StrikeOutcome(plan, false, Vec3.Zero, Vec3.Zero, float.MinValue);
		}

		/// <summary>Inputs du plan pour un tick donné.</summary>
		private static RocketSimNative.RSCarControls Controls(StrikePlan plan, int tick)
		{
			RocketSimNative.RSCarControls c = default;
			c.Throttle = 1f;

			bool airborne = plan.Flip && tick > plan.FlipTick;

			if (!airborne)
			{
				// Avant le saut : on tient le braquage choisi.
				c.Steer = plan.Steer;
				// Le saut est maintenu deux ticks — un seul tick donne un saut minimal, qui ne
				// laisse pas le temps de vriller.
				c.Jump = (plan.Flip && (tick == plan.FlipTick || tick == plan.FlipTick + 1)) ? 1 : 0;
			}
			else
			{
				// En l'air : le braquage devient du lacet, et on applique le tonneau cherché.
				c.Yaw = plan.Steer;
				c.Roll = plan.Roll;
			}

			return c;
		}

		/// <summary>
		/// Note une frappe. Positif = la balle franchit le plan de but entre les poteaux, et la
		/// note est alors sa vitesse le long de l'axe visé — un tir cadré et puissant gagne.
		/// Négatif = elle rate, et la note est l'opposé de l'écart au cadre, pour que « rater de
		/// peu » reste préférable à « rater de loin ».
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
