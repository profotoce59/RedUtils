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
	/// <para><b>Tenue.</b> Voir <see cref="Hold"/> : l'écart de cap décide entre un arc avant (petit
	/// écart) et un demi-tour en marche arrière (grand écart).</para>
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

		private Drive _drive;
		private Vec3 _driveBuiltFor;
		private bool _holding;

		/// <summary>
		/// Sens de rotation retenu pour le pivot en cours. 0 = aucun pivot.
		///
		/// <para>Latché parce que l'écart de cap est un <c>Atan2</c> : sur une cible pile derrière
		/// nous il vaut +π ou −π selon un bruit de quelques unités, et le braquage changerait de
		/// signe d'une frame à l'autre. La voiture tremblerait sur place au lieu de tourner.</para>
		/// </summary>
		private int _turnSign;

		/// <summary>En deçà de ce rayon, on considère le poste tenu.</summary>
		private const float HoldRadius = 250f;
		/// <summary>
		/// Au-delà de ce rayon, on repart en approche.
		///
		/// <para>Doit être plus large que l'empreinte d'un demi-tour, sinon le pivot fait sortir de
		/// la bande, ce qui relance une approche, qui ramène dans la bande, qui relance un pivot —
		/// et la voiture se fige au cap qu'elle avait quand le test d'angle est passé. Un arc à
		/// basse vitesse a un rayon de ~200 uu, donc un demi-tour déplace la voiture de 300 à
		/// 500 uu : 700 laisse la marge nécessaire.</para>
		/// </summary>
		private const float ReleaseRadius = 700f;
		/// <summary>Marge sur la distance de freinage : le throttle n'est ni instantané ni parfait.</summary>
		private const float BrakeSafety = 0.8f;
		/// <summary>En deçà de cette distance, plus de dodge : on ne flippe pas juste avant de s'arrêter.</summary>
		private const float DodgeMinDistance = 1200f;
		/// <summary>Écart de cap en deçà duquel on se considère aligné sur la balle.</summary>
		private const float AlignedAngle = 0.15f;
		/// <summary>Au-delà de cet écart, l'arc avant coûte trop de terrain : on tourne en reculant.</summary>
		private const float ForwardPivotMax = 1.0f;
		/// <summary>Gaz appliqué pour pivoter vers l'avant (il faut rouler un peu pour tourner).</summary>
		private const float PivotThrottle = 0.4f;
		/// <summary>Recul supposé pour un demi-tour, utilisé pour vérifier qu'on ne rentre pas dans le but.</summary>
		private const float ReverseRoom = 450f;
		/// <summary>Marge devant la ligne de but à préserver en reculant.</summary>
		private const float GoalLineMargin = 150f;
		/// <summary>Au-dessus de cette vitesse, le frein à main aide à faire tourner la voiture.</summary>
		private const float HandbrakeSpeed = 500f;
		/// <summary>Gain du freinage de maintien : throttle plein à cette vitesse résiduelle.</summary>
		private const float StopSpeedGain = 200f;
		/// <summary>Dérive de cible au-delà de laquelle le Drive interne est reconstruit.</summary>
		private const float DriveRetargetDistance = 800f;

		public Cover(Car car, Vec3 target, Vec3 facePoint)
		{
			Finished = false;
			Interruptible = true;

			Target = target;
			FacePoint = facePoint;

			BuildDrive(car);
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
			{
				_holding = false;
				// Le pivot a pu nous placer de l'autre côté du poste : la décision marche
				// avant/arrière de Drive date de sa construction et n'est plus valable.
				BuildDrive(bot.Me);
			}
			else if (!_holding && distance < HoldRadius)
			{
				_holding = true;
			}

			if (_holding)
				Hold(bot);
			else
				Approach(bot, distance);
		}

		/// <summary>
		/// (Re)construit le Drive interne.
		///
		/// <para><c>Drive.Backwards</c> est décidé <b>une seule fois</b>, dans le constructeur
		/// (Drive.cs:56), et n'est remis à false que quand une sous-action se termine
		/// (Drive.cs:211). Réutiliser indéfiniment le même Drive fige donc ce choix : si le poste
		/// était derrière la voiture au moment de la création, elle y va en marche arrière pour
		/// toujours — et arrive dans le mauvais sens.</para>
		/// </summary>
		private void BuildDrive(Car car)
		{
			_drive = new Drive(car, Target, Car.MaxSpeed, allowDodges: true, wasteBoost: false);
			_driveBuiltFor = Target;
		}

		/// <summary>Rejoindre le poste à une vitesse dont on peut encore s'arrêter dessus.</summary>
		private void Approach(RUBot bot, float distance)
		{
			if (Target.Dist(_driveBuiltFor) > DriveRetargetDistance)
				BuildDrive(bot.Me);

			// v² = 2·a·d : la vitesse maximale depuis laquelle il reste de quoi freiner.
			float braking = MathF.Sqrt(2f * Car.BrakeAccel * MathF.Max(distance - HoldRadius, 0f)) * BrakeSafety;

			_drive.Target = Target;
			_drive.TargetSpeed = MathF.Min(Car.MaxSpeed, braking);
			_drive.AllowDodges = distance > DodgeMinDistance;
			_drive.Run(bot);

			Interruptible = _drive.Interruptible;
		}

		/// <summary>
		/// Rester sur place, nez vers la balle.
		///
		/// <para><b>Petit écart de cap</b> → arc vers l'avant : il faut de la vitesse pour tourner,
		/// et sur moins d'un radian l'arc reste dans la bande de tenue.</para>
		///
		/// <para><b>Grand écart</b> → demi-tour <b>en marche arrière</b>. Un demi-tour vers l'avant
		/// coûterait 300 à 500 uu de terrain dans une direction qu'on ne choisit pas ; en reculant,
		/// on tourne en revenant vers l'endroit d'où l'on vient. En marche arrière le nez part du
		/// côté <b>opposé</b> au braquage (le taux de lacet change de signe avec la vitesse), donc
		/// le braquage est inversé — c'est le <c>steer * throttle</c> de Noob Black
		/// (bot.py:637-638). On ne recule que s'il reste de la place devant notre ligne de but.</para>
		/// </summary>
		private void Hold(RUBot bot)
		{
			Interruptible = true;
			bot.Controller.Boost = false;

			// AimAt règle Steer/Yaw/Pitch/Roll pour la marche AVANT et renvoie les angles.
			// [1] est l'écart de cap, signé (Atan2) — contrairement à Vec3.FlatAngle, qui est un
			// Acos et ne donnerait pas le sens de rotation.
			float yaw = bot.AimAt(FacePoint)[1];
			float forwardSpeed = bot.Me.Velocity.Dot(bot.Me.Forward);

			if (MathF.Abs(yaw) <= AlignedAngle)
			{
				// Aligné : on annule la vitesse résiduelle et on se fige.
				_turnSign = 0;
				bot.Controller.Steer = 0f;
				bot.Controller.Throttle = Utils.Cap(-forwardSpeed / StopSpeedGain, -1f, 1f);
				bot.Controller.Handbrake = false;
				return;
			}

			if (_turnSign == 0)
				_turnSign = yaw >= 0f ? 1 : -1;

			// Où finirions-nous en reculant ? Il ne faut pas que le demi-tour nous mette dans le but.
			Vec3 behind = bot.Me.Location - bot.Me.Forward.FlatNorm() * ReverseRoom;
			bool roomBehind = MathF.Abs(behind.y) < Field.Length / 2f - GoalLineMargin;

			if (MathF.Abs(yaw) > ForwardPivotMax && roomBehind)
			{
				bot.Controller.Throttle = -1f;
				bot.Controller.Steer = -_turnSign;
				bot.Controller.Handbrake = false;
			}
			else
			{
				bot.Controller.Throttle = PivotThrottle;
				bot.Controller.Steer = _turnSign;
				// Le frein à main ne fait tourner que s'il y a de la vitesse ; à l'arrêt il
				// empêcherait au contraire la voiture de pivoter.
				bot.Controller.Handbrake = bot.Me.Velocity.FlatLen() > HandbrakeSpeed;
			}
		}
	}
}
