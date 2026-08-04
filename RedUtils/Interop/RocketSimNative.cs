using System;
using System.Runtime.InteropServices;

namespace RedUtils.Interop
{
	/// <summary>
	/// Liaison P/Invoke vers <c>RocketSimC</c> — la façade C plate au-dessus de RocketSim
	/// (<c>native/RocketSimC/</c>).
	///
	/// <para><b>Ne jamais recopier les structures C++ de RocketSim ici.</b> <c>RocketSim::Vec</c>
	/// est aligné sur 16 octets avec un 4e float caché (<c>_w</c>, pour le SIMD) : sa taille est
	/// 16 et non 12, et <c>RotMat</c> fait 48 octets. Une structure C# calquée sur <c>CarState</c>
	/// avec des vecteurs de 3 floats serait décalée dès le premier champ, sans la moindre erreur
	/// à la compilation ni à l'exécution — juste des résultats faux. Les structures ci-dessous
	/// correspondent à celles de <c>rocketsim_c.h</c>, dont on contrôle les deux côtés.</para>
	///
	/// <para>Voir AUDIT §7 pour l'architecture d'ensemble et ses trois obstacles (budget de tick,
	/// fidélité de l'état de départ, binding).</para>
	/// </summary>
	public static class RocketSimNative
	{
		private const string Lib = "RocketSimC";

		[StructLayout(LayoutKind.Sequential)]
		public struct RSVec3
		{
			public float X, Y, Z;
			public RSVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
		}

		/// <summary>
		/// État complet d'une voiture pour la simulation.
		///
		/// <para><b>Attention</b> : <c>JumpTime</c>, <c>FlipTime</c>, <c>IsJumping</c>,
		/// <c>IsFlipping</c> et <c>AirTimeSinceJump</c> ne figurent PAS dans le paquet RLBot. Ce
		/// sont pourtant eux qui décident du déclenchement d'un flip. Il faut les suivre tick par
		/// tick côté bot ; les laisser à zéro donne une simulation qui ne décrit pas la voiture
		/// réelle. AUDIT §7.2.</para>
		/// </summary>
		[StructLayout(LayoutKind.Sequential)]
		public struct RSCarState
		{
			public RSVec3 Pos, Vel, AngVel;
			public RSVec3 Forward, Right, Up;

			public int IsOnGround;
			public int HasJumped, HasDoubleJumped, HasFlipped;
			public int IsJumping, IsFlipping;
			public float JumpTime, FlipTime, AirTimeSinceJump;
			public RSVec3 FlipRelTorque;

			public float Boost;
			public int IsBoosting;
			public float BoostingTime, TimeSinceBoosted, SupersonicTime;

			public float HandbrakeVal;
			public int IsDemoed;
			public float DemoRespawnTimer;

			/// <summary>Lecture seule — renseigné par <see cref="CarGetState"/>, ignoré en écriture.</summary>
			public int BallHitValid;
			/// <summary>Tick auquel le contact avec la balle a eu lieu. Lecture seule.</summary>
			public ulong BallHitTick;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct RSBallState
		{
			public RSVec3 Pos, Vel, AngVel;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct RSCarControls
		{
			public float Throttle, Steer, Pitch, Yaw, Roll;
			public int Jump, Boost, Handbrake;
		}

		// --- Initialisation ---

		/// <param name="collisionMeshesFolder">Dossier produit par RLArenaCollisionDumper.</param>
		/// <returns>0 si l'initialisation a réussi.</returns>
		[DllImport(Lib, EntryPoint = "rsc_init", CharSet = CharSet.Ansi)]
		public static extern int Init(string collisionMeshesFolder);

		/// <summary>
		/// Initialise <b>sans</b> les meshes de collision du jeu.
		///
		/// <para>Le sol, le plafond et les murs latéraux sont des plans construits par le code de
		/// RocketSim, pas des meshes. Les meshes ne couvrent que les <b>coins arrondis</b>, les
		/// <b>rampes</b> et la <b>géométrie des buts</b>. On les remplace par un triangle unique
		/// placé au-dessus du plafond, uniquement pour satisfaire le contrôle « la liste ne doit
		/// pas être vide ».</para>
		///
		/// <para><b>Reste exact</b> : la frappe voiture-balle, les rebonds sol / plafond / murs
		/// latéraux, la balistique — donc tout ce qu'il faut pour juger un tir direct.</para>
		///
		/// <para><b>Devient faux, silencieusement</b> : les coins arrondis, le fond de terrain
		/// autour des buts, l'entrée dans le but. Une balle envoyée là-bas traverse le vide.
		/// Il faut donc juger un tir sur sa vitesse à la sortie du contact et couper la
		/// simulation avant que la balle n'atteigne un coin — ne jamais la laisser courir
		/// jusqu'au fond du terrain.</para>
		/// </summary>
		/// <returns>0 si l'initialisation a réussi.</returns>
		[DllImport(Lib, EntryPoint = "rsc_init_planes_only")]
		public static extern int InitPlanesOnly();

		[DllImport(Lib, EntryPoint = "rsc_is_initialized")]
		public static extern int IsInitialized();

		// --- Arène ---

		[DllImport(Lib, EntryPoint = "rsc_arena_create")]
		public static extern IntPtr ArenaCreate(float tickRate);

		/// <summary>Copie profonde. C'est l'opération de base d'une recherche : cloner l'état
		/// courant une fois par candidat, puis dérouler chaque clone séparément.</summary>
		[DllImport(Lib, EntryPoint = "rsc_arena_clone")]
		public static extern IntPtr ArenaClone(IntPtr arena);

		[DllImport(Lib, EntryPoint = "rsc_arena_destroy")]
		public static extern void ArenaDestroy(IntPtr arena);

		[DllImport(Lib, EntryPoint = "rsc_arena_step")]
		public static extern void ArenaStep(IntPtr arena, int ticks);

		[DllImport(Lib, EntryPoint = "rsc_arena_tick_count")]
		public static extern ulong ArenaTickCount(IntPtr arena);

		// --- Voitures ---

		/// <param name="team">0 = bleu, 1 = orange.</param>
		/// <returns>L'identifiant de la voiture, ou 0 en cas d'échec.</returns>
		[DllImport(Lib, EntryPoint = "rsc_arena_add_car")]
		public static extern uint ArenaAddCar(IntPtr arena, int team);

		[DllImport(Lib, EntryPoint = "rsc_car_set_state")]
		public static extern int CarSetState(IntPtr arena, uint carId, in RSCarState state);

		[DllImport(Lib, EntryPoint = "rsc_car_get_state")]
		public static extern int CarGetState(IntPtr arena, uint carId, out RSCarState state);

		[DllImport(Lib, EntryPoint = "rsc_car_set_controls")]
		public static extern int CarSetControls(IntPtr arena, uint carId, in RSCarControls controls);

		// --- Balle ---

		[DllImport(Lib, EntryPoint = "rsc_ball_set_state")]
		public static extern void BallSetState(IntPtr arena, in RSBallState state);

		[DllImport(Lib, EntryPoint = "rsc_ball_get_state")]
		public static extern void BallGetState(IntPtr arena, out RSBallState state);
	}
}
