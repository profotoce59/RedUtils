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
		/// <summary>Fix #1 — Field.Initialize : vide la liste des boost pads avant de la remplir.
		/// <para>Sans ce fix, en 2v2 avec deux RedBots dans le même process, la liste est doublée (68 pads)
		/// et les doublons restent "actifs" pour toujours → GetBoost cible des pads fantômes.</para>
		/// <para>Vérification : au lancement, la console affiche "[RedUtils] Field initialized: N boost pads".
		/// Avec fix : toujours 34. Sans fix : 68 quand le 2e bot rejoint.</para></summary>
		public static bool FieldInitClearBoosts = false;

		/// <summary>Fix #2 — JumpShot.ReadyToJump : remplace la division entière (1 / 2) == 0 par 0.5f.
		/// <para>Sans ce fix, la compensation de gravité est nulle → timing de saut faux sur les murs.</para></summary>
		public static bool JumpShotGravityFix = false;

		/// <summary>Fix #3 — Utils.ShotPowerModifier : corrige le dénominateur du dernier segment
		/// pour que la courbe atteigne bien 0.30 à 4600 uu/s (cohérent avec RocketSim).
		/// <para>Sans ce fix, la puissance des tirs à haute vitesse relative est surestimée (min 0.425).</para></summary>
		public static bool ShotPowerModifierFix = false;

		/// <summary>Fix #4 — BallPrediction.Find : supprime les angles morts de la recherche par blocs
		/// (slice "ancre" retournée avec un bloc de retard, dernier bloc partiel jamais exploré).</summary>
		public static bool BallPredictionFindFix = false;

	}
}
