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

	}
}
