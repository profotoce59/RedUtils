using RedUtils.Math;
using System;
using System.Drawing;

namespace RedUtils
{
	/// <summary>A jump shot action, where the car jumps and dodges into the ball</summary>
	public class JumpShot : Shot
	{
		/// <summary>Whether or not this shot has finished</summary>
		public override bool Finished { get; internal set; }
		/// <summary>Whether or not this shot can be interrupted</summary>
		public override bool Interruptible { get; internal set; }

		/// <summary>The future ball state at which time we are planning to hit the shot</summary>
		public override BallSlice Slice { get; internal set; }
		/// <summary>The exact position we will hit the ball towards</summary>
		public override Vec3 ShotTarget { get; internal set; }
		/// <summary>The final position of the car at the point of collision</summary>
		public override Vec3 TargetLocation { get; internal set; }
		/// <summary>The direction from the car to the ball at the point of collision</summary>
		public override Vec3 ShotDirection { get; internal set; }

		/// <summary>The direction the car should be facing right before dodging</summary>
		public Vec3 DodgeDirection { get; internal set; }
		/// <summary>This shot's arrive sub action, which will take us to the ball</summary>
		public Arrive ArriveAction { get; internal set; }

		/// <summary>The amount of boost we have when starting this action</summary>
		private readonly int _startBoostAmount = 0;
		/// <summary>The length between updates to the target location, and shot direction</summary>
		private readonly float _updateInterval = 0.2f;
		/// <summary>Keeps track of the time since the last update</summary>
		private float _updateTimer = 0;
		/// <summary>Whether or not we have started jumping</summary>
		private bool _jumped = false;
		/// <summary>Whether or not we have left the ground while performing this action</summary>
		private bool _leftGround = false;
		/// <summary>The amount of time since we have started jumping</summary>
		private float _jumpElapsed = 0;
		/// <summary>Keeps track of the last time the ball was touched</summary>
		private float _latestTouchTime = -1;
		/// <summary>When we dodge we have to let go of jump for a few frames. This counts those frames</summary>
		private int _step = 0;
		/// <summary>DEBUG mural : dernier état loggé, pour throttler la trace.
		/// Static car l'action est recréée chaque frame (re-sélection) : des champs d'instance seraient réinitialisés.</summary>
		private static string _dbgLastReason = null;
		private static float _dbgLastTime = -1;

		/// <summary>Initializes a new jump shot action, with a specific ball slice and a shot target</summary>
		public JumpShot(Car car, BallSlice slice, Vec3 shotTarget)
		{
			Finished = false;
			Interruptible = true;

			Slice = slice;
			ShotTarget = shotTarget;
			_startBoostAmount = car.Boost;
			ArriveAction = null;

			SetTargetLocation(car, true);
		}

