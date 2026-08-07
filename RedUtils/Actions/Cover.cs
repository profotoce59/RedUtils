using System;
using System.Drawing;
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
		/// <summary>Vrai quand on aborde le poste directement (bien aligné) ; faux tant qu'on rejoint
		/// d'abord le point de staging pour se présenter face à la balle.</summary>
		private bool _staged;

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
		/// <summary>Recul du point de staging derrière le poste (côté opposé à la balle). On aborde le
		/// poste depuis là, en roulant VERS la balle, pour arriver nez dans le bon sens. Gardé sous
		/// <see cref="DriveRetargetDistance"/> pour que le passage staging→poste ne reconstruise pas le Drive.</summary>
		private const float StageOffset = 700f;
		/// <summary>Écart de cap (rad) sous lequel rouler DROIT au poste nous ferait déjà arriver face à
		/// la balle : plus besoin de staging, on file au poste.</summary>
		private const float LineUpEnter = 0.6f;
		/// <summary>Hystérésis : au-delà de cet écart (ex. la balle a basculé de côté) on repasse par le
		/// staging plutôt que d'arriver de travers.</summary>
		private const float LineUpExit = 1.2f;

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
			}
			else
			{
				if (_holding && distance > ReleaseRadius)
				{
					_holding = false;
					_staged = false;   // on ressort : on se re-présentera face à la balle
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
					ApproachViaStaging(bot, distance);
			}

			if (Fixes.DebugCover && bot.Me.Name == "MyBot")
				DrawDebug(bot, distance);
		}

		private float _lastDebug = -1f;

		/// <summary>
		/// Diagnostic de la tenue de poste (Fixes.DebugCover). Rendu 3D à chaque tick + log 10x/s.
		/// Le rendu superpose le cap VOULU (nez → balle, cyan) et le cap RÉEL de la voiture (rouge) :
		/// leur divergence EST le symptôme « pas orienté vers la balle », et l'état (APPROCHE/HOLD)
		/// en donne la cause — en APPROCHE la voiture suit son déplacement, elle ne se tourne vers la
		/// balle qu'une fois le poste tenu (HOLD).
		/// </summary>
		private void DrawDebug(RUBot bot, float distance)
		{
			Vec3 toFace = bot.Me.Location.FlatDirection(FacePoint);
			Vec3 stage = StagingPoint();
			bot.Renderer.Line3D(Target, Target + new Vec3(0f, 0f, 200f), Color.Lime);                       // le poste
			bot.Renderer.Line3D(stage, stage + new Vec3(0f, 0f, 200f), Color.Magenta);                      // le staging
			bot.Renderer.Line3D(bot.Me.Location, bot.Me.Location + toFace * 300f, Color.Cyan);               // cap voulu
			bot.Renderer.Line3D(bot.Me.Location, bot.Me.Location + bot.Me.Forward.FlatNorm() * 300f, Color.Red); // cap réel

			if (Game.Time - _lastDebug < 0.1f)
				return;
			_lastDebug = Game.Time;

			bool drifting = _drive != null && _drive.Action is FastDrift;
			string state = drifting ? "DRIFT" : !bot.Me.IsGrounded ? "AIR" : _holding ? "HOLD" : _staged ? "APPROCHE" : "STAGING";
			float capErr = bot.Me.Forward.FlatAngle(toFace) * 180f / MathF.PI;
			/*Console.WriteLine($"[{Game.Time:F1}s][Cover] {state} distPoste={distance:F0} capErr={capErr:F0}° " +
				$"turnSign={_turnSign} v={bot.Me.Velocity.FlatLen():F0} " +
				$"thr={bot.Controller.Throttle:F1} steer={bot.Controller.Steer:F1} hb={bot.Controller.Handbrake}");*/
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

		/// <summary>
		/// Rejoindre le poste en se présentant FACE à la balle. Si rouler droit au poste nous y ferait
		/// déjà arriver nez vers la balle (écart de cap &lt; <see cref="LineUpEnter"/>), on y va
		/// directement. Sinon on passe d'abord par un point de staging du côté opposé à la balle
		/// (<see cref="StagingPoint"/>) : le dernier tronçon staging→poste pointe alors vers la balle,
		/// donc on arrive dans le bon sens au lieu de devoir faire demi-tour sur place.
		///
		/// <para>Hystérésis (<see cref="LineUpExit"/>) pour ne pas osciller entre les deux, et pour
		/// re-passer par le staging si la balle bascule franchement de côté pendant l'approche.</para>
		/// </summary>
		private void ApproachViaStaging(RUBot bot, float distanceToPost)
		{
			Vec3 approachDir = bot.Me.Location.FlatDirection(Target);   // là où l'on irait, droit au poste
			Vec3 faceDir = Target.FlatDirection(FacePoint);            // là où l'on veut regarder au poste
			float lineUp = approachDir.FlatAngle(faceDir);

			if (!_staged && lineUp < LineUpEnter)
				_staged = true;
			else if (_staged && lineUp > LineUpExit)
				_staged = false;

			if (_staged)
			{
				// Aligné : on file au poste, en freinant pour s'y arrêter.
				Approach(bot, distanceToPost);
			}
			else
			{
				// Pas aligné : rejoindre d'abord le staging, sans freiner (on veut le TRAVERSER en
				// roulant vers la balle, pas s'y arrêter — le basculement _staged se fera en chemin).
				Vec3 stage = StagingPoint();
				// Arriver au staging déjà orienté vers le poste (= vers la balle), en driftant au besoin.
				DriveTo(bot, stage, bot.Me.Location.FlatDist(stage), brake: false, exitDirection: stage.FlatDirection(Target));
			}
		}

		/// <summary>
		/// Point d'où aborder le poste en roulant vers la balle : le poste décalé de
		/// <see cref="StageOffset"/> du côté OPPOSÉ à la balle. Le tronçon staging→poste pointe donc
		/// vers la balle. Jamais derrière la ligne de but (on garde <see cref="GoalLineMargin"/> devant).
		/// </summary>
		private Vec3 StagingPoint()
		{
			Vec3 awayFromBall = (Target - FacePoint).FlatNorm();
			Vec3 stage = Target + awayFromBall * StageOffset;
			float limit = Field.Length / 2f - GoalLineMargin;
			stage.y = Utils.Cap(stage.y, -limit, limit);
			stage.z = 0f;
			return stage;
		}

		/// <summary>Rejoindre le poste à une vitesse dont on peut encore s'arrêter dessus.</summary>
		private void Approach(RUBot bot, float distance)
		{
			// Aborder le poste en visant à l'arrivée le nez vers la balle (drift anticipé si l'angle est trop grand).
			DriveTo(bot, Target, distance, brake: true, exitDirection: Target.FlatDirection(FacePoint));
		}

		/// <summary>
		/// Conduit le Drive interne vers un point. <paramref name="brake"/> plafonne la vitesse par la
		/// distance de freinage (<c>v² = 2·a·d</c>) pour s'ARRÊTER dessus (approche du poste) ; sans
		/// frein on file à vitesse pleine (traversée du staging). Le Drive est reconstruit si le point
		/// dérive de plus de <see cref="DriveRetargetDistance"/> — sinon on mute juste sa cible, pour ne
		/// pas remettre son <c>timeOnGround</c> à zéro (ce qui interdirait dodges/wavedashes).
		/// </summary>
		private void DriveTo(RUBot bot, Vec3 point, float distance, bool brake, Vec3 exitDirection)
		{
			if (point.Dist(_driveBuiltFor) > DriveRetargetDistance)
			{
				_drive = new Drive(bot.Me, point, Car.MaxSpeed, allowDodges: true, wasteBoost: false, exitDirection: exitDirection);
				_driveBuiltFor = point;
			}

			float speed = Car.MaxSpeed;
			if (brake)
			{
				// v² = 2·a·d : la vitesse maximale depuis laquelle il reste de quoi freiner.
				float braking = MathF.Sqrt(2f * Car.BrakeAccel * MathF.Max(distance - HoldRadius, 0f)) * BrakeSafety;
				speed = MathF.Min(Car.MaxSpeed, braking);
			}

			_drive.Target = point;
			// Direction du nez voulue À L'ARRIVÉE : Drive s'en sert pour s'aligner (line-up leg) ou,
			// si l'angle est trop grand, enclencher un fast-drift anticipé (voir Drive/FastDrift.ShouldStart).
			_drive.ExitDirection = exitDirection;
			_drive.TargetSpeed = speed;
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
