using System.Collections.Generic;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>An action for driving to grab a boost pad</summary>
	public class GetBoost : IAction
	{
		/// <summary>Whether or not this action is finished</summary>
		public bool Finished { get; set; }
		/// <summary>Whether or not this action can be interrupted</summary>
		public bool Interruptible { get; set; }

		/// <summary>The index of the boost pad we are going to grab. -1 when no pad was selected.</summary>
		public int BoostIndex = -1;
		/// <summary>This action's drive subaction. Null when no pad was selected.</summary>
		public Drive DriveAction;
		/// <summary>The pad we are driving to. <b>Null when no pad was selected</b> — always test <see cref="Found"/> first.</summary>
		public Boost ChosenBoost;
		public float Eta = 0;

		/// <summary>
		/// Vrai si un pad utilisable a effectivement été retenu.
		///
		/// <para>Correctif (AUDIT §0.1) : la version d'origine n'avait pas ce cas. Si aucun pad ne
		/// satisfaisait <c>IsActive || TimeUntilActive &lt; eta</c>, l'index restait à sa valeur
		/// initiale — <b>-1</b> dans le constructeur par index (<c>Field.Boosts[-1]</c> →
		/// <c>IndexOutOfRangeException</c>), <b>0</b> dans le constructeur par liste (le bot partait
		/// silencieusement chercher le pad d'index 0, à l'autre bout du terrain). Les deux cas sont
		/// atteignables en jeu : la liste goal-side du Support peut ne contenir que des pads en
		/// cooldown.</para>
		///
		/// <para>Quand ce champ est faux, l'action est déjà <see cref="Finished"/> et ne fait rien :
		/// l'appelant doit tester <c>Found</c> et choisir autre chose plutôt que de l'assigner.</para>
		/// </summary>
		public bool Found => BoostIndex >= 0;

		/// <summary>Whether or not this action was initially set as interruptible</summary>
		private readonly bool _initiallyInterruptible = true;

		/// <summary>Initializes a GetBoost.</summary>
		/// <param name="boostIndex">Index of boost pad to go for. If set to -1 it will attempt to find the best big boost pad automatically</param>
		/// <param name="interruptible">Whether or not this shot can be interrupted</param>
		public GetBoost(Car car, int boostIndex = -1, bool interruptible = true)
		{
			Finished = false;
			Interruptible = interruptible;
			_initiallyInterruptible = interruptible;

			if (boostIndex == -1)
			{
				List<Boost> largePads = new List<Boost>();
				foreach (Boost boost in Field.Boosts)
					if (boost.IsLarge)
						largePads.Add(boost);
				boostIndex = PickBoost(car, largePads);
			}

			Setup(car, boostIndex);
		}

		/// <summary>Initializes a GetBoost action which will go for the soonest reachable boost of the supplied boosts</summary>
		/// <param name="boosts">Which boosts to consider</param>
		/// <param name="interruptible">Whether or not this action can be interrupted</param>
		public GetBoost(Car car, IEnumerable<Boost> boosts, bool interruptible = true)
		{
			Finished = false;
			Interruptible = interruptible;
			_initiallyInterruptible = interruptible;

			Setup(car, PickBoost(car, boosts));
		}

		/// <summary>Soonest reachable pad among the candidates, or -1 if none is usable.</summary>
		private static int PickBoost(Car car, IEnumerable<Boost> boosts)
		{
			int bestIndex = -1;
			float fastestEta = float.MaxValue;

			foreach (Boost boost in boosts)
			{
				// Calculates how long it will take to get the boost
				float eta = Drive.GetEta(car, boost.Location);
				// If we can get there fastest, and it will be active when we get there, we choose it as our new fastest!
				if (eta < fastestEta && (boost.IsActive || boost.TimeUntilActive < eta))
				{
					fastestEta = eta;
					bestIndex = boost.Index;
				}
			}

			return bestIndex;
		}

		/// <summary>Latches the chosen pad, or finishes the action right away when there is none.</summary>
		private void Setup(Car car, int boostIndex)
		{
			BoostIndex = boostIndex;

			if (!Found)
			{
				// Aucun pad utilisable : on ne fabrique ni cible ni Drive, et l'action se termine
				// immédiatement. Elle ne peut plus rien piloter, donc elle reste interruptible.
				ChosenBoost = null;
				DriveAction = null;
				Interruptible = true;
				Finished = true;
				return;
			}

			ChosenBoost = Field.Boosts[BoostIndex];
			DriveAction = new Drive(car, ChosenBoost.Location, Car.MaxSpeed, true, ChosenBoost.IsLarge);
		}

		/// <summary>Drives to the chosen boost pad</summary>
		public void Run(RUBot bot)
		{
			if (!Found)
			{
				Finished = true;
				return;
			}

			// Drive to the boost
			DriveAction.Run(bot);

			// Gets info on the chosen boost
			ChosenBoost = Field.Boosts[BoostIndex];
			Eta = Drive.GetEta(bot.Me, ChosenBoost.Location);

			// This action can only be interrupted if it was initially set as interruptuble, and if its sub action is also interruptible
			Interruptible = _initiallyInterruptible && DriveAction.Interruptible;
			// When we arrive at the boost's location, we finish this action
			Finished = (!ChosenBoost.IsActive && (ChosenBoost.TimeUntilActive > Eta || DriveAction.Finished)) || bot.Me.Boost > 90;
		}
	}
}
