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

	}
}