		/// <summary>Sets the target location and the shot direction based on the velocity of the ball, the position of the car currently, and other factors</summary>
		/// <param name="init">Whether or not this is being run during the initialization of this action</param>
		private void SetTargetLocation(Car car, bool init = false)
		{
			// How much time until we should hit the ball
			float timeRemaining = Slice.Time - Game.Time;

			// Predicts the ball's state after contact
			Ball ballAfterHit = Slice.ToBall();
			Vec3 carFinVel = (((init ? Slice.Location : TargetLocation) - car.Location) / timeRemaining + (init ? car.Location.FlatDirection(Slice.Location) * 500 : ShotDirection.FlatNorm() * 500)).Cap(0, Car.MaxSpeed);
			ballAfterHit.velocity = (carFinVel * 6 + Slice.Velocity) / 7;

			// Predicts how long it will take the ball to hit the target after being hit
			Vec3 directionToScore = Slice.Location.FlatDirection(ShotTarget);
			float velocityDiff = (carFinVel - Slice.Velocity).Length();
			float timeToScore = Slice.Location.FlatDist(ShotTarget) / Utils.Cap(velocityDiff * Utils.ShotPowerModifier(velocityDiff) + ballAfterHit.velocity.Dot(directionToScore), 500, Ball.MaxSpeed);

			// Calculates the shot direction, and target location
			ballAfterHit.velocity = (carFinVel + Slice.Velocity * 2) / 3f;
			ShotDirection = ballAfterHit.PredictLocation(timeToScore).Direction(ShotTarget);
			TargetLocation = Slice.Location - ShotDirection * 165;

			// Gets the closest surface to the target location, and get that surface's normal
			Surface surface = Field.NearestSurface(Slice.Location);
			Vec3 normal = surface.Normal;

			// Calculates a new angle for the shot direction and an angle for the dodge direction, based on whats possible for the car to rotate to
			float height = (TargetLocation - surface.Limit(TargetLocation)).Dot(normal);
			float angle = MathF.Asin(ShotDirection.Dot(normal));
			float dodgeAngle = MathF.Min(MathF.Min(angle + 0.25f, MathF.PI * 0.4f), MathF.Max(Utils.PredictRotation(Car.PitchAngularAccel, Utils.TimeToJump(normal, MathF.Max(height, 50) - 17)) + 0.2f, 0.6f));
			angle = dodgeAngle - 0.25f;

			// Adjusts the shot direction, dodge direction, and target location using the values calculated before
			ShotDirection = ShotDirection.FlatNorm(normal) * MathF.Cos(angle) + normal * MathF.Sin(angle);
			DodgeDirection = ShotDirection.FlatNorm(normal) * MathF.Cos(dodgeAngle) + normal * MathF.Sin(dodgeAngle);
			TargetLocation = Slice.Location - ShotDirection * 165;

			// if the target location is too close to the surface, change the shot direction and the target location so it is flatter against the surface
			float ballHeight = (Slice.Location - surface.Limit(Slice.Location)).Dot(normal);
			height = (TargetLocation - surface.Limit(TargetLocation)).Dot(normal);
			if (height < 40)
			{
				angle = MathF.Asin(Utils.Cap((ballHeight - 40) / 165, -1, 1));
				ShotDirection = (ShotDirection.FlatNorm(normal) * MathF.Cos(angle) + normal * MathF.Sin(angle)).Normalize();
				TargetLocation = Slice.Location - ShotDirection * 165;
			}
			float jumpTime = Utils.TimeToJump(normal, MathF.Max(height, 40) - 17);

			// Adjusts the horizontal offset of the target location so we don't miss, and hit it with power
			TargetLocation -= ShotDirection.FlatNorm(normal) * 25;
			// If the target is on the wall, we have to adjust the target slightly to adjust for gravity and other factors
			if (normal.Dot(Vec3.Up) < 0.95f)
			{
				TargetLocation = TargetLocation.Flatten() + Vec3.Up * (Slice.Location.z + Utils.Cap(TargetLocation.z - Slice.Location.z, -120, 120));
				TargetLocation -= Game.Gravity / 2 * MathF.Pow(jumpTime, 2);
			}

			if (init)
			{
				// If this is during initialization, we need to create the arrive action
				ArriveAction = new Arrive(car, TargetLocation, ShotDirection.FlatNorm(normal), Slice.Time, true, jumpTime + 0.1f);
				return;
			}
			// Otherwise, just update the arrive action
			ArriveAction.Target = TargetLocation;
			ArriveAction.Direction = ShotDirection.FlatNorm(normal);
			ArriveAction.RecoveryTime = jumpTime + 0.1f;
		}

