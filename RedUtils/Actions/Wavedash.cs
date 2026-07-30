using System;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>A wavedash action.
	/// <para><b>boost=false</b> (défaut) : algo d'origine — saut 1 tick, vise l'atterrissage, dodge
	/// près du sol.</para>
	/// <para><b>boost=true</b> : variante boostée — saut, nez légèrement en BAS + boost (gagne de la
	/// vitesse en l'air), on redresse le nez en HAUT, puis wavedash quand on retombe près du sol.</para></summary>
	public class Wavedash : IAction
	{
		/// <summary>Whether or not this action has finished</summary>
		public bool Finished { get; private set; }
		/// <summary>Wavedashes aren't interruptible, so this will always be false</summary>
		public bool Interruptible { get; private set; }

		/// <summary>The direction we plan to wavedash in</summary>
		public Vec3 Direction;
		/// <summary>Variante boostée (saut → nez bas + boost → nez haut → dodge).</summary>
		public bool Boost;
		/// <summary>Estimation de la durée totale (= temps de non-disponibilité), utilisée par Drive
		/// pour son budget temps. Mesuré au banc : ~0.90s pour la variante boostée, ~0.97s sans boost.</summary>
		public float Duration { get { return Boost ? 0.9f : 0.97f; } }

		// --- Réglages de la variante boostée (guess &amp; check) ---
		/// <summary>Pitch (négatif = nez en bas) pour piquer le nez. « Légèrement ».</summary>
		public static float BoostDownPitch = -0.5f;
		/// <summary>Angle de nez-en-bas (deg) à atteindre AVANT de commencer à booster.</summary>
		public static float BoostDownAngle = 1f;
		/// <summary>Durée de la phase de boost, une fois le nez assez bas.</summary>
		public static float BoostTime = 0.2f;
		/// <summary>Pitch (positif = nez en haut) pour redresser après le boost.</summary>
		public static float BoostUpPitch = 0.7f;

		/// <summary>Whether we still need to fire the (single-tick) initial jump</summary>
		private bool _jumping = true;
		/// <summary>Whether we have actually left the ground since starting</summary>
		private bool _leftGround = false;
		/// <summary>When we started this action</summary>
		private float _startTime = -1;
		/// <summary>The inputs for the dodge direction</summary>
		private Vec3 _input = Vec3.Zero;

		/// <summary>Étapes de la variante boostée.</summary>
		private enum BoostPhase { Jump, Down, Boost, Up, Dodge, Recover }
		private BoostPhase _bphase = BoostPhase.Jump;
		private float _bphaseStart = -1;

		/// <summary>Initialize a new wavedash action</summary>
		/// <param name="direction">The direction which we will attempt to dash in.
		/// If null, we will dash in the direction we are already going.</param>
		/// <param name="boost">If true, use the boosted variant (nose down + boost, then wavedash).</param>
		public Wavedash(Vec3? direction = null, bool boost = false)
		{
			Interruptible = false;
			Finished = false;

			Direction = direction ?? Vec3.Zero;
			Boost = boost;
		}

		// TODO(GetEta) : modèle analytique du wavedash pour Movement / Drive.GetEta.
		// Fournir une fonction statique WavedashModel(float v0, bool boost, out float time, out float dist) :
		//   - time  ≈ LINÉAIRE en v0 (quasi constant : ~0.9s boosté, ~0.97s sans boost) ;
		//   - dist  = POLYNÔME en v0 (l'accélération plafonne près du supersonique → distance sous-linéaire).
		// Mesures de calibration (banc, throttle plein, fenêtre = duree) :
		//   sans boost : v0=0 d=147 | 404 d=518 | 904 d=976 | 1412 d=1437 | 1912 d=1905   (vFin 679..2284)
		//   avec boost : v0=0 d=279 | 404 d=624 | 912 d=1058 | 1412 d=1480 | 1904 d=1919  (vFin 887..2290, ~7 boost)

		/// <summary>Runs this wavedash action</summary>
		public void Run(RUBot bot)
		{
			// If this action hasn't started yet
			if (_startTime == -1)
			{
				// Set the start time, and whether or not we should jump (only when starting grounded)
				_startTime = Game.Time;
				_jumping = bot.Me.IsGrounded;
				// Départ en l'air (mur/retombée) → on saute la phase de saut/boost et on va redresser.
				_bphase = bot.Me.IsGrounded ? BoostPhase.Jump : BoostPhase.Up;
			}
			float elapsed = Game.Time - _startTime;

			// Remember once we have actually left the ground
			if (!bot.Me.IsGrounded)
				_leftGround = true;

			if (Boost)
			{
				RunBoosted(bot, elapsed);
				return;
			}

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

		/// <summary>Variante boostée : saut → nez bas + boost → nez haut → dodge à l'atterrissage.</summary>
		private void RunBoosted(RUBot bot, float elapsed)
		{
			// Angle du nez : Forward.z ≈ sin(pitch). Négatif = nez en bas.
			float noseAngle = MathF.Asin(Utils.Cap(bot.Me.Forward.z, -1f, 1f)) * 180f / MathF.PI;

			switch (_bphase)
			{
				case BoostPhase.Jump:
					// 1) Saut sur UN tick, puis on pique le nez.
					bot.Controller.Jump = true;
					_bphase = BoostPhase.Down;
					break;

				case BoostPhase.Down:
					// 2a) On pique le nez vers le BAS (sans booster) jusqu'à atteindre l'angle voulu.
					bot.Controller.Pitch = BoostDownPitch;
					if (noseAngle <= -BoostDownAngle)
					{
						_bphase = BoostPhase.Boost;
						_bphaseStart = Game.Time;
					}
					// Sécurité : si on arrive déjà au sol sans avoir atteint l'angle, on dodge quand même.
					else if (!bot.Me.IsGrounded && bot.Me.Location.z < 40 && bot.Me.Velocity.z < -100)
						_bphase = BoostPhase.Dodge;
					break;

				case BoostPhase.Boost:
					// 2b) Nez assez bas → on BOOST (en gardant le nez bas) pendant BoostTime, pour se
					//     propulser vers l'avant en gagnant de la vitesse tout en redescendant.
					bot.Controller.Pitch = BoostDownPitch;
					bot.Controller.Boost = true;
					if (Game.Time - _bphaseStart >= BoostTime)
						_bphase = BoostPhase.Up;
					break;

				case BoostPhase.Up:
					// 3) On redresse le nez vers le HAUT pour se remettre à plat avant l'atterrissage.
					bot.Controller.Pitch = BoostUpPitch;
					// Dodge juste avant de toucher le sol (un flip doit partir en l'air). Même détection
					// que le wavedash normal, pas de paramètre en plus.
					if (!bot.Me.IsGrounded && bot.Me.Location.z < 40 && bot.Me.Velocity.z < -100)
						_bphase = BoostPhase.Dodge;
					break;

				case BoostPhase.Dodge:
					// 4) Wavedash quand on touche le sol (roues arrière) : dodge nez en bas (flip avant).
					if (_input.Length() == 0)
					{
						_input = Direction.Length() > 0 ?
								new Vec3(bot.Me.Local(Direction)[1], -bot.Me.Local(Direction)[0]) :
								new Vec3(bot.Me.Local(bot.Me.Velocity).Normalize()[1], -bot.Me.Local(bot.Me.Velocity).Normalize()[0]);
					}
					bot.Controller.Jump = true;
					bot.Controller.Yaw = _input[0];
					bot.Controller.Pitch = _input[1];
					if (bot.Me.IsGrounded)
						_bphase = BoostPhase.Recover;
					break;

				case BoostPhase.Recover:
					if (bot.Me.IsGrounded && _leftGround)
						Finished = true;
					break;
			}

			// Garde-fou : action non-interruptible, on la termine si jamais on ne retombe pas.
			if (elapsed > Duration + 0.6f)
				Finished = true;
		}
	}
}
