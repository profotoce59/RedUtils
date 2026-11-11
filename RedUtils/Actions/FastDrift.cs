using System;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>
	/// Demi-tour rapide au frein à main vers une orientation cible (« fast-drift »). On part avec un
	/// peu de vitesse, on braque à fond du bon côté en tirant le frein à main : l'arrière décroche et
	/// le nez pivote bien plus vite qu'en virage normal. Terminé dès que le nez atteint la direction
	/// visée — pour enchaîner sur un déplacement propre au lieu de repartir en marche arrière.
	///
	/// <para><b>Non-interruptible</b> tant qu'elle tourne : un demi-tour à moitié fait laisse la
	/// voiture en travers. Pendant la manœuvre : <b>pas de throttle avant ni de boost</b> (on veut
	/// PIVOTER, pas avancer), et on reste au sol — la glisse au sol EST la mécanique.</para>
	///
	/// <para><b>Précondition : avoir de la vitesse.</b> Sans elle, le frein à main ne fait pas tourner.
	/// Si la vitesse tombe sous <see cref="MinDriftSpeed"/> avant l'alignement (ou en cas de décollage /
	/// dépassement du <see cref="Timeout"/>), on rend la main (Finished) pour que la stratégie reprenne
	/// en conduite normale.</para>
	/// </summary>
	public class FastDrift : IAction
	{
		public bool Finished { get; private set; }
		public bool Interruptible { get; private set; }

		/// <summary>Direction à plat que le nez doit atteindre.</summary>
		public Vec3 TargetDirection;

		/// <summary>
		/// Sens de braquage latché (0 = pas encore décidé).
		/// <para>Latché car l'écart de cap est un <c>Atan2</c> : sur une cible pile derrière, il bascule
		/// de +π à −π au bruit près, et le braquage changerait de signe d'une frame à l'autre.</para>
		/// </summary>
		private int _turnSign;
		private float _startTime = -1f;

		/// <summary>Écart de cap (rad) sous lequel la cible est atteinte.</summary>
		private const float AlignedAngle = 0.5f;
		/// <summary>Sous cette vitesse au sol, le frein à main ne fait plus pivoter : on abandonne.</summary>
		private const float MinDriftSpeed = 10;

		private const float MinDriftNeedThrottle = 250f;
		/// <summary>Garde-fou : au-delà, on considère la manœuvre coincée et on rend la main.</summary>
		private const float Timeout = 1.5f;

		/// <summary>
		/// Écart de cap (rad) au-delà duquel un virage normal ne suffit plus : on drifte.
		/// <para><b>1 rad ≈ 57°</b>, volontairement bas. Sur un point PROCHE, pivoter au frein à main
		/// est le moyen le PLUS RAPIDE d'y arriver : au rayon de virage courant on ne peut tout
		/// simplement pas le prendre. Ce qui empêche d'en abuser n'est pas cet angle mais
		/// <see cref="DriftMaxDistance"/>.</para>
		/// <para>(L'annotation « ~126° » qui accompagnait cette valeur dans Cover était fausse —
		/// 1 rad = 57° — mais la valeur, elle, est la bonne.)</para>
		/// </summary>
		private const float DriftTriggerAngle = 1f;

		/// <summary>
		/// Au-delà de cette distance au point visé, <b>jamais</b> de drift.
		/// <para>Loin, on a toute la place de tourner normalement en gardant la vitesse — alors qu'un
		/// drift la sacrifie par construction (throttle à 0, pas de boost, frein à main). Le drift
		/// n'est rentable que sur un point proche, qu'on ne peut pas atteindre autrement.</para>
		/// </summary>
		private const float DriftMaxDistance = 1000f;
		/// <summary>Sous cette vitesse au sol, le frein à main ne fait pas pivoter : pas de drift.</summary>
		private const float DriftMinEntrySpeed = 100f;
		/// <summary>Taux de rotation du nez pendant la glisse (rad/s). Modèle grossier : constant.
		/// C'EST la constante à caler au banc (state_setting_tests_defense.py).</summary>
		private const float DriftYawRate = 1.5f;

		/// <summary>Crée un fast-drift vers <paramref name="targetDirection"/> (aplatie et normalisée).</summary>
		public FastDrift(Vec3 targetDirection)
		{
			Finished = false;
			Interruptible = false;
			TargetDirection = targetDirection.FlatNorm();
		}

		public void Run(RUBot bot)
		{
			if (_startTime < 0f)
				_startTime = Game.Time;
			
			// AimAt règle Steer/Yaw vers un point dans la direction visée et renvoie l'écart de cap
			// SIGNÉ [1] (Atan2), comme dans Cover.Hold. On se sert du signe pour latcher le braquage.
			float yaw = bot.AimAt(bot.Me.Location + TargetDirection * 1000f)[1];

			bool aligned = MathF.Abs(yaw) <= AlignedAngle;
			bool tooSlow = bot.Me.Velocity.FlatLen() < MinDriftSpeed;
			bool needThrottle = bot.Me.Velocity.FlatLen() < MinDriftNeedThrottle;
			bool airborne = !bot.Me.IsGrounded;
			bool timedOut = Game.Time - _startTime > Timeout;

			if (aligned || tooSlow || airborne || timedOut)
			{
				
				// Fin : on relâche tout (surtout le frein à main) et on redevient interruptible.
				bot.Controller.Handbrake = false;
				bot.Controller.Throttle = 0f;
				bot.Controller.Boost = false;
				Interruptible = true;
				Finished = true;
				return;
			}

			if (_turnSign == 0)
				_turnSign = yaw >= 0f ? 1 : -1;

			// Cœur de la mécanique : frein à main + braquage plein du bon côté, sans avancer ni booster.
			bot.Controller.Steer = _turnSign;
			bot.Controller.Handbrake = true;
			bot.Controller.Throttle = 0f;
			bot.Controller.Boost = false;
			if (needThrottle)
			{
				bot.Controller.Throttle = 1f;
			}
		}

		/// <summary>
		/// Distance parcourue pendant la glisse pour pivoter le nez de <paramref name="angle"/> (rad)
		/// à la vitesse <paramref name="speed"/>. La voiture continue ~tout droit à <paramref name="speed"/>
		/// pendant que le nez tourne à <see cref="DriftYawRate"/> : t = angle/ω, d = speed·t. Sert à
		/// ANTICIPER le déclenchement pour finir la glisse au point voulu, pas 300 uu plus loin.
		/// </summary>
		public static float SlideDistance(float speed, float angle)
		{
			return MathF.Max(speed, 0f) * MathF.Max(angle, 0f) / DriftYawRate;
		}

		/// <summary>
		/// Faut-il enclencher le fast-drift MAINTENANT pour arriver au point orienté sur
		/// <paramref name="exitDirection"/> ?
		/// <para>Vrai si : au sol, assez rapide, l'écart de cap dépasse
		/// <see cref="DriftTriggerAngle"/>, le point est <b>proche</b>
		/// (<see cref="DriftMaxDistance"/> — c'est la garde qui décide si le drift est rentable),
		/// ET il ne reste plus que la distance de glisse à parcourir
		/// (<see cref="SlideDistance"/>, pour finir la glisse SUR le point).</para>
		/// </summary>
		/// <param name="remainingDistance">Distance à plat restant jusqu'au point d'arrivée.</param>
		public static bool ShouldStart(Car car, Vec3 exitDirection, float remainingDistance)
		{
			if (!car.IsGrounded)
				return false;

			// Loin : on a la place d'arcer sans rien perdre. Drifter coûterait plus que ça ne rapporte.
			if (remainingDistance > DriftMaxDistance)
				return false;

			float speed = car.Velocity.FlatLen();
			if (speed < DriftMinEntrySpeed)
				return false;

			float angle = car.Forward.FlatAngle(exitDirection.FlatNorm());
			if (angle < DriftTriggerAngle)
				return false;

			return remainingDistance <= SlideDistance(speed, angle);
		}
	}
}
