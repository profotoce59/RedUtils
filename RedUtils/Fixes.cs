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

		/// <summary>DEBUG — Trace frame par frame le déclenchement du JumpShot quand la voiture est
		/// sur un mur (normal non verticale). Imprime timeRemaining/timeToJump/eta/alignement et la
		/// raison (WAIT / JUMP / ABORT-xxx). À mettre à false une fois le diagnostic terminé.</summary>
		public static bool DebugWallJumpShot = false;

	}
}
