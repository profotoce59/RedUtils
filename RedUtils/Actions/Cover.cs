using System;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>
	/// Tenir un poste : rejoindre un point <b>en s'y arrêtant</b>, puis y rester nez pointé vers un
	/// point d'intérêt (la balle). C'est le comportement d'un dernier homme — être en place et
	/// prêt à partir — et non celui d'un déplacement.
	///
	/// <para><b>Pourquoi pas <see cref="Arrive"/>.</b> Arrive fait deux choses dont aucune ne
	/// convient ici :</para>
	/// <list type="number">
	/// <item><b>Il roule à fond jusqu'au bout.</b> Sans <c>arrivalTime</c>, sa vitesse cible vaut
	/// <c>distance / (distance / MaxSpeed)</c> = MaxSpeed (Arrive.cs:56-59) : rien ne freine, la
	/// voiture traverse le point et doit faire demi-tour.</item>
	/// <item><b>Il recule sa cible pour s'aligner</b> (Arrive.cs:82) — jusqu'à 60 % du trajet le
	/// long de la direction d'arrivée. Or ici la direction visée est « face à la balle », donc
	/// vers le terrain : la cible décalée part <b>derrière</b>, c'est-à-dire dans notre but. Près
	/// de la ligne il n'y a physiquement pas la place pour cet alignement.</item>
	/// </list>
	///
	/// <para><b>Approche.</b> Vitesse plafonnée par la distance de freinage
	/// (<c>v = √(2·a·d)</c>, marge <see cref="BrakeSafety"/>). Le plafond ne mord que sur les
	/// derniers ~800 uu : au-delà, √(2·3500·d) dépasse déjà la vitesse maximale, donc le trajet
	/// n'est pas ralenti.</para>
	///
	/// <para><b>Tenue.</b> Une fois sur place : si le nez n'est pas dans l'axe de la balle, on
	/// pivote (un peu de gaz, frein à main si l'écart est grand et qu'il reste de la vitesse — le
	/// demi-tour dans le but) ; si l'alignement est bon, on tue la vitesse résiduelle. L'entrée et
	/// la sortie de tenue ont des rayons différents (<see cref="HoldRadius"/> /
	/// <see cref="ReleaseRadius"/>) : sans cette bande morte, le pivot fait sortir du rayon, ce qui
	/// relance une approche, qui ramène dans le rayon — et la voiture tourne en rond sur place.</para>
	/// </summary>
	public class Cover : IAction
	{
		/// <summary>Une tenue de poste ne se termine jamais d'elle-même : c'est la stratégie qui en sort.</summary>
		public bool Finished { get; private set; }
		public bool Interruptible { get; private set; }

		/// <summary>Le poste à tenir. Mutable : la stratégie le déplace sans recréer l'action.</summary>
		public Vec3 Target;
		/// <summary>Le point vers lequel pointer le nez une fois en place (la balle).</summary>
		public Vec3 FacePoint;

		private readonly Drive _drive;
		private bool _holding;

		/// <summary>En deçà de ce rayon, on considère le poste tenu.</summary>
		private const float HoldRadius = 200f;
		/// <summary>Au-delà de ce rayon, on repart en approche. Bande morte anti-va-et-vient.</summary>
		private const float ReleaseRadius = 450f;
		/// <summary>Marge sur la distance de freinage : le throttle n'est ni instantané ni parfait.</summary>
		private const float BrakeSafety = 0.8f;
		/// <summary>En deçà de cette distance, plus de dodge : on ne flippe pas juste avant de s'arrêter.</summary>
		private const float DodgeMinDistance = 1200f;
		/// <summary>Écart de cap en deçà duquel on se considère aligné sur la balle.</summary>
		private const float AlignedAngle = 0.15f;
		/// <summary>Gaz appliqué pour pivoter sur place (il faut rouler un peu pour tourner).</summary>
		private const float PivotThrottle = 0.35f;
		/// <summary>Au-delà de cet écart de cap, le frein à main accélère le demi-tour.</summary>
		private const float HandbrakeAngle = 1.2f;
		/// <summary>Vitesse à partir de laquelle le frein à main sert à quelque chose.</summary>
		private const float HandbrakeSpeed = 400f;
		/// <summary>Gain du freinage de maintien : throttle plein à cette vitesse résiduelle.</summary>
		private const float StopSpeedGain = 200f;

		public Cover(Car car, Vec3 target, Vec3 facePoint)
		{
			Finished = false;
			Interruptible = true;

			Target = target;
			FacePoint = facePoint;

			_drive = new Drive(car, target, Car.MaxSpeed, allowDodges: true, wasteBoost: false);
		}

		public void Run(RUBot bot)
		{
			float distance = bot.Me.Location.FlatDist(Target);

			// En l'air : on délègue à Drive, qui sait orienter la voiture pour un atterrissage propre
			// (Drive.cs:136-139). Pivoter en l'air ne ferait que la faire tomber de travers.
			if (!bot.Me.IsGrounded)
			{
				Approach(bot, distance);
				return;
			}

			if (_holding && distance > ReleaseRadius)
				_holding = false;
			else if (!_holding && distance < HoldRadius)
				_holding = true;

			if (_holding)
				Hold(bot);
			else
				Approach(bot, distance);
		}

		/// <summary>Rejoindre le poste à une vitesse dont on peut encore s'arrêter dessus.</summary>
		private void Approach(RUBot bot, float distance)
		{
			// v² = 2·a·d : la vitesse maximale depuis laquelle il reste de quoi freiner.
			float braking = MathF.Sqrt(2f * Car.BrakeAccel * MathF.Max(distance - HoldRadius, 0f)) * BrakeSafety;

			_drive.Target = Target;
			_drive.TargetSpeed = MathF.Min(Car.MaxSpeed, braking);
			_drive.AllowDodges = distance > DodgeMinDistance;
			_drive.Run(bot);

			Interruptible = _drive.Interruptible;
		}

		/// <summary>Rester sur place, nez vers la balle.</summary>
		private void Hold(RUBot bot)
		{
			Interruptible = true;

			// AimAt règle Steer/Yaw/Pitch/Roll et renvoie les angles ; [1] est l'écart de cap.
			float yaw = bot.AimAt(FacePoint)[1];
			float forwardSpeed = bot.Me.Velocity.Dot(bot.Me.Forward);

			if (MathF.Abs(yaw) > AlignedAngle)
			{
				// Pivot : une voiture à l'arrêt ne tourne pas, il faut un peu d'élan. Si on dérive
				// au-delà de ReleaseRadius, l'approche reprend la main et nous ramène.
				bot.Controller.Throttle = PivotThrottle;
				bot.Controller.Handbrake = MathF.Abs(yaw) > HandbrakeAngle
					&& bot.Me.Velocity.FlatLen() > HandbrakeSpeed;
			}
			else
			{
				// Aligné : on annule la vitesse résiduelle et on se fige.
				bot.Controller.Throttle = Utils.Cap(-forwardSpeed / StopSpeedGain, -1f, 1f);
				bot.Controller.Steer = 0f;
			}

			// Jamais de boost pour tenir un poste.
			bot.Controller.Boost = false;
		}
	}
}
