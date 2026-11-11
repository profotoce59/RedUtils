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

		/// <summary>
		/// Angle de nez minimal (deg) pour AUTORISER le dodge d'atterrissage.
		/// <para>Un dodge avant sur un nez déjà piqué l'enfonce encore : au lieu de replaquer les roues,
		/// la voiture part en front flip et redécolle. Mesuré sur un enchaînement : nez à −23° au
		/// moment du dodge → −32°, −67°, −70° et <c>vz=+232</c>, manœuvre perdue. Trop piqué, on ne
		/// dodge pas : on redresse à fond et on atterrit à plat. Pas de gain de vitesse, mais pas de
		/// désastre non plus.</para>
		/// </summary>
		private const float MinDodgeNoseAngle = -10f;

		/// <summary>
		/// Durée maximale de la phase « nez en bas » (depuis le début de la manœuvre).
		/// <para>Sans plafond, une rotation résiduelle d'atterrissage peut faire MONTER le nez au lieu
		/// de le piquer (mesuré sur un enchaînement : +2°, +3°, +1° malgré un pitch négatif), et cette
		/// phase passe de 0,12 s à 0,38 s. Tout le séquencement glisse, et la phase de redressement
		/// n'a plus le temps de remettre le nez à plat avant le sol.</para>
		/// </summary>
		public static float BoostDownMaxTime = 0.2f;

		/// <summary>
		/// Écart maximal (rad) entre le nez et la direction demandée. <b>20°</b> : au-delà, le dodge
		/// part trop de biais et la manœuvre coûte plus de vitesse qu'elle n'en rapporte.
		/// <para>Plafond appliqué DANS l'action, pas chez l'appelant : c'est une limite physique de la
		/// manœuvre, elle doit tenir quel que soit ce qu'on lui passe. Une direction plus désaxée est
		/// ramenée à ce cône — on fait un wavedash utile dans la bonne famille de direction plutôt
		/// qu'un flip en travers.</para>
		/// </summary>
		private const float MaxDirectionAngle = 0.35f;   // ~20°

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

		private string _loggedPhase = "";
		private float _lastPhaseLog = -1f;

		/// <summary>
		/// Trace phase par phase (Fixes.DebugWavedashPhases) : une ligne au CHANGEMENT de phase
		/// (préfixe « &gt;&gt; ») et un battement 10x/s tant qu'on y reste.
		///
		/// <para>Lecture : une phase qui s'éternise avec <c>sol=OUI</c> = le saut n'est jamais parti,
		/// la voiture roule au lieu de décoller, et la machine attend une condition aérienne qui ne
		/// viendra pas. Comparer la chronologie du 1er dash et celle des suivants : c'est là que se
		/// voit ce qui diffère dans un enchaînement.</para>
		/// </summary>
		private void LogPhase(RUBot bot, string phase, float elapsed)
		{
			if (!Fixes.DebugWavedashPhases || bot.Me.Name != "MyBot")
				return;

			bool changed = phase != _loggedPhase;
			if (!changed && Game.Time - _lastPhaseLog < 0.1f)
				return;
			_loggedPhase = phase;
			_lastPhaseLog = Game.Time;

			float noseAngle = MathF.Asin(Utils.Cap(bot.Me.Forward.z, -1f, 1f)) * 180f / MathF.PI;
			Console.WriteLine($"[WD]{(changed ? " >>" : "   ")} {phase,-9} t={elapsed:F3}s " +
				$"sol={(bot.Me.IsGrounded ? "OUI" : "non")} z={bot.Me.Location.z:F0} vz={bot.Me.Velocity.z:F0} " +
				$"nez={noseAngle:+0;-0}° v={bot.Me.Velocity.FlatLen():F0} " +
				$"saut={(bot.Me.HasJumped ? "oui" : "non")} dj={(bot.Me.HasDoubleJumped ? "oui" : "non")}");
		}

		/// <summary>
		/// Direction effectivement jouée : celle demandée, ramenée dans le cône
		/// <see cref="MaxDirectionAngle"/> autour du nez. Sert AUSSI BIEN à la tenue en l'air qu'au
		/// dodge, pour que la voiture s'oriente vers là où elle va réellement flipper.
		/// </summary>
		private Vec3 ClampedDirection(Car car)
		{
			Vec3 forward = car.Forward.FlatNorm();
			Vec3 wanted = Direction.Length() > 0 ? Direction.FlatNorm() : car.Velocity.FlatNorm();

			if (wanted.Length() < 1e-4f)
				return forward;
			if (forward.FlatAngle(wanted) <= MaxDirectionAngle)
				return wanted;

			// Rotate est anti-horaire : +angle = gauche. Clamp attend (start, end) en ordre horaire.
			return wanted.Clamp(forward.Rotate(MaxDirectionAngle), forward.Rotate(-MaxDirectionAngle), Vec3.Up).FlatNorm();
		}

		/// <summary>
		/// Instant où le dodge a été DEMANDÉ (-1 si pas encore). Le flip continue d'agir sur la
		/// rotation un certain temps APRÈS cet instant, y compris une fois retombé — état qu'aucun
		/// champ de <see cref="Car"/> n'expose et que le state setter remet à zéro. C'est donc la
		/// seule référence disponible pour savoir si un enchaînement part trop tôt.
		/// </summary>
		public float DodgeTime { get; private set; } = -1f;

		/// <summary>Entrées du dodge (yaw, pitch) vers la direction bornée.</summary>
		private void SetDodgeInput(RUBot bot)
		{
			if (_input.Length() != 0)
				return;
			Vec3 dir = ClampedDirection(bot.Me);
			_input = new Vec3(bot.Me.Local(dir)[1], -bot.Me.Local(dir)[0]);
			DodgeTime = Game.Time;
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
				LogPhase(bot, "SAUT", elapsed);
				// Initial jump on a SINGLE tick (minimal jump), then we never re-jump here
				bot.Controller.Jump = true;
				_jumping = false;
			}
			else if (!bot.Me.IsGrounded && bot.Me.Location.z < 40 && bot.Me.Velocity.z < -100
					 && MathF.Asin(Utils.Cap(bot.Me.Forward.z, -1f, 1f)) * 180f / MathF.PI >= MinDodgeNoseAngle)
			{
				// Même garde que la variante boostée : jamais de dodge sur un nez piqué, il enfonce
				// le nez au lieu de replaquer les roues et la voiture part en front flip.
				LogPhase(bot, "DODGE", elapsed);
				// If we are about to hit the ground, dodge! Direction bornée à MaxDirectionAngle.
				SetDodgeInput(bot);

				// Dodges using the input set earlier
				bot.Controller.Yaw = _input[0];
				bot.Controller.Pitch = _input[1];
				bot.Controller.Jump = true;
			}
			else if (!bot.Me.IsGrounded)
			{
				LogPhase(bot, "AIR", elapsed);
				// Aim slightly above the ground, in the direction given (bornée : on s'oriente vers là
				// où l'on va RÉELLEMENT flipper, sinon la voiture vise un cap qu'elle ne jouera pas)
				Vec3 landingNormal = Field.FindLandingSurface(bot.Me).Normal;
				bot.AimAt(bot.Me.Location + ClampedDirection(bot.Me).FlatNorm(landingNormal) + landingNormal * 0.2f, landingNormal);
			}
			else if (_leftGround)
			{
				LogPhase(bot, "FIN", elapsed);
				// On ne termine qu'une fois REVENU au sol après avoir décollé. Sans cette garde, avec un
				// saut d'un seul tick on finirait dès le tick suivant (la voiture est encore au sol le
				// temps de décoller).
				Finished = true;
			}
			else
			{
				// Au sol, pas encore décollé : on attend que le saut fasse effet. Rester ici veut dire
				// que le saut n'est JAMAIS parti — cas typique d'un enchaînement où l'on rejumpe avant
				// que le jeu ait réarmé le saut après le flip précédent. La voiture roule alors tout
				// droit pendant que la machine attend un décollage qui ne viendra pas.
				LogPhase(bot, "ATTENTE-SOL", elapsed);
			}
		}

		/// <summary>Variante boostée : saut → nez bas + boost → nez haut → dodge à l'atterrissage.</summary>
		private void RunBoosted(RUBot bot, float elapsed)
		{
			// Angle du nez : Forward.z ≈ sin(pitch). Négatif = nez en bas.
			float noseAngle = MathF.Asin(Utils.Cap(bot.Me.Forward.z, -1f, 1f)) * 180f / MathF.PI;

			// STABILISATION lacet/roulis pendant toute la phase aérienne. Les phases Down/Boost/Up ne
			// pilotaient que le pitch : le moindre résidu de rotation au décollage n'était jamais
			// corrigé, la voiture dérivait de travers en l'air, et le dodge final partait en biais —
			// il coûtait de la vitesse au lieu d'en gagner. La variante NON boostée n'a pas ce défaut
			// parce qu'elle appelle AimAt à chaque tick ; on fait pareil ici.
			// AimAt règle aussi le Pitch : chaque phase le réécrit APRÈS, c'est voulu (le pitch est la
			// mécanique de cette variante, le reste n'est que de la tenue en l'air).
			if (!bot.Me.IsGrounded)
			{
				Vec3 landingNormal = Field.FindLandingSurface(bot.Me).Normal;
				bot.AimAt(bot.Me.Location + ClampedDirection(bot.Me).FlatNorm(landingNormal) * 500f, landingNormal);
			}

			// Une phase qui s'éternise avec sol=OUI = le saut n'est jamais parti.
			LogPhase(bot, _bphase.ToString().ToUpperInvariant(), elapsed);

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
					else if (elapsed > BoostDownMaxTime)
					{
						// Le nez n'est pas descendu à temps (rotation résiduelle d'un enchaînement) :
						// on ABANDONNE le gain de la variante boostée et on passe au redressement.
						// Insister ferait glisser toute la séquence, et le dodge partirait nez piqué —
						// donc en front flip. Un wavedash simple vaut mieux qu'un boost raté.
						_bphase = BoostPhase.Up;
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
					// Pitch INCHANGÉ (BoostUpPitch). Une version passait le pitch à fond tant que le nez
					// était sous MinDodgeNoseAngle : or le nez d'un PREMIER wavedash réussi passe cette
					// phase entre −20° et −3°, donc la garde s'appliquait aussi à lui et allongeait la
					// manœuvre (0,90 s → 0,95 s mesuré). On ne touche pas à ce qui marche : c'est la
					// garde sur le DODGE, plus bas, qui empêche le front flip.
					bot.Controller.Pitch = BoostUpPitch;

					if (bot.Me.IsGrounded)
					{
						// Reposé sans avoir pu dodger : manœuvre sans gain, mais atterrissage À PLAT
						// au lieu d'un front flip. On rend la main proprement.
						_bphase = BoostPhase.Recover;
					}
					else if (bot.Me.Location.z < 40 && bot.Me.Velocity.z < -100)
					{
						// Dodge juste avant de toucher le sol (un flip doit partir en l'air), MAIS
						// seulement si le nez est assez relevé : piqué, le dodge avant l'enfonce encore
						// et la voiture redécolle en front flip au lieu de poser ses roues.
						if (noseAngle >= MinDodgeNoseAngle)
							_bphase = BoostPhase.Dodge;
					}
					break;

				case BoostPhase.Dodge:
					// 4) Wavedash quand on touche le sol (roues arrière) : dodge nez en bas (flip avant).
					SetDodgeInput(bot);
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
			// Une manœuvre qui sort PAR ICI n'a pas fonctionné — la durée mesurée vaut alors
			// Duration + 0.6 s (1.5 s) et non la durée d'un vrai wavedash. C'est le symptôme à
			// reconnaître dans les mesures du banc.
			if (elapsed > Duration + 0.6f)
			{
				LogPhase(bot, "TIMEOUT", elapsed);
				Finished = true;
			}
		}
	}
}
