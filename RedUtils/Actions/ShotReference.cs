using System;
using RedUtils.Interop;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>Phase aérienne à rejouer, selon le type de tir en cours.</summary>
	public enum ShotKind
	{
		/// <summary>Aucune référence : la recherche n'a rien à perturber, elle doit renoncer.</summary>
		None,
		/// <summary><see cref="JumpShot"/> — montée en visant <c>DodgeDirection</c>, puis dodge.</summary>
		Jump,
		/// <summary><see cref="DoubleJumpShot"/> — saut tenu, relâche, second saut, puis visée.</summary>
		DoubleJump,
		/// <summary><see cref="AerialShot"/> — saut (simple ou double), puis correction continue de
		/// l'écart à la position visée au boost, et dodge final si le temps le permet.</summary>
		Aerial,
	}

	/// <summary>
	/// État mutable du contrôleur de référence. À remettre à zéro <b>avant chaque candidat</b> :
	/// c'est lui qui porte les compteurs de frames de la mécanique saut/relâche/dodge, et le
	/// traîner d'un candidat à l'autre décalerait tous les suivants.
	/// </summary>
	public struct ReferenceState
	{
		public int Step;
		public float JumpElapsed;
		public bool Dodging;
		public bool DodgeInputSet;
		public float DodgeYaw, DodgePitch;
		/// <summary>Aerial : la séquence de saut est terminée, on ne fait plus que corriger.</summary>
		public bool Jumped;
		/// <summary>Aerial : le second saut est en cours — toutes les directions sont relâchées pour
		/// ne pas déclencher un flip par accident.</summary>
		public bool CurrentlyDoubleJumping;
	}

	/// <summary>
	/// La loi de commande que le <see cref="Shot"/> en cours <b>va</b> appliquer après le décollage,
	/// rejouée dans la simulation.
	///
	/// <para><b>Pourquoi elle existe.</b> Sans elle, la recherche inventait la séquence d'inputs de
	/// toutes pièces (plein gaz, tout droit, un flip) et simulait une voiture qui n'avait rien à voir
	/// avec celle que le bot allait piloter. Sur une seconde de vol, l'écart est tel qu'aucun candidat
	/// n'atteignait la balle. Avec la référence, la recherche redevient ce qu'elle doit être : une
	/// perturbation locale d'une trajectoire connue.</para>
	///
	/// <para><b>Ce qui est rejoué, et ce qui ne l'est pas.</b> Uniquement la branche <c>_jumped</c>
	/// de <c>Shot.Run</c> — celle qui s'applique une fois en l'air. C'est précisément là que
	/// <c>JumpShot</c> et <c>DoubleJumpShot</c> cessent d'appeler leur sous-action <c>Arrive</c>
	/// (<c>JumpShot.cs:151</c> n'est plus atteint) et ne dépendent plus que de constantes portées par
	/// le tir. La phase d'approche au sol, elle, reste hors de portée : elle passe par <c>Arrive</c>
	/// et <c>Drive</c>, qui lisent l'état global.</para>
	///
	/// <para><b>Pas d'accès aux statiques.</b> Tout ce qui vient du thread de jeu (<c>Game.Gravity</c>,
	/// les directions du tir) est capturé une fois dans <see cref="FromShot"/>, appelée côté jeu.
	/// Les méthodes d'instance ne lisent ensuite que l'état simulé qu'on leur passe.</para>
	/// </summary>
	public sealed class ShotReference
	{
		public readonly ShotKind Kind;
		/// <summary>Direction visée pendant la montée (JumpShot). Nulle pour les autres.</summary>
		public readonly Vec3 DodgeDirection;
		/// <summary>Direction dans laquelle on frappe — celle du dodge final.</summary>
		public readonly Vec3 ShotDirection;
		/// <summary>Position visée par la voiture à l'instant du contact.</summary>
		public readonly Vec3 TargetLocation;
		/// <summary>Normale de la surface au moment où la référence a été capturée.</summary>
		public readonly Vec3 SurfaceNormal;
		/// <summary>Position de la balle au contact prévu — Aerial s'en sert comme référence de toit.</summary>
		public readonly Vec3 SliceLocation;
		/// <summary>Aerial : le tir prévoit-il un double saut au décollage.</summary>
		public readonly bool DoubleJumping;
		/// <summary>Gravité capturée côté thread de jeu, pour ne pas lire <c>Game</c> ailleurs.</summary>
		public readonly Vec3 Gravity;

		public bool Valid => Kind != ShotKind.None;

		private ShotReference(ShotKind kind, Vec3 dodgeDirection, Vec3 shotDirection,
			Vec3 targetLocation, Vec3 surfaceNormal, Vec3 gravity,
			Vec3 sliceLocation = default, bool doubleJumping = false)
		{
			Kind = kind;
			DodgeDirection = dodgeDirection;
			ShotDirection = shotDirection;
			TargetLocation = targetLocation;
			SurfaceNormal = surfaceNormal;
			Gravity = gravity;
			SliceLocation = sliceLocation;
			DoubleJumping = doubleJumping;
		}

		/// <summary>
		/// Capture la référence depuis le tir en cours. <b>À appeler sur le thread de jeu</b> : c'est
		/// ici, et seulement ici, qu'on lit <c>Game</c> et l'action vivante.
		///
		/// <para>Les trois tirs qui décollent sont couverts. Ne restent en <see cref="ShotKind.None"/>
		/// que ceux qui ne quittent jamais le sol — un <c>GroundShot</c> ou un <c>QuickShot</c> — pour
		/// lesquels la question ne se pose pas, puisque la recherche ne part qu'au décollage.</para>
		/// </summary>
		public static ShotReference FromShot(Shot shot, Vec3 surfaceNormal)
		{
			switch (shot)
			{
				case JumpShot js:
					return new ShotReference(ShotKind.Jump, js.DodgeDirection, js.ShotDirection,
						js.TargetLocation, surfaceNormal, Game.Gravity);

				case DoubleJumpShot djs:
					return new ShotReference(ShotKind.DoubleJump, Vec3.Zero, djs.ShotDirection,
						djs.TargetLocation, surfaceNormal, Game.Gravity);

				case AerialShot ase:
					return new ShotReference(ShotKind.Aerial, Vec3.Zero, ase.ShotDirection,
						ase.TargetLocation, surfaceNormal, Game.Gravity,
						ase.Slice.Location, ase.DoubleJumping);

				default:
					return new ShotReference(ShotKind.None, Vec3.Zero, Vec3.Zero, Vec3.Zero,
						surfaceNormal, Game.Gravity);
			}
		}

		/// <summary>
		/// Inputs de référence pour un tick, à partir de l'état simulé.
		/// </summary>
		/// <param name="car">État de la voiture dans la simulation, ce tick.</param>
		/// <param name="timeRemaining">Secondes restantes avant le contact prévu.</param>
		/// <param name="dt">Pas de temps de la simulation (1/120 s).</param>
		/// <param name="st">Compteurs de frames, propres au candidat en cours.</param>
		public RocketSimNative.RSCarControls Controls(in RocketSimNative.RSCarState car,
			float timeRemaining, float dt, ref ReferenceState st)
		{
			RocketSimNative.RSCarControls c = default;
			if (Kind == ShotKind.None)
				return c;

			st.JumpElapsed += dt;
			Vec3 pos = RocketSimArena.ToRU(car.Pos);

			if (Kind == ShotKind.Jump)
			{
				// Visée identique à JumpShot.Run : nez sur la direction de dodge, toit orienté à
				// l'opposé de celle-ci par rapport à la surface.
				Vec3 up = DodgeDirection.Cross(DodgeDirection.Cross(-SurfaceNormal)).Normalize();
				AimAt(in car, pos + DodgeDirection, up, ref c);

				if (st.Dodging)
				{
					ApplyDodge(in car, ShotDirection.FlatNorm(), ref c, ref st);
				}
				else if (timeRemaining > 0.075f || st.JumpElapsed < 0.05f)
				{
					// Maintien du saut tant qu'il reste du temps.
					c.Jump = 1;
				}
				else if (st.Step < 3 || timeRemaining > 0.05f)
				{
					// Relâche obligatoire d'au moins 3 frames avant de pouvoir dodger.
					c.Jump = 0;
					st.Step++;
				}
				else
				{
					st.Dodging = true;
					ApplyDodge(in car, ShotDirection.FlatNorm(), ref c, ref st);
				}

				return c;
			}

			if (Kind == ShotKind.Aerial)
			{
				// Le dodge final remplace l'action dans le vrai code : une fois lancé, plus rien
				// d'autre ne pilote.
				if (st.Dodging)
				{
					ApplyDodge(in car, ShotDirection.FlatNorm(), ref c, ref st);
					return c;
				}

				// Remis à zéro à chaque frame, comme AerialShot.Run le fait en tête de branche.
				st.CurrentlyDoubleJumping = false;

				if (!st.Jumped)
				{
					if (st.JumpElapsed <= Car.JumpMaxDuration)
					{
						c.Jump = 1;
					}
					else if (st.Step < 3 && DoubleJumping)
					{
						c.Jump = 0;
						st.Step++;
					}
					else if (st.Step < 6 && DoubleJumping)
					{
						c.Jump = 1;
						st.CurrentlyDoubleJumping = true;
						st.Step++;
					}
					else
					{
						st.Jumped = true;
					}
				}

				Vec3 velocity = RocketSimArena.ToRU(car.Vel);
				Vec3 roof = RocketSimArena.ToRU(car.Up);

				// Où la voiture sera au contact si elle ne fait plus rien, selon l'étape du saut.
				Vec3 finPos = st.Jumped
					? Ballistic(pos, velocity, Gravity, timeRemaining)
					: LocationAfterJump(pos, velocity, roof, Gravity, car.IsOnGround != 0,
						DoubleJumping && car.HasDoubleJumped == 0, timeRemaining, st.JumpElapsed);

				Vec3 offset = TargetLocation - finPos;
				Vec3 forward = RocketSimArena.ToRU(car.Forward);

				// Toit tourné vers la balle une fois le saut fini — c'est ce qui garde la voiture
				// dans le bon sens pour frapper, et non simplement « en l'air ».
				Vec3 aimUp = st.Jumped ? (SliceLocation - pos).Normalize() : Vec3.Up;
				AimAt(in car, pos + offset, aimUp, ref c);

				float along = offset.Dot(forward);
				float safeRemaining = MathF.Max(timeRemaining, 1e-4f);

				c.Boost = along / safeRemaining
						>= (Car.BoostAccelAir + Car.AirThrottleAccel) * MathF.Max(dt, 13f / 120f)
					&& offset.Angle(forward) < 0.4f ? 1 : 0;

				c.Throttle = Utils.Cap(
					along / safeRemaining / (Car.AirThrottleAccel * MathF.Max(dt, 1f / 120f)), -1, 1);

				if (st.CurrentlyDoubleJumping)
				{
					// Toute direction pendant le second saut déclencherait un flip au lieu du saut.
					c.Steer = 0; c.Yaw = 0; c.Pitch = 0; c.Roll = 0;
				}
				else if (!DoubleJumping && st.JumpElapsed < 1.45f
					&& timeRemaining < 0.1f && offset.Length() < 100)
				{
					// Assez proche et assez tôt pour dodger dans la balle : le vrai tir le fait, et
					// l'impulsion du dodge change franchement la frappe.
					st.Dodging = true;
					ApplyDodge(in car, ShotDirection.FlatNorm(), ref c, ref st);
				}
				else if (offset.Length() < 50)
				{
					AimAt(in car, pos + ShotDirection, (SliceLocation - pos).Normalize(), ref c);
				}

				return c;
			}

			// --- DoubleJump : saut tenu, relâche 3 frames, second saut 3 frames, puis visée ---
			if (st.JumpElapsed < Car.JumpMaxDuration)
			{
				c.Jump = 1;
			}
			else if (st.Step < 3)
			{
				c.Jump = 0;
				st.Step++;
			}
			else if (st.Step < 6)
			{
				c.Jump = 1;
				st.Step++;
			}
			else
			{
				// Écart entre où la balistique nous emmène et où il faudrait être. La voiture a déjà
				// consommé son second saut : PredictLocation se réduit ici à une chute libre.
				Vec3 vel = RocketSimArena.ToRU(car.Vel);
				Vec3 finPos = pos + vel * timeRemaining + Gravity * 0.5f * timeRemaining * timeRemaining;
				Vec3 offset = TargetLocation - finPos;
				Vec3 forward = RocketSimArena.ToRU(car.Forward);
				Vec3 offsetDir = offset.Normalize();

				bool needsBoost = timeRemaining > 0f
					&& offset.Dot(forward) / timeRemaining
						>= (Car.BoostAccelAir + Car.AirThrottleAccel) * MathF.Max(dt, 13f / 120f)
					&& offsetDir.Dot(forward) > 0.75f;

				c.Boost = needsBoost ? 1 : 0;
				c.Throttle = offsetDir.Dot(forward) > 0.5f ? 1 : 0;

				AimAt(in car, pos + ShotDirection, Vec3.Up, ref c);
			}

			return c;
		}

		/// <summary>Chute libre : <c>Car.PredictLocation</c>, qui n'est rien d'autre.</summary>
		private static Vec3 Ballistic(Vec3 pos, Vec3 vel, Vec3 gravity, float time)
		{
			return pos + vel * time + gravity * 0.5f * time * time;
		}

		/// <summary>
		/// <c>Car.LocationAfterJump</c> / <c>LocationAfterDoubleJump</c>, recopiées à l'identique :
		/// la balistique augmentée de ce que le saut en cours va encore ajouter.
		/// </summary>
		private static Vec3 LocationAfterJump(Vec3 pos, Vec3 vel, Vec3 up, Vec3 gravity,
			bool grounded, bool secondJumpAvailable, float time, float elapsed)
		{
			float jumpTimeRemaining = Utils.Cap(Car.JumpMaxDuration - elapsed, 0, Car.JumpMaxDuration);
			float stickTimeRemaining = Utils.Cap(0.05f - elapsed, 0, 0.05f);

			Vec3 result = pos + vel * time + gravity * 0.5f * time * time
				+ (grounded ? up * Car.JumpVel * time : Vec3.Zero)
				+ up * Car.JumpAccel * jumpTimeRemaining * (time - 0.5f * jumpTimeRemaining)
				- up * Car.StickyAccel * stickTimeRemaining * (time - 0.5f * stickTimeRemaining);

			if (secondJumpAvailable)
				result += up * Car.JumpVel * (time - jumpTimeRemaining);

			return result;
		}

		/// <summary>
		/// Reproduit <see cref="Dodge"/> tel qu'il se comporte <b>démarré en l'air</b> : comme
		/// <c>_jumping</c> y vaut faux, tout le préambule saut/relâche disparaît et il ne reste que
		/// l'appui maintenu avec l'inclinaison calculée une fois pour toutes.
		/// </summary>
		private static void ApplyDodge(in RocketSimNative.RSCarState car, Vec3 direction,
			ref RocketSimNative.RSCarControls c, ref ReferenceState st)
		{
			if (!st.DodgeInputSet)
			{
				Vec3 forward = RocketSimArena.ToRU(car.Forward);
				Vec3 velocity = RocketSimArena.ToRU(car.Vel);
				Vec3 flat = forward.FlatNorm();

				Vec3 localDirection = new Vec3(-flat.Cross().Dot(direction), -flat.Dot(direction));

				float forwardVel = forward.Dot(velocity);
				float s = MathF.Abs(forwardVel) / Car.MaxSpeed;
				bool backwardsDodge = MathF.Abs(forwardVel) < 100
					? localDirection.x < 0
					: (localDirection.x >= 0) != (forwardVel > 0);

				float x = localDirection.x / (backwardsDodge ? (16f / 15f) * (1 + 1.5f * s) : 1);
				float y = localDirection.y / (1 + 0.9f * s);

				Vec3 input = new Vec3(x, y).Normalize();
				st.DodgeYaw = input.x;
				st.DodgePitch = input.y;
				st.DodgeInputSet = true;
			}

			c.Yaw = st.DodgeYaw;
			c.Pitch = st.DodgePitch;
			c.Jump = 1;
		}

		/// <summary>
		/// Version statique de <c>RUBot.AimAt</c> (Tools.cs:41), paramétrée par l'état simulé au lieu
		/// de <c>Me</c>. Même boucle PD, mêmes gains — <c>Me.Local(v)</c> n'étant qu'un produit
		/// scalaire de <c>v</c> sur Forward / Right / Up.
		/// </summary>
		private static void AimAt(in RocketSimNative.RSCarState car, Vec3 targetLocation, Vec3 up,
			ref RocketSimNative.RSCarControls c)
		{
			Vec3 pos = RocketSimArena.ToRU(car.Pos);
			Vec3 forward = RocketSimArena.ToRU(car.Forward);
			Vec3 right = RocketSimArena.ToRU(car.Right);
			Vec3 roof = RocketSimArena.ToRU(car.Up);
			Vec3 angVel = RocketSimArena.ToRU(car.AngVel);

			Vec3 toTarget = targetLocation - pos;
			Vec3 localTarget = new Vec3(toTarget.Dot(forward), toTarget.Dot(right), toTarget.Dot(roof));

			Vec3 safeUp = up.Length() != 0 ? up.Normalize() : Vec3.Up;
			Vec3 localUp = new Vec3(safeUp.Dot(forward), safeUp.Dot(right), safeUp.Dot(roof));

			Vec3 localAngVel = new Vec3(angVel.Dot(forward), angVel.Dot(right), angVel.Dot(roof));

			float pitchAngle = MathF.Atan2(localTarget.z, localTarget.x);
			float yawAngle = MathF.Atan2(localTarget.y, localTarget.x);
			float rollAngle = MathF.Atan2(localUp.y, localUp.z);

			c.Steer = SteerPD(yawAngle, -localAngVel.z * 0.01f);
			c.Pitch = SteerPD(pitchAngle, localAngVel.y * 0.2f);
			c.Yaw = SteerPD(yawAngle, -localAngVel.z * 0.15f);
			c.Roll = SteerPD(rollAngle, localAngVel.x * 0.25f);
		}

		/// <summary>Boucle proportionnelle-dérivée de <c>Tools.SteerPD</c>, recopiée à l'identique.</summary>
		private static float SteerPD(float angle, float rate)
		{
			return Utils.Cap(MathF.Pow(35 * (angle + rate), 3) / 10, -1f, 1f);
		}
	}
}
