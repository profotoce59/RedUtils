using System;
using RedUtils.Math;

namespace RedUtils.Interop
{
	/// <summary>
	/// Enveloppe gérée autour d'une arène RocketSim : une voiture, une balle, et de quoi rejouer
	/// un scénario court autant de fois qu'on veut.
	///
	/// <para><b>Réutilisée, jamais clonée.</b> La façade expose <c>ArenaClone</c>, mais cloner
	/// reconstruit tout le monde Bullet à chaque candidat — coûteux et inutile ici : poser
	/// l'état de la voiture et de la balle suffit à repartir d'une situation propre. Le compteur
	/// de ticks et les pads de boost ne sont pas remis à zéro, ce qui est sans effet sur une
	/// simulation d'une demi-seconde.</para>
	///
	/// <para><b>Un seul thread à la fois.</b> Une instance n'est pas protégée : c'est le
	/// propriétaire (la recherche, sur son thread de fond) qui garantit l'exclusivité.</para>
	/// </summary>
	public sealed class RocketSimArena : IDisposable
	{
		private IntPtr _arena;
		private readonly uint _carId;

		/// <summary>Vrai si l'arène est utilisable.</summary>
		public bool Valid => _arena != IntPtr.Zero && _carId != 0;

		/// <summary>
		/// Crée l'arène. Initialise RocketSim au passage si ce n'est pas déjà fait — sans les
		/// meshes de collision, voir <see cref="RocketSimNative.InitPlanesOnly"/> et AUDIT §7.6.
		/// </summary>
		/// <param name="team">Équipe de la voiture simulée. Sans effet sur la physique, mais
		/// garde les repères cohérents avec le jeu.</param>
		public RocketSimArena(int team)
		{
			if (RocketSimNative.IsInitialized() == 0 && RocketSimNative.InitPlanesOnly() != 0)
				return;

			_arena = RocketSimNative.ArenaCreate(120f);
			if (_arena == IntPtr.Zero)
				return;

			_carId = RocketSimNative.ArenaAddCar(_arena, team);
		}

		/// <summary>Pose l'état de la voiture et de la balle : le point de départ d'un candidat.</summary>
		public void Seed(in RocketSimNative.RSCarState car, in RocketSimNative.RSBallState ball)
		{
			RocketSimNative.CarSetState(_arena, _carId, in car);
			RocketSimNative.BallSetState(_arena, in ball);
		}

		/// <summary>Applique des inputs et avance d'un tick.</summary>
		public void Step(in RocketSimNative.RSCarControls controls)
		{
			RocketSimNative.CarSetControls(_arena, _carId, in controls);
			RocketSimNative.ArenaStep(_arena, 1);
		}

		public RocketSimNative.RSCarState GetCar()
		{
			RocketSimNative.CarGetState(_arena, _carId, out var s);
			return s;
		}

		public RocketSimNative.RSBallState GetBall()
		{
			RocketSimNative.BallGetState(_arena, out var s);
			return s;
		}

		public void Dispose()
		{
			if (_arena != IntPtr.Zero)
			{
				RocketSimNative.ArenaDestroy(_arena);
				_arena = IntPtr.Zero;
			}
			GC.SuppressFinalize(this);
		}

		~RocketSimArena() => Dispose();

		// ---------------------------------------------------------------- conversions

		public static RocketSimNative.RSVec3 ToRS(Vec3 v) => new RocketSimNative.RSVec3(v.x, v.y, v.z);
		public static Vec3 ToRU(RocketSimNative.RSVec3 v) => new Vec3(v.X, v.Y, v.Z);

		/// <summary>
		/// Convertit une voiture du jeu en état de simulation.
		///
		/// <para><b>À n'utiliser QUE si <see cref="CanSeedExactly"/> est vrai.</b> Le paquet RLBot
		/// ne contient pas <c>jumpTime</c>, <c>flipTime</c>, <c>isJumping</c>, <c>isFlipping</c>
		/// ni <c>airTimeSinceJump</c> — or ce sont eux qui décident du déclenchement d'un flip.
		/// Ils sont laissés à zéro ici, ce qui n'est exact que pour une voiture au sol dont le saut
		/// et le flip sont encore disponibles. Dans tout autre cas, la simulation décrirait une
		/// voiture différente de la vraie et personne ne le signalerait. Voir AUDIT §7.2.</para>
		/// </summary>
		public static RocketSimNative.RSCarState FromCar(Car car)
		{
			return new RocketSimNative.RSCarState
			{
				Pos = ToRS(car.Location),
				Vel = ToRS(car.Velocity),
				AngVel = ToRS(car.AngularVelocity),
				Forward = ToRS(car.Forward),
				Right = ToRS(car.Right),
				Up = ToRS(car.Up),

				IsOnGround = car.IsGrounded ? 1 : 0,
				HasJumped = car.HasJumped ? 1 : 0,
				HasDoubleJumped = car.HasDoubleJumped ? 1 : 0,
				HasFlipped = 0,
				IsJumping = 0,
				IsFlipping = 0,
				JumpTime = 0f,
				FlipTime = 0f,
				AirTimeSinceJump = 0f,
				FlipRelTorque = new RocketSimNative.RSVec3(0f, 0f, 0f),

				Boost = car.Boost,
				IsBoosting = 0,
				BoostingTime = 0f,
				TimeSinceBoosted = 1f,
				SupersonicTime = 0f,

				HandbrakeVal = 0f,
				IsDemoed = car.IsDemolished ? 1 : 0,
				DemoRespawnTimer = 0f,
			};
		}

		/// <summary>
		/// Vrai si l'état de cette voiture est <b>intégralement</b> reconstructible depuis le
		/// paquet RLBot — c'est-à-dire au sol, saut et flip encore disponibles. Tous les compteurs
		/// manquants valent alors zéro, ce qui est leur vraie valeur.
		///
		/// <para>C'est la précondition de toute simulation fidèle tant qu'un suivi tick par tick
		/// de ces compteurs n'existe pas (AUDIT §7.2).</para>
		/// </summary>
		public static bool CanSeedExactly(Car car)
			=> car.IsGrounded && !car.HasJumped && !car.HasDoubleJumped && !car.IsDemolished;

		public static RocketSimNative.RSBallState BallNow()
		{
			return new RocketSimNative.RSBallState
			{
				Pos = ToRS(Ball.Location),
				Vel = ToRS(Ball.Velocity),
				AngVel = ToRS(Ball.AngularVelocity),
			};
		}
	}
}
