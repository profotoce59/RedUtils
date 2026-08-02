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

		/// <summary>Feature — Moteur de déplacement (RedUtils/Movement.cs) au lieu de Drive.GetEta.
		/// <para>Movement.Eta étalonne sur mesures ce que Drive.GetEta estimait mal : coût réel du
		/// virage, surcoût du flip, et freinage quand la voiture s'éloigne de sa cible (le plus gros
		/// écart mesuré, +34 %). Voir ETA_MESURES.md.</para>
		/// <para><b>Portée (AUDIT §1.1) — volontairement PARTIELLE.</b> Ce flag ne concerne que les
		/// appelants « déplacement au sol vers un point », le seul cas étalonné au banc :
		/// <c>Rotation.FirstReachableEta</c> / <c>ComputeScore</c> (possession, rôles),
		/// <c>MyBot.ContestPoint</c>, <c>MyBot.InterceptReachable</c> (interception de save).</para>
		/// <para>Les tirs (<c>Shot.IsValid</c>) et les actions au contact (<c>Fifty</c>,
		/// <c>Save</c>, <c>GetBoost</c>, <c>Arrive</c>) restent sur <c>Drive.GetEta</c>
		/// <b>délibérément</b> : ils demandent « quand puis-je être là EN ROULANT DANS LA BONNE
		/// DIRECTION », un cas que le banc ne mesure pas. Tout basculer sur Movement a été essayé
		/// et <b>joue moins bien</b> (mesuré en match). Voir « domaine de validité » en tête de
		/// Movement.cs avant d'ajouter un appelant.</para>
		/// <para>Mettre à false pour ramener ces appelants-là sur Drive.GetEta et comparer les deux
		/// moteurs sur les mêmes courses du banc.</para></summary>
		public static bool MovementEngine = true;

		/// <summary>Feature — Moteur de collision pour l'évaluation des tirs.
		/// <para><b>false</b> (défaut) = <c>DefaultShotCheck</c> : la vitesse de la balle après
		/// impact est estimée par la formule historique de RedUtils
		/// (<c>(vitesseVoiture * 6 + vitesseBalle) / 7</c>).</para>
		/// <para><b>true</b> = <c>AccurateShotCheck</c> : impulsion reprise de RocketSim
		/// (<c>Ball::_OnHit</c>) — direction de contact aplatie en Z, composante avant réduite,
		/// courbe d'impulsion par paliers. Plus juste sur les tirs d'angle, où la formule historique
		/// surestime la déviation.</para>
		/// <para>Remplace l'ancienne constante <c>MyBot.AccuratePhysics</c>, qui imposait une
		/// recompilation pour changer de moteur. Voir Tools.cs.</para></summary>
		public static bool RocketSimShotCheck = false;

		/// <summary>Feature — Patience de tir (AUDIT §2.2).
		/// <para>Sans ce flag, comportement d'origine : <c>Ball.Prediction.Find</c> renvoie la
		/// PREMIÈRE slice jouable, donc le bot tire toujours le plus tôt possible, quel que soit
		/// l'angle. Une balle qui traverse depuis le corner est frappée pendant qu'elle est encore
		/// de côté, alors qu'attendre quelques dixièmes la place face au but.</para>
		/// <para>Avec le flag, en <c>Possessed</c> + zone offensive uniquement, le ShotCheck refuse
		/// les slices dont l'écart latéral au-delà du poteau dépasse la distance restante jusqu'à la
		/// ligne — mais SEULEMENT tant que la balle revient vers l'axe. Sur une balle qui part dans
		/// le corner, on tire quand même : attendre un mieux qui ne viendra pas revient à ne jamais
		/// tirer.</para>
		/// <para>Restreint à <c>Possessed</c> à dessein : c'est le seul état où l'on a le temps.
		/// En <c>Contested</c> l'adversaire arrive ; sur un dégagement ou une save la question ne se
		/// pose pas. Réglages : <c>MyBot.PatienceMaxX</c>, <c>MyBot.PatienceClosingSpeed</c>.</para></summary>
		public static bool PatientShot = true;

		/// <summary>Feature — Boost ramassé sur le trajet de repli (AUDIT §2.4).
		/// <para>Sans ce flag, comportement d'origine : le Support ne collecte que via l'hystérésis
		/// 30/60, et le pad est choisi parmi les gros pads goal-side de la balle — ce qui peut
		/// imposer une longue traversée. Se replacer et se recharger sont deux activités séparées.
		/// </para>
		/// <para>Avec le flag, un repli (<c>Arrive→Couverture</c>, <c>Arrive→BackupPos</c>) passe
		/// par un pad quand celui-ci est presque sur le chemin : plus près de la destination que
		/// nous, même côté de terrain, et détour borné (<c>Rotation.MaxDetour</c>). Le score est le
		/// détour, pas la distance au pad — c'est le détour qui se paie en position. Les petits pads
		/// sont pénalisés (<c>SmallPadPenalty</c>), et au-dessus de <c>BoostSeekCeiling</c> aucun
		/// détour n'est envisagé.</para>
		/// <para>Limité au Support pour l'instant. L'étendre à l'Attacker (qui peut jouer tout un
		/// match à 0 boost) demande de décider quand un repli d'Attacker est un vrai repli — à
		/// traiter séparément.</para></summary>
		public static bool RetreatBoost = true;

		/// <summary>Feature — Vrai poste de dernier homme (AUDIT §2.5). Bascule DEUX changements
		/// solidaires : le point visé, et la façon de l'occuper.
		/// <para><b>Sans le flag</b> : <c>DefensivePosition</c> = 20 % du chemin centre du but →
		/// balle, rejoint par un <c>Arrive</c>. Deux défauts. Le point ignore le côté, donc le
		/// dernier homme se place sur la trajectoire de la balle — du même côté que l'attaquant.
		/// Et <c>Arrive</c> roule à pleine vitesse jusqu'au bout (aucun <c>arrivalTime</c>, donc
		/// vitesse cible = MaxSpeed) : la voiture déborde et doit faire demi-tour. Elle recule en
		/// prime sa cible pour s'aligner sur la direction d'arrivée, ce qui, si près de la ligne,
		/// la place DANS le but.</para>
		/// <para><b>Avec le flag</b> : le poste est ancré au <b>deuxième poteau</b> (latéral en
		/// rampe, à l'opposé de la balle) avec une profondeur qui suit la distance balle→ligne,
		/// bornée à 1100 uu — balle dans un corner, on est au deuxième poteau sur la ligne. Il est
		/// occupé par la nouvelle action <see cref="Cover"/> : approche à vitesse plafonnée par la
		/// distance de freinage, puis arrêt sur place nez pointé vers la balle, avec pivot au frein
		/// à main si le cap est mauvais.</para>
		/// <para>Réglages : <c>Rotation.CoverAdvanceFraction / CoverMaxAdvance / CoverPostX /
		/// CoverPostRamp</c>, et les constantes de <c>Cover.cs</c>.</para></summary>
		public static bool GoalieCover = true;

		/// <summary>DEBUG — Trace l'action <see cref="Cover"/> (10x/s + rendu 3D), pour MyBot.
		/// <para>Console : état APPROCHE / HOLD / AIR, distance au POSTE (pas à la balle), écart de cap
		/// vers la balle en degrés, sens de pivot, vitesse et commandes (throttle/steer/frein à main).
		/// Rendu : ligne verte = le poste ; ligne cyan = le cap VOULU (nez → balle) ; ligne rouge = le
		/// cap RÉEL de la voiture. Si cyan et rouge divergent, la voiture ne fait pas face à la balle —
		/// et l'état dit pourquoi (en APPROCHE elle regarde son déplacement, pas la balle).</para>
		/// <para>À remettre à false une fois le diagnostic terminé.</para></summary>
		public static bool DebugCover = true;

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

		/// <summary>DEBUG — Banc de mesure de la ROTATION (une seule action, isolée).
		/// <para>Quand ce flag est vrai, le bot ABANDONNE toute stratégie : il ne joue PAS la balle,
		/// il exécute juste une <see cref="Rotate"/> depuis la pose imposée par le state setter, et
		/// imprime le profil de vitesse du trajet.</para>
		/// <para>La question mesurée est « garde-t-on la vitesse ? » : lire <c>vMin</c> (vitesse la
		/// plus basse) et <c>tempsLent</c> (temps sous 1000 uu/s). Si la vitesse casse dans l'arc,
		/// baisser <c>ArcMaxAngle</c> dans Rotate.cs pour élargir la trajectoire.</para>
		/// <para>La balle sert seulement de marqueur : la destination (<c>DefensivePosition</c>) en
		/// dépend, elle est lue UNE fois au départ puis figée. Utilisation : mettre à true,
		/// recompiler, lancer un match 4 voitures, puis dérouler state_setting_tests_rotation.py.</para></summary>
		public static bool RotationBench = false;

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

		/// <summary>DEBUG — Fait mesurer au banc Wavedash la variante BOOSTÉE (saut → nez bas + boost →
		/// nez haut → dodge) au lieu du wavedash normal. À true, le bench crée <c>new Wavedash(dir, boost:true)</c>,
		/// et la colonne boostUtilise devient non nulle (normal, cette variante consomme du boost).
		/// Sert à comparer, au banc, la variante boostée à l'ordinaire sur les mêmes scénarios.</summary>
		public static bool WavedashBenchBoost = false;

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
		/// sinon interception d'urgence <c>Arrive→Save</c> temporisée sur la trajectoire (dernière slice
		/// atteignable, cible latchée). <b>true</b> = nouvelle action <c>Save</c> unique (approche goal-side + frappe
		/// par hauteur + dodge dirigé), qui remplace tout le circuit.</para>
		/// <para>Laissé désactivable pour comparer : la nouvelle action est encore en rodage.</para></summary>
		public static bool UnifiedSave = false;

		/// <summary>Feature — Refonte défensive (anti-CSC + vraies saves).
		/// <para>Sans ce flag, comportement d'origine : sur une balle qui arrive dans notre camp le bot
		/// fait Fifty/Drive→Balle sans se soucier de sa position → touches non maîtrisées vers notre but.</para>
		/// <para>Avec le flag, trois priorités AVANT la logique standard :</para>
		/// <para>• SAVE (Attacker et solo) : si Ball.Prediction voit la balle entrer dans NOTRE but,
		/// tir de dégagement (Target away-from-goal) ; sinon interception d'urgence temporisée sur la
		/// trajectoire (intent Shot→Save / Arrive→Save). Le Support garde sa couverture.</para>
		/// <para>• DÉGAGEMENT (Attacker) : balle dans notre tiers ou fonçant vers notre but →
		/// tir loin du but (intent Shot→Dégagement).</para>
		/// <para>• GOAL-SIDE (Attacker) : jamais de contact en poursuivant la balle vers notre propre
		/// but — on se replie d'abord entre la balle et le but (intent Drive→GoalSide).
		/// C'est LE correctif anti-CSC : un Fifty/contact n'est tenté que goal-side,
		/// donc la poussée (voiture→balle) part toujours vers le camp adverse.</para></summary>
		public static bool DefensiveOverhaul = true;

		/// <summary>Feature — Challenge d'un dribble adverse plutôt qu'une save passive.
		/// <para>Sans ce flag, comportement d'origine : dès que <c>Ball.Prediction.FindGoal</c> voit
		/// la balle entrer dans notre but (priorité 1 de <c>TryDefensivePriority</c>), le bot fait la
		/// save — et quand aucun tir dirigé n'est jouable, il tombe sur <c>Arrive→Save</c> qui se
		/// TEMPORISE sur un point d'interception goal-side. Or un adversaire qui dribble la balle vers
		/// notre cage fait prédire un but à CHAQUE tick (la prédiction ignore sa voiture) : le bot
		/// attend l'interception au lieu de contester, et le porteur frappe le premier, même quand on
		/// est aussi près de la balle que l'adversaire.</para>
		/// <para>Avec le flag, quand la prédiction voit un but MAIS que c'est en réalité un 50/50 à nos
		/// pieds — adversaire ET nous à moins de <c>FiftyChallengeRange</c> de la balle, balle basse
		/// (dribble sol) et nous GOAL-SIDE (challenge sain, anti-CSC) — on déclenche un <c>Fifty</c>
		/// pour disputer la balle au lieu de reculer. Le <c>Fifty</c> restant interruptible, si la
		/// prédiction bascule vers un tir cadré imparable on repasse en save au tick suivant.</para>
		/// <para>Réglages dans Bot.cs : <c>FiftyChallengeRange</c>, <c>ChallengeMaxBallHeight</c>.</para></summary>
		public static bool ChallengeOverDriveSave = true;

		/// <summary>DEBUG — Trace un tir en cours, 10 fois par seconde, jusqu'à sa fin.
		/// <para>Sert à départager POURQUOI un tir rate, sans supposer : mauvaise cible, arrivée
		/// trop tardive, ou prédiction de balle qui dérive. Voir MyBot.TraceShot pour la lecture
		/// des colonnes. À remettre à false une fois le diagnostic terminé.</para></summary>
		public static bool DebugShot = true;

		/// <summary>Feature — Sortie de rotation (action <see cref="Rotate"/>).
		/// <para>Sans ce flag, comportement d'origine : après avoir engagé un contest ou un tir, le bot
		/// repasse Support et se replace par <c>Arrive→BackupPos</c> / <c>Cover</c> — cible recalculée
		/// à chaque tick depuis la balle, donc corrections de direction permanentes. Il ne tient
		/// jamais une vitesse.</para>
		/// <para>Avec le flag, et <b>si le coéquipier est bien replacé goal-side</b> (sinon on garde la
		/// trajectoire courte classique, on est le dernier recours), on sort en rotation : tout droit
		/// d'abord, puis un gros pad du côté OPPOSÉ au contest s'il s'aborde à moins de 45°, en y
		/// arrivant orienté vers l'extérieur/notre but, puis un arc de cercle vers le replacement.
		/// La trajectoire est décidée UNE fois et tenue — c'est ce qui permet de garder la vitesse.</para>
		/// <para>Réglages dans Rotate.cs : <c>ArcMaxAngle</c> (l'ouverture de l'arc, le réglage
		/// principal), <c>PadEntryMaxAngle</c>, <c>PadExitOutward</c>, <c>ArcLookAhead</c>.</para></summary>
		public static bool RotationMode = true;

		/// <summary>DEBUG — Trace l'action <see cref="Rotate"/> (10x/s + rendu 3D), pour MyBot.
		/// <para>Console : phase (PAD / ARC), vitesse, boost, écart de cap vers la destination, distance
		/// restante, pad visé. Rendu : ligne orange = destination, jaune = pad. Sert à régler
		/// <c>ArcMaxAngle</c> : si la vitesse chute dans la phase ARC, l'arc est trop serré.</para></summary>
		public static bool DebugRotation = true;

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
