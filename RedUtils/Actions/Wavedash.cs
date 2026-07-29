using System;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>A wavedash action (algo d'origine : vise l'atterrissage puis dodge près du sol),
	/// avec un saut initial sur UN SEUL tick.</summary>
	public class Wavedash : IAction
	{
		/// <summary>Whether or not this action has finished</summary>
		public bool Finished { get; private set; }
		/// <summary>Wavedashes aren't interruptible, so this will always be false</summary>
		public bool Interruptible { get; private set; }

		/// <summary>The direction we plan to wavedash in</summary>
		public Vec3 Direction;
		/// <summary>Le saut initial ne dure qu'UN tick ; JumpTime ne sert plus qu'à estimer Duration.</summary>

		public float Duration { get { return 1f; } }

		/// <summary>Whether we still need to fire the (single-tick) initial jump</summary>
		private bool _jumping = true;
		/// <summary>Whether we have actually left the ground since starting</summary>
		private bool _leftGround = false;
		/// <summary>When we started this action</summary>
		private float _startTime = -1;
		/// <summary>The inputs for the dodge direction</summary>
		private Vec3 _input = Vec3.Zero;

		/// <summary>Initialize a new wavedash action</summary>
		/// <param name="direction">The direction which we will attempt to dash in.
		/// If null, we will dash in the direction we are already going.</param>

		public Wavedash(Vec3? direction = null)
		{
			Interruptible = false;
			Finished = false;

			Direction = direction ?? Vec3.Zero;
		}

		/// <summary>Runs this wavedash action</summary>
		public void Run(RUBot bot)
		{
			// If this action hasn't started yet
			if (_startTime == -1)
			{
				// Set the start time, and whether or not we should jump (only when starting grounded)
				_startTime = Game.Time;
				_jumping = bot.Me.IsGrounded;
			}

			// Remember once we have actually left the ground
			if (!bot.Me.IsGrounded)
				_leftGround = true;

			if (_jumping)
			{
				// Initial jump on a SINGLE tick (minimal jump), then we never re-jump here
				bot.Controller.Jump = true;
				_jumping = false;
			}
			else if (!bot.Me.IsGrounded && bot.Me.Location.z < 40 && bot.Me.Velocity.z < -100)
			{
				// If we are about to hit the ground, dodge!
				if (_input.Length() == 0)
				{
					// If the input hasn't been set, set the input according to the given direction. If no direction is given, just dodge forward
					_input = Direction.Length() > 0 ?
							new Vec3(bot.Me.Local(Direction)[1], -bot.Me.Local(Direction)[0]) :
							new Vec3(bot.Me.Local(bot.Me.Velocity).Normalize()[1], -bot.Me.Local(bot.Me.Velocity).Normalize()[0]);
				}

				// Dodges using the input set earlier
				bot.Controller.Yaw = _input[0];
				bot.Controller.Pitch = _input[1];
				bot.Controller.Jump = true;
			}
			else if (!bot.Me.IsGrounded)
			{
				// Aim slightly above the ground, in the direction given
				Vec3 landingNormal = Field.FindLandingSurface(bot.Me).Normal;
				bot.AimAt(bot.Me.Location + (Direction.Length() > 0 ? Direction.FlatNorm(landingNormal) : bot.Me.Velocity.FlatNorm(landingNormal)) + landingNormal * 0.2f, landingNormal);
			}
			else if (_leftGround)
			{
				// On ne termine qu'une fois REVENU au sol après avoir décollé. Sans cette garde, avec un
				// saut d'un seul tick on finirait dès le tick suivant (la voiture est encore au sol le
				// temps de décoller).
				Finished = true;
			}
		}
	}
}