		/// <summary>Performs this jump shot</summary>s
		public override void Run(RUBot bot)
		{
			// How much time until we should hit the ball
			float timeRemaining = Slice.Time - Game.Time;

			// Updates latest touch time with the last time the ball was hit
			if (_latestTouchTime < 0 && Ball.LatestTouch != null)
				_latestTouchTime = Ball.LatestTouch.Time;

			// Gets the nearest surface to our bot, to be used later
			Surface surface = Field.NearestSurface(bot.Me.Location);

			if (!_jumped)
			{
				// Before we jump, we gotta approach the ball
				ArriveAction.Run(bot);

				// While approaching, this shot is interrutpible only if the arrive action is
				Interruptible = ArriveAction.Interruptible;
				// Check if we have left the ground
				_leftGround = _leftGround || !bot.Me.IsGrounded;

				// Calculates the height of the target and how long it would take to jump that height
				float height = Utils.Cap((TargetLocation - surface.Limit(TargetLocation)).Dot(surface.Normal) - 17, 1, 270);
				float timeToJump = MathF.Max(Utils.TimeToJump(surface.Normal, height, false), 0.15f);
				// Assuming we drive straight to the target location, how long should it take?
				float eta = ArriveAction.Eta(bot.Me);

				_updateTimer += bot.DeltaTime;

				// Décompose chaque garde pour pouvoir tracer laquelle bloque (voir DEBUG plus bas)
				bool abortWindow = timeRemaining < timeToJump - 0.05f;
				bool abortLanded = _leftGround && bot.Me.IsGrounded;
				bool abortInvalid = !ShotValid();
				bool abortBoost = bot.Me.Boost > _startBoostAmount;
				bool abortEta = eta > MathF.Max(timeRemaining * 1.05f, timeRemaining + 0.025f);
				bool abortEarly = eta < timeRemaining - 0.25f && _updateTimer > _updateInterval;
				bool doAbort = Interruptible && (abortWindow || abortLanded || abortInvalid || abortBoost || abortEta || abortEarly);

				string dbgReason;
				if (doAbort)
				{
					// If this shot is no longer valid, or we think it's possible that a better shot exists, we finish this action
					Finished = true;
					dbgReason = "ABORT[" + (abortWindow ? "window " : "") + (abortLanded ? "landed " : "") + (abortInvalid ? "invalid " : "")
						+ (abortBoost ? "boost " : "") + (abortEta ? "eta " : "") + (abortEarly ? "early " : "") + "]";
				}
				else if (_updateTimer > _updateInterval && timeRemaining > timeToJump)
				{
					// Updates the target location and shot direction every so often, so this shot is as accurate as possible
					SetTargetLocation(bot.Me);
					_updateTimer = 0;
					dbgReason = "UPDATE";
				}
				else if (ReadyToJump(bot.Me, timeToJump))
				{
					// If we think we would hit the ball if we jump now, then jump!
					_jumped = true;
					// On committe IMMÉDIATEMENT : sans ça, Interruptible reste true cette frame, et la boucle
					// de stratégie (Bot.cs : "Action.Interruptible") remplace ce tir par un neuf à la frame
					// suivante — le saut décidé est jeté avant même d'être exécuté (observé sur mur).
					Interruptible = false;
					dbgReason = "JUMP";
				}
				else
				{
					dbgReason = "WAIT";
				}

				// DEBUG mural : trace les valeurs décisives quand la voiture est sur un mur/plafond.
				// Throttle : uniquement si la raison change OU dans la fenêtre décisive (tRem proche de tJump),
				// et jamais deux fois pour le même Game.Time (évite le spam quand le match est en pause).
				if (Fixes.DebugWallJumpShot)
				{
					Vec3 nrm = Field.NearestSurface(bot.Me.Location).Normal;
					bool decisive = timeRemaining < timeToJump + 0.12f;
					if (nrm.Dot(Vec3.Up) < 0.95f && Game.Time != _dbgLastTime && (dbgReason != _dbgLastReason || decisive))
					{
						float gravityFactor =  0.5f;
						Vec3 finPosDbg = bot.Me.LocationAfterJump(timeRemaining, 0) - gravityFactor * Game.Gravity * MathF.Pow(timeRemaining, 2);
						float alignDbg = finPosDbg.FlatDist(TargetLocation, nrm);
						float alignThr = 50 - nrm.Dot(Vec3.Up) * 20;
						float vUp = MathF.Abs(bot.Me.Velocity.Dot(bot.Me.Up));
						Console.WriteLine($"[{Game.Time:F2}s][WallJump] {dbgReason} tRem={timeRemaining:F3} tJump={timeToJump:F3} eta={eta:F3} align={alignDbg:F0}/{alignThr:F0} vUp={vUp:F0} interr={Interruptible} h={height:F0}");
						_dbgLastReason = dbgReason;
						_dbgLastTime = Game.Time;
					}
				}
			}
			else
			{
				// Now that we are up in the air, we set interruptible to false, as we don't want to be interrupted while jumping
				Interruptible = false;
				_jumpElapsed += bot.DeltaTime;

				// Aim in our dodge direction, and air roll such that our roof is facing away from our dodge direction
				bot.AimAt(bot.Me.Location + DodgeDirection, DodgeDirection.Cross(DodgeDirection.Cross(-surface.Normal)).Normalize());

				if (timeRemaining < -0.1f || (timeRemaining > 0.4f && !ShotValid()) || (Ball.LatestTouch != null && _latestTouchTime != Ball.LatestTouch.Time && Ball.LatestTouch.PlayerIndex != bot.Index && Ball.Location.Dist(bot.Me.Location) > 200))
				{
					// If the target time has passed, or someone else hits the ball before us, then we stop this action
					Finished = true;
				}
				else if (timeRemaining > 0.075f || _jumpElapsed < 0.05f)
				{
					// Holds the jump button for as long as we need
					bot.Controller.Jump = true;
				}
				else if (_step < 3 || timeRemaining > 0.05f)
				{
					// Releases the jump button for at least 3 frames, and then wait until the right time to dodge
					bot.Controller.Jump = false;
					_step++;
				}
				else
				{
					// When we have .05 seconds until the target time, dodge into the ball
					bot.Action = new Dodge(ShotDirection.FlatNorm(), 0.1f);
				}
			}
		}

