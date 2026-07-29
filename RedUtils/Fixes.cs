namespace RedUtils
{
	/// <summary>
	/// Flags pour activer/désactiver individuellement les correctifs du framework.
	/// <para>Passe un flag à false pour retrouver le comportement d'origine (buggé),
	/// recompile (dotnet build Bot.sln), puis rejoue le scénario correspondant
	/// dans state_setting_tests_fixes.py pour comparer.</para>
	/// </summary>
	public static class Fixes
	{

		/// <summary>Feature — Dribble : catch/carry DIRECTIONNEL de la balle.
		/// <para>Sans ce flag, comportement d'origine (poussée aveugle dans l'axe voiture→balle → contre un mur
		/// la balle grimpe et rebondit dans notre camp). Avec le flag, deux états :</para>
		/// <para>• CATCH : se placer sous le point de chute prédit, décalé pour poser la balle sur le NEZ,
		/// à vitesse synchronisée (arrive pile à temps, sans dépasser ni la rater).</para>
		/// <para>• CARRY : garder la balle légèrement en avant du nez (contrôle de vitesse proportionnel) et
		/// la pousser vers le but adverse, en roulant le long des murs latéraux au lieu d'y monter.</para>
		/// <para>Réglages dans Dribble.cs : CatchHeight, NoseOffset, CarryFrontOffset, CarrySpeedGain,
		/// CarryPushOffset, WallAvoidDist.</para></summary>
		public static bool DirectionalDribble = true;

		/// <summary>DEBUG — Trace frame par frame le déclenchement du JumpShot quand la voiture est
		/// sur un mur (normal non verticale). Imprime timeRemaining/timeToJump/eta/alignement et la
		/// raison (WAIT / JUMP / ABORT-xxx). À mettre à false une fois le diagnostic terminé.</summary>
		public static bool DebugWallJumpShot = false;

		/// <summary>DEBUG — Logue POURQUOI un JumpShot s'abandonne (au sol), avec la ou les gardes qui
		/// ont déclenché : window (trop tard pour sauter) / landed / invalid (ShotValid, la balle
		/// dévie) / boost / eta (n'arrive pas à temps) / early (un meilleur tir semble exister).
		/// Sert à trancher pourquoi un save à répétition ne va jamais au bout du saut.</summary>
		public static bool DebugSaveJump = true;

		/// <summary>Feature — Garde `abortEarly` du JumpShot/DoubleJumpShot.
		/// <para><b>true</b> = comportement d'origine : le tir s'abandonne dès qu'il arrive &gt; 0.25s trop
		/// tôt (pour laisser la stratégie re-choisir). <b>false</b> (défaut) = on la retire : le tir
		/// commit, se met en place et ATTEND le moment du saut.</para>
		/// <para>Mesuré : la garde jetait la marge (voiture en position 0.7s avant l'impact) et bouclait
		/// avec FindShot — or FindShot renvoie déjà la slice la plus précoce, donc « arriver tôt » n'est
		/// pas un signal de re-optimisation, juste une mise en place anticipée. La dérive de balle reste
		/// couverte par abortInvalid/ShotValid. Passe à true pour comparer avec l'ancien comportement.</para></summary>
		public static bool JumpAbortWhenEarly = false;

		/// <summary>Feature — Moteur de déplacement (Bot/Movement.cs) au lieu de Drive.GetEta.
		/// <para>Movement.Eta étalonne sur mesures ce que Drive.GetEta estimait mal : coût réel du
		/// virage, surcoût du flip, et freinage quand la voiture s'éloigne de sa cible (le plus gros
		/// écart mesuré, +34 %). Voir ETA_MESURES.md.</para>
		/// <para>Mettre à false pour retrouver Drive.GetEta et comparer les deux sur les mêmes
		/// courses du banc.</para></summary>
		public static bool MovementEngine = true;

		/// <summary>Feature — Latch de la cible de save (MyBot, branche TryDefensivePriority).
		/// <para>Garde la dernière interception connue quand FindInterceptSlice rate un tick, au lieu
		/// de sauter au repli près-but. Corrige l'oscillation de la voiture, mais MASQUE la cause
		/// racine (un tick sur deux raté).</para>
		/// <para>EN PAUSE (false) pour diagnostiquer le comportement brut via les logs [SAVEMISS].</para></summary>
		public static bool SaveTargetLatch = false;

		/// <summary>DEBUG — Banc d'étalonnage de l'ETA à vitesse maximale.
		/// <para>Quand ce flag est vrai, le bot ABANDONNE toute stratégie : il roule à fond (boost
		/// autorisé) vers la position de la balle, et imprime l'ETA prédit puis le temps réellement
		/// mis. C'est la brique de base du moteur de déplacement : tant que « temps minimal pour
		/// aller de A à B » n'est pas juste, tout ce qui s'appuie dessus (choix de tir, possession,
		/// rôles, dosage du boost) est bâti sur du sable.</para>
		/// <para>Utilisation : mettre à true, recompiler, lancer un match, puis dérouler
		/// state_setting_tests_eta.py. Chaque scénario place la voiture et la balle ; le bot fonce et
		/// imprime une ligne [BENCH].</para></summary>
		public static bool EtaBench = false;

		/// <summary>DEBUG — Banc de mesure du Wavedash (une seule action, isolée).
		/// <para>Quand ce flag est vrai, le bot ABANDONNE toute stratégie : depuis la pose du state
		/// setter (vitesse initiale imposée), il déclenche UN wavedash droit devant, throttle à fond
		/// et sans jamais demander de boost, puis imprime ce que la manœuvre a réellement produit :</para>
		/// <para>• vitesse au sol de départ, pic atteint, vitesse une fois retombé → gain net ;</para>
		/// <para>• boost consommé (doit être ~0 : le wavedash ne booste pas) ;</para>
		/// <para>• durée de la manœuvre = temps de NON-DISPONIBILITÉ (saut → air → dodge d'atterrissage
		/// → de nouveau au sol), pendant lequel aucun autre wavedash n'est possible.</para>
		/// <para>Sert à calibrer quand il vaut le coup de remplacer un Drive/Dodge par un wavedash
		/// (cf. Drive.cs branche sol). Utilisation : mettre à true, recompiler, lancer un match, puis
		/// dérouler state_setting_tests_wavedash.py.</para></summary>
		public static bool WavedashBench = false;

		/// <summary>DEBUG — Banc de mesure de Drive.GetEta.
		/// <para>À chaque cible que le bot se fixe, enregistre l'ETA PRÉDIT, puis mesure le temps
		/// RÉELLEMENT mis pour y arriver et imprime l'écart. C'est la seule façon de savoir si GetEta
		/// dit la vérité, plutôt que de régler ses constantes au jugé.</para>
		/// <para>Lire les lignes [ETA] : erreur &gt; 0 = GetEta est optimiste (le bot arrive en retard,
		/// il s'engage sur des interceptions hors de portée) ; erreur &lt; 0 = pessimiste (le bot refuse
		/// des tirs jouables). ABANDON = la cible a changé avant l'arrivée, mesure non conclusive.</para></summary>
		public static bool DebugEta = true;

		/// <summary>Feature — Circuit de save unifié (action Save dirigée).
		/// <para><b>false</b> (défaut) = ancien circuit : FindShot(Save) si un tir dirigé est jouable,
		/// sinon interception d'urgence <c>Drive→Save</c> qui se met dans la trajectoire (goal-side,
		/// cible latchée). <b>true</b> = nouvelle action <c>Save</c> unique (approche goal-side + frappe
		/// par hauteur + dodge dirigé), qui remplace tout le circuit.</para>
		/// <para>Laissé désactivable pour comparer : la nouvelle action est encore en rodage.</para></summary>
		public static bool UnifiedSave = false;

		/// <summary>Feature — Refonte défensive (anti-CSC + vraies saves).
		/// <para>Sans ce flag, comportement d'origine : sur une balle qui arrive dans notre camp le bot
		/// fait Fifty/Drive→Balle sans se soucier de sa position → touches non maîtrisées vers notre but.</para>
		/// <para>Avec le flag, trois priorités AVANT la logique standard :</para>
		/// <para>• SAVE (tous rôles) : si Ball.Prediction voit la balle entrer dans NOTRE but,
		/// tir de dégagement (Target away-from-goal) ; sinon interception d'urgence sur la
		/// trajectoire avec boost autorisé (intent Shot→Save / Drive→Save).</para>
		/// <para>• DÉGAGEMENT (Attacker) : balle dans notre tiers ou fonçant vers notre but →
		/// tir loin du but (intent Shot→Dégagement).</para>
		/// <para>• GOAL-SIDE (Attacker) : jamais de contact en poursuivant la balle vers notre propre
		/// but — on se replie d'abord entre la balle et le but (intent Drive→GoalSide).
		/// C'est LE correctif anti-CSC : un Fifty/contact n'est tenté que goal-side,
		/// donc la poussée (voiture→balle) part toujours vers le camp adverse.</para></summary>
		public static bool DefensiveOverhaul = true;

		/// <summary>DEBUG — Trace un tir en cours, 10 fois par seconde, jusqu'à sa fin.
		/// <para>Sert à départager POURQUOI un tir rate, sans supposer : mauvaise cible, arrivée
		/// trop tardive, ou prédiction de balle qui dérive. Voir MyBot.TraceShot pour la lecture
		/// des colonnes. À remettre à false une fois le diagnostic terminé.</para></summary>
		public static bool DebugShot = true;

		/// <summary>Feature — Pressing offensif.
		/// <para>Sans ce flag, comportement d'origine : en état NotPossessed les deux bots se replient
		/// quelle que soit la zone — l'Attacker shadow à 60% entre notre but et la balle (soit le rond
		/// central quand la balle est chez eux) et le Support couvre notre but. L'adversaire construit
		/// son attaque sans aucune pression.</para>
		/// <para>Avec le flag, quand la balle est dans LEUR moitié :</para>
		/// <para>• Attacker : Drive vers le premier slice de Ball.Prediction atteignable pour contester
		/// la balle (intent Drive→Pressing) au lieu du shadow.</para>
		/// <para>• Support : monte sur BackupPosition (2500u goal-side de la balle, back post) au lieu
		/// de rentrer sur DefensivePosition — il avance avec le jeu et sert de relais.</para>
		/// <para>En zone défensive, le repli d'origine est conservé.</para></summary>
		public static bool OffensivePressing = true;

	}
}
