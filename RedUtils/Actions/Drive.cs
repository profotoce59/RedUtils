using System;
using System.Drawing;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>An action meant to drive the car to a certain location</summary>
	public class Drive : IAction
	{
		/// <summary>Whether or not we have arrived at our destination</summary>
		public bool Finished { get; private set; }
		/// <summary>Whether or not this action can currently be interrupted</summary>
		public bool Interruptible { get; private set; }

		/// <summary>The destination the car will drive to</summary>
		public Vec3 Target;
		/// <summary>The speed we intend to mantain while driving</summary>
		public float TargetSpeed;
		/// <summary>Whether or not we are going to drive backwards</summary>
		public bool Backwards;
		/// <summary>Whether or not we are going to allow dodges to increase speed</summary>
		public bool AllowDodges;
		/// <summary>Whether or not we are going to use any amount of boost neccesary to mantain our target speed</summary>
		public bool WasteBoost;
		/// <summary>
		/// Traite <see cref="Target"/> comme une cible MOLLE : ne jamais freiner pour tourner plus
		/// serré, ni tirer le frein à main. On préfère passer large à pleine vitesse.
		/// <para>Pour un replacement (voir <see cref="Rotate"/>) la destination est une zone, pas un
		/// point à taper : sacrifier la vitesse pour la précision est le mauvais arbitrage. À laisser
		/// à false pour tout ce qui doit arriver PRÉCISÉMENT quelque part (tirs, poste, pad).</para>
		/// </summary>
		public bool PreserveSpeed;
		/// <summary>Direction souhaitée du nez à l'ARRIVÉE (placement précis). Zéro = aucune contrainte
		/// (Drive classique). Angle faible → on décale la cible pour s'aligner (line-up leg, comme
		/// Arrive mais sans contrainte de temps) ; angle élevé → FastDrift anticipé (FastDrift.ShouldStart).</summary>
		public Vec3 ExitDirection;
		/// <summary>This action's subaction, which could be a dodge, halfflip, speedflip, etc</summary>
		public IAction Action;

		/// <summary>How long we have spent driving on the ground</summary>
		private float timeOnGround = 0;

		/// <summary>Fraction of the trip spent lining up on the arrival direction. Mirrors Arrive.Run.</summary>
		private const float ApproachLineUpFraction = 0.6f;
		/// <summary>Cap on the line-up leg, expressed as seconds of travel. Mirrors Arrive.Run.</summary>
		private const float ApproachLineUpSeconds = 1.5f;

		/// <summary>How much time until we arrive at our destination</summary>
		public float TimeRemaining { get; private set; }

		/// <summary>Initializes a new drive action</summary>
		/// <param name="target">The destination the car will drive to</param>
		/// <param name="targetSpeed">The speed we intend to mantain while driving</param>
		/// <param name="allowDodges">Whether or not we are going to allow dodges to increase speed</param>
		/// <param name="wasteBoost">>Whether or not we are going to use any amount of boost neccesary to mantain our target speed</param>
		/// <param name="exitDirection">Direction du nez souhaitée à l'arrivée (placement précis). Null = aucune contrainte.</param>
		public Drive(Car car, Vec3 target, float targetSpeed = Car.MaxSpeed, bool allowDodges = true, bool wasteBoost = false, Vec3? exitDirection = null)
		{
			Interruptible = true;
			Finished = false;

			Target = target;
			TargetSpeed = targetSpeed;
			ExitDirection = exitDirection ?? Vec3.Zero;

			float forwardsEta = GetEta(car, target, false, false);
			float backwardsEta = GetEta(car, target, true, false);

			// Only go backwards under very specific circumstances, because otherwise the bot goes backwards far too often
			Backwards = backwardsEta + 0.5f < forwardsEta && car.Forward.Dot(car.Velocity) < 500 && car.Forward.FlatAngle(car.Location.Direction(target), car.Up) > MathF.PI * 0.6f;
			AllowDodges = allowDodges;
			WasteBoost = wasteBoost;

			Action = null;
		}

		/// <summary>Drives the car toward the target destination</summary>
		public void Run(RUBot bot)
		{
			// Calculates how much time we have before we should arrive
			TimeRemaining = Distance(bot.Me) / TargetSpeed;

			// Finds the nearest surface to the target for some calculations later
			Surface targetSurface = Field.NearestSurface(Target);

			// Placement précis avec direction de sortie : si l'écart de cap est trop grand pour un
			// virage normal, on anticipe un fast-drift. FastDrift détient le savoir « maintenant ou
			// plus tard » ; Drive se contente de poser la question et de lancer la manœuvre.
			if (Action == null && ExitDirection.Length() > 0
				&& FastDrift.ShouldStart(bot.Me, ExitDirection, bot.Me.Location.FlatDist(Target)))
			{
				Action = new FastDrift(ExitDirection);
			}

			// When no subaction is set, drive normally and look for a subaction
			if (Action == null)
			{
				if (bot.Me.IsGrounded)
					timeOnGround += bot.DeltaTime;

				// Gets some other relavent surfaces for calculations
				Surface nextSurface = targetSurface;
				Surface mySurface = Field.NearestSurface(bot.Me.Location);

				// Gets some other important values
				float carSpeed = bot.Me.Velocity.Length();
				float forwardSpeed = bot.Me.Velocity.Dot(bot.Me.Forward);

				// Limits the final target to the nearest surface. Avec une direction de sortie et un
				// angle MODÉRÉ, on décale la cible en arrière le long d'ExitDirection (line-up leg) pour
				// que le dernier tronçon soit aligné. Les angles trop grands sont déjà partis en FastDrift.
				Vec3 finalTarget = ExitDirection.Length() > 0
					? LineUpTarget(bot.Me)
					: Field.LimitToNearestSurface(Target);
				// If we are on a differently orientated surface
				if (mySurface.Normal.Dot(targetSurface.Normal) < 0.95f)
				{
					// Finds the next surface we have to drive onto
					nextSurface = FindNextSurface(Field.LimitToNearestSurface(bot.Me.Location), finalTarget);
					finalTarget = nextSurface.Limit(finalTarget);
					Vec3 closestSurfacePoint = mySurface.Limit(finalTarget);
					// Adjust the final target so that the bot drives onto the surface properly
					finalTarget = closestSurfacePoint - nextSurface.Normal.FlatNorm(mySurface.Normal) * MathF.Max(closestSurfacePoint.Dist(finalTarget) - 75, 0);
				}
				if (mySurface.Key != targetSurface.Key)
				{
					// If the target might be around a corner, calculate where to aim so we don't hit a wall
					finalTarget = FindTargetAroundCorner(bot, finalTarget, nextSurface);
				}

				float turnRadius = TurnRadius(MathF.Abs(forwardSpeed));
				// Finds the point of rotation for our bot
				Vec3 nearestTurnCenter = mySurface.Limit(bot.Me.Location) + bot.Me.Right.FlatNorm(mySurface.Normal) * MathF.Sign(bot.Me.Right.Dot(finalTarget - bot.Me.Location)) * turnRadius;
				// Gets info on the landing of our car
				float landingTime = bot.Me.PredictLandingTime();

				if (Field.DistanceBetweenPoints(nearestTurnCenter, Target) > turnRadius - 40 && bot.Me.IsGrounded)
				{
					// If the target isn't within our turn radius, then just drive at our target speed
					bot.Throttle(TargetSpeed, Backwards);
				}
				else if (!bot.Me.IsGrounded)
				{
					// Protects us from errors
					TimeRemaining = float.IsNaN(TimeRemaining) ? 0.01f : TimeRemaining;
					bot.Throttle(Distance(bot.Me) / MathF.Max(TimeRemaining - landingTime, 0.01f));
				}
				else if (PreserveSpeed)
				{
					// Cible molle : on garde la vitesse et on passe large plutôt que de freiner
					// pour taper le point (c'est la branche qui casse la vitesse en replacement).
					bot.Throttle(TargetSpeed, Backwards);
				}
				else
				{
					// Otherwise, slow dowwn to turn sharper
					bot.Throttle(MathF.Max(SpeedFromTurnRadius(TurnRadius(bot.Me, Target)), 400), Backwards);
				}

				float angleToTarget;
				if (bot.Me.IsGrounded || bot.Me.Velocity.FlatLen() < 500)
				{
					// Aim at the final target assuming we shoukdn't recover
					angleToTarget = bot.AimAt(finalTarget, backwards: Backwards)[0];
				}
				else
				{
					// Otherwise, aim so we have a smooth landing
					Vec3 landingNormal = Field.FindLandingSurface(bot.Me).Normal;
					Vec3 targetDirection = Utils.Lerp(Utils.Cap(landingTime * 1.5f - 0.6f, 0, 0.75f), bot.Me.Velocity.FlatNorm(landingNormal), -Vec3.Up);
					bot.AimAt(bot.Me.Location + targetDirection, landingNormal);
					angleToTarget = bot.Me.Forward.Angle(targetDirection);
				}

				// Only boost when we are facing our target, and when we really need to
				bot.Controller.Boost = bot.Controller.Boost && (angleToTarget < 0.3f || (angleToTarget < 0.8f && !bot.Me.IsGrounded)) && !Backwards && WasteBoost;
				// Drift if the target is behind us, or when we need to turn really sharply.
				// En PreserveSpeed on ne garde que le cas « cible derrière » : le frein à main pour
				// virage serré fait perdre la vitesse qu'on cherche justement à tenir.
				bot.Controller.Handbrake = (MathF.Abs(angleToTarget) > 2 || (!PreserveSpeed && Field.DistanceBetweenPoints(nearestTurnCenter, Target) < turnRadius - 40 && SpeedFromTurnRadius(TurnRadius(bot.Me, Target)) < 400))
											&& mySurface.Normal.Dot(Vec3.Up) > 0.9f && bot.Me.Velocity.Normalize().Dot(bot.Me.Forward) > 0.9f;

				// Draws a debug line to represent the final target
				bot.Renderer.Line3D(finalTarget, finalTarget + Field.NearestSurface(finalTarget).Normal * 200, Color.LimeGreen);

				// Estimates where we'll be after dodging
				Vec3 predictedLocation = bot.Me.LocationAfterDodge();
				// Estimates how much time we have to dodge
				float timeLeft = bot.Me.Location.FlatDist(finalTarget) / MathF.Max(carSpeed + 500, 1410);
				float speedFlipTimeLeft = bot.Me.Location.FlatDist(finalTarget) / MathF.Max(carSpeed + 500 + MathF.Min(bot.Me.Boost, 40) * Car.BoostAccel / 2, 1410);

				if (AllowDodges && Field.InField(predictedLocation, 50) && carSpeed < 2000 && bot.Me.Location.z < 600 && Game.Gravity.z < -500 && MathF.Abs(bot.Me.Velocity.Dot(bot.Me.Up)) < 100)
				{
					// Look for dodges only if we won't hit a wall, and when we actually need to
					if (forwardSpeed > 0)
					{
						if (TargetSpeed > 100 + forwardSpeed)
						{
							// When we're moving forward, and need extra speed, look for dodges, speedflips, and wavedashes
							if (bot.Me.Location.z < 200 && bot.Me.IsGrounded && carSpeed > (bot.Me.Boost > 30 ? 800 : 1000) && bot.Me.Forward.FlatAngle(bot.Me.Location.Direction(finalTarget)) < 0.1f && timeOnGround > 0.02f)
							{
								// On the ground: keep speedflips for far targets, otherwise wavedash.
								// Variante BOOSTÉE si Boost>30 (paie dès v0=800), sinon sans-boost (dès v0=1000).
								// En dessous du seuil : rouler/booster tout droit gagne plus de terrain (mesuré au banc).
								Wavedash wavedash = new Wavedash(bot.Me.Location.FlatDirection(Target), bot.Me.Boost > 30);

								if (speedFlipTimeLeft > SpeedFlip.Duration && bot.Me.Boost > 0 && Field.InField(predictedLocation, 500) && WasteBoost)
								{
									// Only speedflip if we have time, and have boost
									Action = new SpeedFlip(bot.Me.Location.FlatDirection(Target));
								}
								else if (timeLeft > wavedash.Duration + 0.05f) // + récup : cible pas trop proche (Duration ~0.9s boosté / ~0.97s sans)
								{
									// Otherwise, wavedash if we have time (shorter than a dodge → needs less room)
									Action = wavedash;
								}
							}
							else if (bot.Me.Location.z > 100 && !bot.Me.HasDoubleJumped && (!bot.Me.IsGrounded || bot.Me.Velocity.Dot(Vec3.Up) < 200))
							{
								// If we are on the wall, or if we are falling and have a dodge, look for a wavedash
								Wavedash wavedash = new Wavedash(bot.Me.Location.FlatDirection(Target));

								if (timeLeft > wavedash.Duration)
								{
									// Only wavedash if we have time to
									Action = wavedash;
								}
							}
						}
					}
					else if (bot.Me.Location.z < 200 && bot.Me.IsGrounded && carSpeed > 800 && Backwards && (-bot.Me.Forward).FlatAngle(bot.Me.Location.Direction(finalTarget)) < 0.1f && timeOnGround > 0.2f)
					{
						// If we're moving backwards, and are facing the right direction, check if we should halfflip
						if (timeLeft > HalfFlip.Duration)
						{
							// Only halfflip if we have time
							Action = new HalfFlip();
						}
					}
				}
			}
			else if (Action != null && Action.Finished)
			{
				// If our subaction has finished, reset it to null, and reset some other values
				Action = null;
				Backwards = false;
				timeOnGround = 0;
			}
			else if (Action != null)
			{
				// If we currently have a subaction, run it
				Action.Run(bot);
				if (Action is SpeedFlip)
				{
					// If it's a speedflip, add a little extra speed
					bot.Throttle(TargetSpeed + 500, Backwards);
				}
			}

			// Draws a debug line to represent the target
			bot.Renderer.Line3D(Field.LimitToNearestSurface(Target), Field.LimitToNearestSurface(Target) + targetSurface.Normal * 200, Color.LimeGreen);
			
			// Prevents this action from being interrupted during a dodge
			Interruptible = Action == null || Action.Interruptible;

			if (Field.LimitToNearestSurface(bot.Me.Location).Dist(Field.LimitToNearestSurface(Target)) < 100)
			{
				// If we have arrived at our destination, finish this action
				Finished = true;
			}
		}

		/// <summary>
		/// Décale la cible en arrière le long de <see cref="ExitDirection"/> pour aborder le point aligné
		/// sur cette direction — le « line-up leg » d'Arrive, sans la contrainte de temps (placement pur).
		/// Le décalage croît avec la vitesse mais est plafonné par le rayon de virage, et clampé pour ne
		/// jamais passer de l'autre côté de la cible. Renvoie le point (limité à la surface la plus proche).
		/// </summary>
		private Vec3 LineUpTarget(Car car)
		{
			float carSpeed = car.Velocity.Length();
			Vec3 surfaceNormal = Field.NearestSurface(Target).Normal;
			Vec3 directionToTarget = car.Location.FlatDirection(Target, surfaceNormal);

			// Longueur du tronçon d'alignement : borné par une fraction du trajet et par une durée,
			// puis atténué quand il dépasse le rayon de virage (on ne peut pas s'aligner plus vite que ça).
			float shift = MathF.Min(Field.DistanceBetweenPoints(Target, car.Location) * ApproachLineUpFraction,
				Utils.Cap(carSpeed, Car.MaxThrottleSpeed, Car.MaxSpeed) * ApproachLineUpSeconds);
			float turnRadius = TurnRadius(Utils.Cap(carSpeed, 500, Car.MaxSpeed)) * 1.2f;
			shift *= Utils.Cap(shift / turnRadius, 0f, 1f);

			// Clampe le décalage pour qu'il ne bascule pas de l'autre côté de la cible par rapport à nous.
			Vec3 leftDirection = directionToTarget.Cross(surfaceNormal).Normalize();
			Vec3 rightDirection = directionToTarget.Cross(-surfaceNormal).Normalize();
			return Field.LimitToNearestSurface(Target - ExitDirection.Clamp(leftDirection, rightDirection, surfaceNormal).Normalize() * shift);
		}

		/// <summary>Finds the distance left to drive</summary>
		public float Distance(Car car)
		{
			return GetDistance(car, Target, Backwards);
		}

		/// <summary>Estimates the time left before we arrive, assuming we drive as fast as possible</summary>
		public float Eta(Car car)
		{
			return GetEta(car, Target, Backwards, AllowDodges);
		}

		/// <summary>Finds the next driving surface between a start point and a target point</summary>
		private static Surface FindNextSurface(Vec3 start, Vec3 target)
		{
			// Gets a point between the start and target points, then limits it to a surface
			Vec3 middle = Field.LimitToNearestSurface((start + target) / 2);

			// Chooses points along the line between the start and middle points, and then between the middle and target points
			for (float f = 0; f < 2; f += 0.25f)
			{
				// Gets the next position, then limits it to a surface
				Vec3 nextPos = Field.LimitToNearestSurface(start + (middle - start) * Utils.Cap(f, 0, 1) + (target - middle) * Utils.Cap(f - 1, 0, 1));
				if (Field.NearestSurface(nextPos).Normal.Dot(Field.NearestSurface(start).Normal) < 0.95f)
				{
					// return the first surface found that differs from the start surface
					return Field.NearestSurface(nextPos);
				}
			}

			// Otherwise, just give back the target's surface
			return Field.NearestSurface(target);
		}

		/// <summary>Finds target positions so that corners are navigated around nicely</summary>
		private static Vec3 FindTargetAroundCorner(RUBot bot, Vec3 finalTarget, Surface nextSurface)
		{
			Surface mySurface = Field.NearestSurface(bot.Me.Location);

			if (mySurface.Key == "Ground")
			{
				// If we are on the ground, we need to make sure not to hit the post on accident
				Goal goal = Field.Side(bot.Team) == MathF.Sign(finalTarget.y) ? bot.OurGoal : bot.TheirGoal;

				Vec3 enterLeftDirection = bot.Me.Location.Direction(goal.LeftPost - new Vec3(MathF.Sign(goal.LeftPost.x) * 100, MathF.Sign(goal.LeftPost.y) * 50));
				Vec3 enterRightDirection = bot.Me.Location.Direction(goal.RightPost - new Vec3(MathF.Sign(goal.RightPost.x) * 100, MathF.Sign(goal.RightPost.y) * 50));
				Vec3 exitLeftDirection = bot.Me.Location.Direction(goal.LeftPost + new Vec3(MathF.Sign(goal.LeftPost.x) * 60, -MathF.Sign(goal.LeftPost.y) * 50));
				Vec3 exitRightDirection = bot.Me.Location.Direction(goal.RightPost + new Vec3(MathF.Sign(goal.RightPost.x) * 60, -MathF.Sign(goal.RightPost.y) * 50));

				// Clamps the target direction so if the target is in the goal, we enter the goal without hitting the post
				// and if it's not in the goal we make sure we avoid the goal, and the posts
				Vec3 targetDirection = nextSurface.Key.Contains("Goal Ground") ?
									   bot.Me.Location.Direction(finalTarget).Clamp(enterLeftDirection, enterRightDirection, mySurface.Normal) :
									   bot.Me.Location.Direction(finalTarget).Clamp(exitRightDirection, exitLeftDirection, mySurface.Normal);

				// Return the adjusted target
				return bot.Me.Location + targetDirection.Rescale(bot.Me.Location.Dist(finalTarget));
			}
			else if (mySurface.Key.Contains("Goal Ground"))
			{
				// If we are in a goal, we gotta make sure not to hit the posts on our way out
				Goal goal = Field.Side(bot.Team) == MathF.Sign(bot.Me.Location.y) ? bot.OurGoal : bot.TheirGoal;

				Vec3 leftDirection = bot.Me.Location.Direction(goal.LeftPost - new Vec3(MathF.Sign(goal.LeftPost.x) * 100, MathF.Sign(goal.LeftPost.y) * 50));
				Vec3 rightDirection = bot.Me.Location.Direction(goal.RightPost - new Vec3(MathF.Sign(goal.RightPost.x) * 100, MathF.Sign(goal.RightPost.y) * 50));

				// Clamps the target direction between the goal posts, so we don't hit them
				Vec3 targetDirection = bot.Me.Location.Direction(finalTarget).Clamp(rightDirection, leftDirection, mySurface.Normal);

				// Return the adjusted target
				return bot.Me.Location + targetDirection.Rescale(bot.Me.Location.Dist(finalTarget));
			}
			else if (mySurface.Key.Contains("Backboard") || mySurface.Key.Contains("Backwall"))
			{
				// If we are on the backboard, or the backwall, we gotta make sure not to accidentally fall into the goal
				Goal goal = Field.Side(bot.Team) == MathF.Sign(bot.Me.Location.y) ? bot.OurGoal : bot.TheirGoal;

				Vec3 leftDirection;
				Vec3 rightDirection;
				// Depending on which surface we are on, we choose different left and right direction to clamp between
				if (mySurface.Key.Contains("Left Backwall"))
				{
					leftDirection = bot.Me.Location.Direction(goal.TopRightCorner + new Vec3(MathF.Sign(goal.TopRightCorner.x) * 50, 0, 50));
					rightDirection = bot.Me.Location.Direction(goal.BottomRightCorner + new Vec3(MathF.Sign(goal.BottomRightCorner.x) * 50, 0, -50));
				}
				else if (mySurface.Key.Contains("Right Backwall"))
				{
					leftDirection = bot.Me.Location.Direction(goal.BottomLeftCorner + new Vec3(MathF.Sign(goal.BottomLeftCorner.x) * 50, 0, -50));
					rightDirection = bot.Me.Location.Direction(goal.TopLeftCorner + new Vec3(MathF.Sign(goal.TopLeftCorner.x) * 50, 0, 50));
				}
				else
				{
					leftDirection = bot.Me.Location.Direction(goal.TopLeftCorner + new Vec3(MathF.Sign(goal.TopLeftCorner.x) * 50, 0, 50));
					rightDirection = bot.Me.Location.Direction(goal.TopRightCorner + new Vec3(MathF.Sign(goal.TopRightCorner.x) * 50, 0, 50));
				}

				// Clamps the target direction between the directions chosen earlier
				Vec3 targetDirection = bot.Me.Location.Direction(finalTarget).Clamp(leftDirection, rightDirection, mySurface.Normal);

				// Return the adjusted target
				return bot.Me.Location + targetDirection.Rescale(bot.Me.Location.Dist(finalTarget));
			}

			// If none of those apply, just return the target
			return finalTarget;
		}

		/// <summary>Finds the distance the car will travel in order to get to the given target</summary>
		public static float GetDistance(Car car, Vec3 target)
		{
			float forwardsEta = GetEta(car, target, false, false);
			float backwardsEta = GetEta(car, target, true, false);

			bool backwards = backwardsEta + 0.5f < forwardsEta && car.Forward.Dot(car.Velocity) < 500 && car.Forward.FlatAngle(car.Location.Direction(target), car.Up) > MathF.PI * 0.6f;

			return GetDistance(car, target, backwards);
		}

		/// <summary>Finds the distance the car will travel in order to get to the given target</summary>
		/// <param name="backwards">Whether or not we are planning to drive backwards</param>
		public static float GetDistance(Car car, Vec3 target, bool backwards)
		{
			return GetDistance(car, target, backwards, out _, out _);
		}

		/// <summary>Finds the distance the car will travel in order to get to the given target</summary>
		/// <param name="backwards">Whether or not we are planning to drive backwards</param>
		/// <param name="angle",>Gives us the approximate angle of our turn in order to face the target</param>
		/// <param name="radius">Gives us the approximate radius of our turn in order to face the target</param>
		public static float GetDistance(Car car, Vec3 target, bool backwards, out float angle, out float radius)
		{
			// Puts our target on the nearest surface
			target = Field.LimitToNearestSurface(target);
			// Gets the position of the car when it starts driving
			Vec3 carPos = car.PredictLandingPosition();
			// Gets the nearest surface to the car when it starts driving
			Surface carSurface = Field.FindLandingSurface(car);
			Vec3 surfaceNormal = carSurface.Normal;
			// Calculates the forward and right direction for the car when it start driving
			Vec3 carForward = car.IsGrounded ? car.Forward : (car.Velocity.FlatLen() > 500 ? car.Velocity.FlatNorm(surfaceNormal) : car.Location.FlatDirection(target, surfaceNormal));
			Vec3 carRight = carForward.Cross(car.IsGrounded ? -car.Up : -surfaceNormal).Normalize();

			// Grabs the current speed of the car, as well as an estimate of the angle of the next turn
			float currentSpeed = car.Velocity.Dot(carForward);
			angle = (backwards ? -carForward : carForward).FlatAngle(target - carPos, surfaceNormal);
			// Using those values, we estimate the average turn speed of the car, and use that to calculate the average turn radius
			float turnSpeed = backwards ? SpeedAfterTurn(-currentSpeed, angle, 0.4f) : SpeedAfterTurn(currentSpeed, angle, 0.5f);
			radius = TurnRadius(turnSpeed);

			// Finds the point of rotation for our car
			Vec3 nearestTurnCenter = carPos + carRight * MathF.Sign(carRight.Dot(target - carPos)) * radius;
			Vec3 limitedTurnCenter = carSurface.Limit(nearestTurnCenter);
			// If the calculated point of rotation is outside the map, that means the point of rotation is on a different surface from the car
			if (nearestTurnCenter.Dist(limitedTurnCenter) > 1)
			{
				// Calculates the direction up along the wall, towards the actual point of rotation
				Vec3 normal = limitedTurnCenter.Direction(Field.LimitToNearestSurface(nearestTurnCenter + carSurface.Normal * 500));
				// Estimates the actual point of rotaation
				nearestTurnCenter = limitedTurnCenter + normal * nearestTurnCenter.Dist(limitedTurnCenter);
			}

			// Gets the distance between the point of rotation and the target
			float distance = Field.DistanceBetweenPoints(nearestTurnCenter, target);

			if (distance < radius)
			{
				// If we are too closse to the target, adjust our turn radius and point of rotation
				radius = TurnRadius(car, target);
				nearestTurnCenter = carPos + carRight * MathF.Sign(carRight.Dot(target - carPos)) * radius;

				distance = Field.DistanceBetweenPoints(nearestTurnCenter, target);
			}

			// Does some fancy math things that calculates the actual turn angle
			angle = MathF.Abs((carPos - nearestTurnCenter).FlatAngle(target - nearestTurnCenter, surfaceNormal) - ((target - carPos).Dot(backwards ? -carForward : carForward) < 0 ? 2 * MathF.PI : 0));
			angle -= MathF.Acos(Utils.Cap(radius / distance, 0, 1));
			angle = Utils.Cap(angle, 0, 2 * MathF.PI);

			// Returns an estimate of the actual distance needed to drive, including the turn
			return MathF.Sqrt(MathF.Max(MathF.Pow(distance, 2) - MathF.Pow(radius, 2), 0)) + radius * angle;
		}

		/// <summary>Estimates how long it should take do drive to a given target, accelerating from the car's current speed (see TimeToCoverDistance)</summary>
		public static float GetEta(Car car, Vec3 target)
		{
			float forwardsEta = GetEta(car, target, false, false);
			float backwardsEta = GetEta(car, target, true, false);

			// Only go backwards under very specific circumstances, because otherwise the bot goes backwards far too often
			bool backwards = backwardsEta + 0.5f < forwardsEta && car.Forward.Dot(car.Velocity) < 500 && car.Forward.FlatAngle(car.Location.Direction(target), car.Up) > MathF.PI * 0.6f;

			return GetEta(car, target, backwards, true);
		}

		/// <summary>Estimates how long it should take do drive to a given target, accelerating from the car's current speed (see TimeToCoverDistance)</summary>
		/// <param name="allowDodges">Currently unused by the estimate: crediting a dodge here was measurably wrong (see the note in the forwards branch). Dodge modelling lives in Bot/Movement.cs. Kept so callers keep compiling and to record the intent.</param>
		public static float GetEta(Car car, Vec3 target, bool allowDodges)
		{
			float forwardsEta = GetEta(car, target, false, false);
			float backwardsEta = GetEta(car, target, true, false);

			// Only go backwards under very specific circumstances, because otherwise the bot goes backwards far too often
			bool backwards = backwardsEta + 0.5f < forwardsEta && car.Forward.Dot(car.Velocity) < 500 && car.Forward.FlatAngle(car.Location.Direction(target), car.Up) > MathF.PI * 0.6f;

			return GetEta(car, target, backwards, allowDodges);
		}

		/// <summary>Estimates how long it should take do drive to a given target, accelerating from the car's current speed (see TimeToCoverDistance)</summary>
		/// <param name="allowDodges">Currently unused by the estimate: crediting a dodge here was measurably wrong (see the note in the forwards branch). Dodge modelling lives in Bot/Movement.cs. Kept so callers keep compiling and to record the intent.</param>
		/// <param name="backwards">Whether or not we are planning to drive backwards</param>
		public static float GetEta(Car car, Vec3 target, bool backwards, bool allowDodges)
		{
			return GetEta(car, target, backwards, allowDodges, 0f);
		}

		/// <summary>
		/// ETA to a target we have to reach while already travelling along <paramref name="arrivalDirection"/>
		/// — which is what a shot needs: not just touching the ball, but hitting it the right way.
		///
		/// <para>The plain overloads measure the shortest path to the point and stop there. Arrive,
		/// which is what actually drives the car, does something quite different: it aims at a point
		/// backed off along the arrival direction so the car lines up, then runs the last stretch
		/// straight (see Arrive.Run). That approach leg can add a large fraction of the trip when the
		/// car starts off to the side of the shot line — distance the plain ETA never counts, so the
		/// bot commits to shots it cannot make and ends up turning onto the ball's path too late.</para>
		/// </summary>
		/// <param name="arrivalDirection">Direction the car should be travelling on arrival. Zero means no constraint.</param>
		public static float GetEta(Car car, Vec3 target, Vec3 arrivalDirection, bool allowDodges = true)
		{
			if (arrivalDirection.Length() < 1e-4f)
				return GetEta(car, target, allowDodges);

			// Mirrors the line-up distance Arrive uses, so the estimate matches the path actually driven
			float directDistance = Field.DistanceBetweenPoints(car.Location, target);
			float lineUp = MathF.Min(directDistance * ApproachLineUpFraction,
				Utils.Cap(car.Velocity.Length(), Car.MaxThrottleSpeed, Car.MaxSpeed) * ApproachLineUpSeconds);

			Vec3 approachPoint = Field.LimitToNearestSurface(target - arrivalDirection.Normalize() * lineUp);

			// Turn onto the approach point, then cover the final line-up stretch in a straight line
			return GetEta(car, approachPoint, false, allowDodges, lineUp);
		}

		/// <param name="extraStraightDistance">Extra ground covered in a straight line at the end of the drive</param>
		private static float GetEta(Car car, Vec3 target, bool backwards, bool allowDodges, float extraStraightDistance)
		{
			// Gets the distance to drive to the given target, as well and the angle, and radius of the turn we have to make to face the target
			float distance = GetDistance(car, target, backwards, out float angle, out float radius);
			// Seperates the distance from the turn distance
			float turnDistance = angle * radius;
			distance -= turnDistance;
			distance += extraStraightDistance;

			// Gets the normal of the nearest surface to the car when it starts driving
			Vec3 surfaceNormal = car.IsGrounded ? Field.NearestSurface(car.Location).Normal : Field.FindLandingSurface(car).Normal;
			// Calculates the car's forward direction when it starts driving, and it's velocity in that direction
			Vec3 carForward = car.IsGrounded ? car.Forward : (car.Velocity.FlatLen() > 500 ? car.Velocity.FlatNorm(surfaceNormal) : car.Location.FlatDirection(target, surfaceNormal));
			float currentSpeed = carForward.Dot(car.Velocity);
			float landingTime = car.PredictLandingTime();

			if (backwards)
			{
				// Speed coming out of the turn — no 1400 floor, same reasoning as the forwards case.
				// Boost is not modelled here: it does nothing while reversing.
				float exitSpeed = Utils.Cap(SpeedAfterTurn(-currentSpeed, angle, 0.8f), 0, Car.MaxThrottleSpeed);
				return landingTime + turnDistance / MathF.Max(SpeedFromTurnRadius(radius), 400)
					+ TimeToCoverDistance(exitSpeed, 0f, distance);
			}
			else
			{
				// Speed coming out of the turn. No floor at 1400 here: the old code forced this to
				// MaxThrottleSpeed, which pretended a car at a standstill was already at full throttle
				// speed and made this estimate far too optimistic when starting slow.
				float exitSpeed = Utils.Cap(SpeedAfterTurn(currentSpeed, angle), 0, Car.MaxSpeed);
				float turnTime = turnDistance / MathF.Max(SpeedFromTurnRadius(radius), 400);

				// Integrate the real acceleration curve over the straight part.
				//
				// No dodge discount here. Crediting one unconditionally produced estimates that
				// physics forbids: measured case E11 (2463 uu from 2291 uu/s, no boost) came out at
				// 0.966s when the hard floor at the 2300 speed cap is 2463/2300 = 1.071s. A dodge
				// buys nothing at the speed cap and costs recovery time everywhere else. Dropping it
				// brought straight-line error from ~+15% down to +2/+3% across the whole bench
				// (see state_setting_tests_eta.py). Modelling when a dodge actually pays off belongs
				// in Bot/Movement.cs, which reproduces what Drive really does rather than an optimum.
				float driveTime = TimeToCoverDistance(exitSpeed, car.Boost, distance);

				return landingTime + turnTime + driveTime;
			}
		}

		/// <summary>Estimates the maximum possible turn radius in order to still hit the target</summary>
		public static float TurnRadius(Car car, Vec3 target)
		{
			float distance = Field.DistanceBetweenPoints(car.Location, target);
			return (distance / 2) / (car.Right * MathF.Sign(car.Right.Dot(target - car.Location))).Dot(car.Location.FlatDirection(target, car.Up));
		}

		/// <summary>
		/// Ground acceleration from holding throttle, at a given forward speed.
		/// Falls off linearly from ThrottleAccelZero at a standstill to ThrottleAccelMax at
		/// ThrottleAccelKnee, then drops to 0 at MaxThrottleSpeed — throttle alone cannot push
		/// the car past MaxThrottleSpeed, only boost can.
		/// </summary>
		public static float ThrottleAccel(float speed)
		{
			speed = MathF.Abs(speed);
			if (speed >= Car.MaxThrottleSpeed)
				return 0f;
			if (speed >= Car.ThrottleAccelKnee)
				return Utils.Lerp((speed - Car.ThrottleAccelKnee) / (Car.MaxThrottleSpeed - Car.ThrottleAccelKnee), Car.ThrottleAccelMax, 0f);
			return Utils.Lerp(speed / Car.ThrottleAccelKnee, Car.ThrottleAccelZero, Car.ThrottleAccelMax);
		}

		/// <summary>
		/// Estimates how long it takes to cover a distance in a straight line, starting from a given
		/// speed, holding throttle and boosting while boost lasts.
		///
		/// <para>This replaces the old assumption that the car is always already at MaxThrottleSpeed
		/// (a `MathF.Max(..., 1400)` floor). That floor treated a car at a standstill as if it were
		/// already doing 1400 uu/s, which made GetEta wildly optimistic when starting slow — the bot
		/// would commit to interceptions that were physically out of reach and get outrun.</para>
		///
		/// <para>Integrated in fixed steps rather than solved analytically: acceleration is piecewise
		/// in speed AND changes when boost runs out, so closed form would be several cases for no
		/// real gain. Steps are cheap — GetEta is called on many ball slices per tick, but this is
		/// a handful of floating point ops each.</para>
		/// </summary>
		/// <param name="startSpeed">Forward speed at the start. Negative values are treated as 0.</param>
		/// <param name="boostAmount">Boost available, 0-100. Pass 0 to estimate without boosting.</param>
		/// <param name="distance">Distance to cover</param>
		public static float TimeToCoverDistance(float startSpeed, float boostAmount, float distance)
		{
			if (distance <= 0f)
				return 0f;

			const float step = 1f / 60f;
			const float maxTime = 10f;

			float speed = Utils.Cap(startSpeed, 0f, Car.MaxSpeed);
			float boost = MathF.Max(boostAmount, 0f);
			float travelled = 0f;
			float time = 0f;

			while (travelled < distance && time < maxTime)
			{
				bool boosting = boost > 0f;
				float accel = ThrottleAccel(speed) + (boosting ? Car.BoostAccel : 0f);
				float newSpeed = Utils.Cap(speed + accel * step, 0f, Car.MaxSpeed);

				// Average of the two speeds over the step — trapezoidal, keeps the error small
				// enough at 60Hz that a finer step doesn't change the answer meaningfully.
				float advanced = (speed + newSpeed) / 2f * step;

				if (travelled + advanced >= distance)
				{
					// Interpolate within this step instead of overshooting by up to 1/60s
					float remaining = distance - travelled;
					return time + (advanced > 1e-6f ? step * (remaining / advanced) : 0f);
				}

				travelled += advanced;
				speed = newSpeed;
				if (boosting)
					boost = MathF.Max(boost - Car.BoostConsumption * step, 0f);
				time += step;
			}

			return time;
		}

		/// <summary>Returns the turn radius of the car at a given speed</summary>
		public static float TurnRadius(float speed)
		{
			speed = Utils.Cap(speed, 0.01f, Car.MaxSpeed);
			if (speed <= 500)
				return Utils.Lerp(speed / 500, 145, 251);
			if (speed <= 1000)
				return Utils.Lerp((speed - 500) / 500, 251, 425);
			if (speed <= 1500)
				return Utils.Lerp((speed - 1000) / 500, 425, 727);
			if (speed <= 1750)
				return Utils.Lerp((speed - 1500) / 250, 727, 909);
			return Utils.Lerp((speed - 1750) / 550, 909, 1136);
		}

		/// <summary>Returns the speed of the car given a turn radius</summary>
		public static float SpeedFromTurnRadius(float radius)
		{
			radius = Utils.Cap(radius, 145, 1136);
			if (radius <= 251)
				return Utils.Lerp((radius - 145) / 106, 0, 500);
			if (radius <= 425)
				return Utils.Lerp((radius - 251) / 174, 500, 1000);
			if (radius <= 727)
				return Utils.Lerp((radius - 425) / 302, 1000, 1500);
			if (radius <= 909)
				return Utils.Lerp((radius - 727) / 182, 1500, 1750);
			return Utils.Lerp((radius - 909) / 227, 1750, Car.MaxSpeed);
		}

		/// <summary>Estimates the speed of the car after a turn, given the current speed of the car and the angle of the turn</summary>
		public static float SpeedAfterTurn(float currentSpeed, float angle, float modifier = 1)
		{
			return Utils.Cap((1234 * (MathF.Exp(angle * 0.49f * modifier) * angle * 0.49f * modifier * (currentSpeed > 1234 ? 0.2f : 1)) + currentSpeed) / ((MathF.Exp(angle * 0.49f * modifier) * angle * 0.49f * modifier * (currentSpeed > 1234 ? 0.2f : 1)) + 1), 0, Car.MaxSpeed);
		}
	}
}