		/// <summary>Returns whether this jump shot is possible</summary>
		public override bool IsValid(Car car)
		{
			// How much time until we should hit the ball
			float timeRemaining = Slice.Time - Game.Time;

			// Calculates the height of the target
			Surface surface = Field.NearestSurface(TargetLocation);
			float height = MathF.Max((TargetLocation - surface.Limit(TargetLocation)).Dot(surface.Normal), 20);

			// Fix mural : sans marge, IsValid rejette la slice à tRem≈TimeToJump, soit AVANT que Run puisse
			// sauter (tRem<timeToJump). Comme le tir est re-sélectionné chaque frame, la slice est alors
			// abandonnée au profit d'une slice plus tardive → le saut mural ne se déclenche jamais.
			// La marge maintient la slice valide le temps que Run atteigne son point de saut.
			float jumpGate = Utils.TimeToJump(surface.Normal, height) -  0.06f ;

			// Returns true if we can get there in time, and the ball isn't too high to reach from jumping
			return Drive.GetEta(car, TargetLocation) < timeRemaining && height < 270 && timeRemaining > jumpGate;
		}

		/// <summary>Returns whether or not we should jump now</summary>
		private bool ReadyToJump(Car car, float timeToJump)
		{
			// How much time until we should hit the ball
			float timeRemaining = Slice.Time - Game.Time;

			// Offsets the predicted location by the amount we would've fallen, so we can compare correctly to the target location
			// Fix #2 : (1 / 2) est une division entière == 0 → la compensation de gravité était supprimée (jump shots muraux mal synchronisés)
			float gravityFactor =  0.5f;
			Vec3 finPos = car.LocationAfterJump(timeRemaining, 0) - gravityFactor * Game.Gravity * MathF.Pow(timeRemaining, 2);
			// Gets the nearest surface to the car'a normal
			Vec3 normal = Field.NearestSurface(car.Location).Normal;

			float alignTolerance =  50 - normal.Dot(Vec3.Up) * 20;
			return timeRemaining < timeToJump && MathF.Abs(car.Velocity.Dot(car.Up)) < 200 && finPos.FlatDist(TargetLocation, normal) < alignTolerance && Interruptible;
		}
	}
}
