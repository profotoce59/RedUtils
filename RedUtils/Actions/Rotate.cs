using System;
using System.Collections.Generic;
using System.Drawing;
using RedUtils.Math;

namespace RedUtils
{
	/// <summary>
	/// Sortie de rotation : se replacer <b>sans jamais casser la vitesse</b>, après avoir engagé un
	/// contest ou un tir. C'est un déplacement, pas une manœuvre : tout ce qui coûte de la vitesse
	/// (freiner pour tourner, frein à main, demi-tour) est banni.
	///
	/// <para><b>Pourquoi pas un simple Drive vers la position de repli.</b> Drive vise le point, et
	/// s'il ne peut pas le prendre au rayon courant il <b>ralentit pour tourner plus serré</b>
	/// (Drive.cs, branche « slow down to turn sharper »). Sur un replacement c'est le mauvais
	/// arbitrage : la destination est une zone, pas une cible — mieux vaut passer large à pleine
	/// vitesse. D'où <see cref="Drive.PreserveSpeed"/>, activé ici.</para>
	///
	/// <para><b>Le trajet</b>, dans l'ordre :</para>
	/// <list type="number">
	/// <item><b>LANCEMENT — prendre de la vitesse d'abord.</b> On ne vise PAS l'objectif : on va
	/// presque droit devant (cône <see cref="LaunchMaxAngle"/>), infléchi assez pour s'en rapprocher
	/// mais pas assez pour pointer dessus. Deux raisons : tourner tôt casse la vitesse, et surtout
	/// viser l'objectif refermerait la fenêtre d'alignement que Drive exige pour speedflipper — le
	/// bot ne flipperait jamais. On sort de cette phase à <see cref="LaunchExitSpeed"/>.</item>
	/// <item><b>Gros pad du côté opposé</b>, et seulement s'il s'aborde à moins de
	/// <see cref="PadEntryMaxAngle"/>. Au-delà, le virage coûte plus que le boost ne rapporte : la
	/// table mesurée de Movement.cs facture 0,165 s à 90° et 0,745 s à 135°, contre 0,029 s à 45°.
	/// On y arrive orienté <b>vers l'extérieur du terrain, vers notre but</b>
	/// (<see cref="PadExitDirection"/>), pour enchaîner sans casser l'angle. Le speedflip vient
	/// tout seul : Drive le déclenche quand il reste du boost et qu'on est aligné.</item>
	/// <item><b>Arc de cercle</b> vers la destination — on ne vise jamais la cible directement, on
	/// borne le braquage à <see cref="ArcMaxAngle"/> et on se réoriente au fur et à mesure.</item>
	/// </list>
	///
	/// <para>Le pad est choisi <b>une seule fois</b>, à la construction. Re-décider à chaque tick est
	/// précisément ce qui empêchait le bot de tenir une vitesse.</para>
	/// </summary>
	public class Rotate : IAction
	{
		public bool Finished { get; private set; }
		public bool Interruptible { get; private set; }

		/// <summary>Destination du replacement. Mutable : la stratégie la suit sans recréer l'action.</summary>
		public Vec3 FinalTarget;

		/// <summary>Le pad visé, ou null si aucun ne s'abordait proprement.</summary>
		public Boost Pad { get; private set; }

		private readonly Goal _ourGoal;
		private readonly Drive _drive;
		private bool _padDone;
		/// <summary>Le pad a-t-il déjà été DEVANT nous ? Sans ça, « il est derrière » ne distingue pas
		/// « je l'ai dépassé » de « je n'ai pas encore tourné vers lui » — et au départ d'une rotation
		/// c'est toujours le second cas.</summary>
		private bool _padWasAhead;
		/// <summary>Phase de mise en vitesse, avant de commencer à vraiment tourner. Vrai au départ.</summary>
		private bool _launching = true;
		private float _lastDebug = -1f;
		/// <summary>Pourquoi aucun pad n'a été retenu. Répété à CHAQUE ligne de debug : le choix se fait
		/// une seule fois, à la construction, donc une trace unique se perd dans le défilement.</summary>
		private readonly string _padReport;

		/// <summary>
		/// Rallongement maximal du retour toléré pour aller chercher un pad. <b>Filtre de rejet
		/// uniquement</b> — le tri se fait sur la distance (voir <see cref="ChoosePad"/>).
		/// <para>Généreux à dessein : une rotation traverse VOLONTAIREMENT vers le côté opposé, le
		/// détour EST la manœuvre, pas un accident. Il suffit à écarter les pads absurdes — mesuré
		/// depuis la moitié adverse : pad du milieu opposé 4437, pad arrière opposé 4611, pad du coin
		/// ADVERSE 6539 (le seul qu'on veuille refuser).</para>
		/// <para><b>Deux filtres d'angle ont été essayés ici et retirés</b>, tous deux nés d'une
		/// mauvaise lecture. (1) L'angle entre le cap de sortie de contest et le pad : en sortie on
		/// pointe encore vers le but adverse, donc TOUT pad était à ~180°. (2) Le virage AU pad
		/// (arrivée vs départ vers la destination) : mesuré à 104-129°, il rejetait tout aussi.
		/// Or aller large est le PRINCIPE de la rotation — exiger que le pad soit sur la ligne droite
		/// du retour la contredit. Les « moins de 45° » décrivent le CAP D'ARRIVÉE sur le pad, ce que
		/// <see cref="PadExitDirection"/> produit déjà (~31° vers l'extérieur), pas un critère de tri.</para>
		/// </summary>
		private const float PadMaxDetour = 6000f;
		/// <summary>
		/// Ouverture de l'arc : braquage maximal demandé d'un coup. Plus c'est petit, plus la
		/// trajectoire est large et la vitesse préservée — au prix d'un retour plus long.
		/// <b>C'est LE réglage à ajuster.</b>
		/// <para>Passé de 0,6 à 0,45 : à 0,6 l'écart de cap demandé suffisait à saturer le braquage,
		/// donc la voiture tournait au rayon MINIMAL et grattait de la vitesse en permanence
		/// (mesuré : 2272 → 2028 sur l'approche du pad, à plat, sans obstacle).</para>
		/// </summary>
		private const float ArcMaxAngle = 0.50f;         // ~26°

		/// <summary>
		/// Ouverture pendant le LANCEMENT : on va presque droit devant, en s'infléchissant très
		/// doucement vers l'objectif.
		/// <para>Volontairement sous la fenêtre d'alignement de 0,1 rad que <c>Drive</c> exige pour
		/// déclencher un speedflip : viser directement le pad refermerait cette fenêtre et le bot ne
		/// flipperait jamais. On prend donc de la vitesse sur une ligne quasi droite qui amène
		/// <i>près</i> de l'objectif sans pointer dessus — l'arc s'occupe du reste.</para>
		/// </summary>
		private const float LaunchMaxAngle = 0.08f;      // ~4.6°
		/// <summary>
		/// Vitesse à partir de laquelle le lancement a fait son travail : on peut tourner, le rayon de
		/// virage est de toute façon énorme à cette allure. <b>Seul critère de sortie</b> avec le
		/// garde-fou de terrain.
		/// <para>Il n'y a délibérément <b>pas</b> de sortie sur l'angle à l'objectif. Une version
		/// précédente sortait au-delà de 60° : mesuré, l'objectif est à ~137° au départ d'une rotation,
		/// donc le lancement s'arrêtait à la première frame. C'est un contresens — un objectif très
		/// désaxé est précisément la situation pour laquelle le lancement existe.</para>
		/// </summary>
		private const float LaunchExitSpeed = 2000f;
		/// <summary>Garde-fou : on regarde ce point devant nous pour ne pas foncer dans un mur en
		/// tenant le cap. S'il sort du terrain, on arrête le lancement et on tourne.</summary>
		private const float LaunchLookAhead = 1500f;
		/// <summary>Marge au bord du terrain sous laquelle le lancement s'arrête.</summary>
		private const float LaunchFieldMargin = 500f;
		/// <summary>Distance de visée du point d'arc devant la voiture.</summary>
		private const float ArcLookAhead = 1500f;
		/// <summary>Combien on penche la sortie du pad vers l'extérieur du terrain (0 = plein vers le but).</summary>
		private const float PadExitOutward = 0.6f;
		/// <summary>Rayon auquel le pad est considéré pris.</summary>
		private const float PadReachedDistance = 250f;
		/// <summary>Rayon auquel le replacement est considéré fait.</summary>
		private const float ArrivedDistance = 400f;

		/// <summary>Crée une sortie de rotation.</summary>
		/// <param name="finalTarget">Où l'on se replace (poste de dernier homme / position de soutien).</param>
		/// <param name="rotationSide">Signe en x du côté par lequel tourner — l'OPPOSÉ de celui du contest.</param>
		public Rotate(Car car, Vec3 finalTarget, int rotationSide, Goal ourGoal)
		{
			Finished = false;
			Interruptible = true;

			FinalTarget = finalTarget;
			_ourGoal = ourGoal;

			Pad = ChoosePad(car, finalTarget, rotationSide, out _padReport);
			_padDone = Pad == null;

			// wasteBoost: le boost est ce qui permet de tenir la vitesse — et c'est aussi ce qui
			// autorise Drive à déclencher un speedflip (il l'exige, cf. Drive.cs).
			_drive = new Drive(car, Pad?.Location ?? finalTarget, Car.MaxSpeed, allowDodges: true, wasteBoost: true);
			_drive.PreserveSpeed = true;
		}

		public void Run(RUBot bot)
		{
			// Une rotation ne se fait JAMAIS en marche arrière. Drive fige ce choix à la construction ;
			// on le force ici, la voiture pouvant être lente juste après un contact.
			_drive.Backwards = false;
			_drive.TargetSpeed = Car.MaxSpeed;
			_drive.AllowDodges = true;

			Vec3 forward = bot.Me.Forward.FlatNorm();

			if (!_padDone && Pad != null)
			{
				// « Dépassé » ne veut dire quelque chose QUE si le pad a d'abord été devant nous.
				// Au départ d'une rotation il est toujours derrière (on sort face au but adverse) :
				// tester seulement « est-il derrière ? » l'abandonnait dès la première frame.
				bool padAhead = forward.Dot(bot.Me.Location.FlatDirection(Pad.Location)) > 0f;
				if (padAhead)
					_padWasAhead = true;

				if (bot.Me.Location.FlatDist(Pad.Location) < PadReachedDistance
					|| (_padWasAhead && !padAhead))
				{
					_padDone = true;
				}
			}

			Vec3 objective = !_padDone && Pad != null ? Pad.Location : FinalTarget;
			float speed = bot.Me.Velocity.FlatLen();

			// Fin du lancement : la vitesse est faite, ou continuer tout droit nous sortirait du
			// terrain. PAS de sortie sur l'angle à l'objectif — voir LaunchExitSpeed.
			if (_launching
				&& (speed >= LaunchExitSpeed
					|| !Field.InField(bot.Me.Location + forward * LaunchLookAhead, LaunchFieldMargin)))
			{
				_launching = false;
			}

			string phase;
			if (_launching)
			{
				// LANCEMENT — on ne vise PAS l'objectif : on vise presque droit devant, infléchi d'au
				// plus LaunchMaxAngle. On PREND DE LA VITESSE D'ABORD, quitte à passer à côté du pad ;
				// c'est l'arc qui rattrapera. Ce cône très serré est aussi ce qui garde ouverte la
				// fenêtre d'alignement (0.1 rad) que Drive exige pour déclencher un speedflip — viser
				// directement le pad la refermerait, et le bot ne flipperait jamais.
				_drive.Target = ClampedWaypoint(bot.Me, objective, LaunchMaxAngle);
				_drive.ExitDirection = Vec3.Zero;
				phase = "LANCEMENT";
			}
			else if (!_padDone && Pad != null)
			{
				// Borné comme l'arc : viser le pad EN DIRECT demandait un demi-tour d'un coup quand il
				// est très désaxé (mesuré : 160° au départ → vitesse tombée de 2291 à 1066). On ne
				// demande jamais plus d'ArcMaxAngle, ici comme ailleurs.
				Vec3 waypoint = ClampedWaypoint(bot.Me, Pad.Location, ArcMaxAngle);
				_drive.Target = waypoint;
				// La direction de sortie ne vaut que si l'on vise VRAIMENT le pad : appliquée à un
				// point d'arc intermédiaire, elle décalerait une cible qui n'est pas la bonne.
				// ClampedWaypoint renvoie la cible telle quelle quand elle est dans le cône.
				_drive.ExitDirection = waypoint.Dist(Pad.Location) < 1f
					? PadExitDirection(Pad, _ourGoal)
					: Vec3.Zero;
				phase = "PAD";
			}
			else
			{
				// ARC : on vise un point à ArcMaxAngle du cap actuel, pas la destination. En se
				// réalignant tick après tick, la voiture décrit un arc au lieu de braquer d'un coup.
				_drive.Target = ClampedWaypoint(bot.Me, FinalTarget, ArcMaxAngle);
				_drive.ExitDirection = Vec3.Zero;
				phase = "ARC";
			}

			_drive.Run(bot);
			Interruptible = _drive.Interruptible;
			//Debug(bot, phase);

			if (bot.Me.Location.FlatDist(FinalTarget) < ArrivedDistance)
				Finished = true;
		}

		/// <summary>
		/// Point à viser pour ne jamais demander plus de <paramref name="maxAngle"/> de braquage : la
		/// cible si elle est déjà dans le cône, sinon un point à <see cref="ArcLookAhead"/> dans la
		/// direction bornée à ce cône. En se réalignant tick après tick, la voiture décrit une courbe
		/// au lieu de braquer d'un coup.
		/// <para>Un seul mécanisme pour les deux phases, à deux ouvertures près : très serré au
		/// LANCEMENT (on va presque droit, on prend de la vitesse, le speedflip reste possible), plus
		/// ouvert dans l'ARC (on rattrape la destination).</para>
		/// </summary>
		private static Vec3 ClampedWaypoint(Car car, Vec3 target, float maxAngle)
		{
			Vec3 forward = car.Forward.FlatNorm();
			Vec3 desired = car.Location.FlatDirection(target);

			if (forward.FlatAngle(desired) <= maxAngle)
				return target;

			// Rotate est anti-horaire : +angle = gauche. Clamp attend (start, end) dans l'ordre
			// horaire, comme Arrive.cs — donc (gauche, droite).
			Vec3 clamped = desired.Clamp(forward.Rotate(maxAngle), forward.Rotate(-maxAngle), Vec3.Up);
			return car.Location + clamped.FlatNorm() * ArcLookAhead;
		}

		/// <summary>Direction du nez voulue en prenant le pad : vers notre but, penchée vers l'extérieur.</summary>
		private static Vec3 PadExitDirection(Boost pad, Goal ourGoal)
		{
			Vec3 toGoal = pad.Location.FlatDirection(ourGoal.Location);
			Vec3 outward = new Vec3(MathF.Sign(pad.Location.x), 0f, 0f);
			return (toGoal + outward * PadExitOutward).FlatNorm();
		}

		/// <summary>
		/// Gros pad du côté de rotation. On garde <b>le plus PROCHE</b> ; le détour ne sert qu'à
		/// écarter les absurdités (pad du coin adverse, qui enfonce dans leur camp).
		///
		/// <para><b>Pourquoi la distance et non le détour.</b> Trier par détour élisait le pad du
		/// FOND, presque sur le chemin du retour — mais atteint tout à la fin. Mesuré : 5,6 s de
		/// trajet à 0 boost, vitesse tombée à 1066, et le pad touché à 3078 de l'arrivée, quand il ne
		/// sert plus à rien. Ce qui compte n'est pas le coût en distance du détour mais <b>le moment
		/// où le boost arrive</b> : pris tôt, il finance tout le reste de la rotation ; pris à la fin,
		/// il ne finance rien. Le pad le plus proche est donc le bon, et le détour n'est plus qu'un
		/// garde-fou (mesuré sur ce cas : milieu 5295 et fond 4865 doivent passer, coin adverse 8453
		/// doit être écarté).</para>
		/// <para>Chaque refus est tracé (<c>Fixes.DebugRotation</c>) : quand aucun pad ne convient, la
		/// raison est imprimée, plutôt que d'avoir à la deviner d'un <c>pad=-</c>.</para>
		/// </summary>
		private static Boost ChoosePad(Car car, Vec3 finalTarget, int rotationSide, out string report)
		{
			float directDistance = car.Location.Dist(finalTarget);

			Boost best = null;
			float bestDistance = float.MaxValue;
			List<string> notes = new List<string>();
			int candidates = 0;

			foreach (Boost pad in Field.Boosts)
			{
				if (!pad.IsLarge)
					continue;

				// Côté imposé : on tourne par l'opposé du contest
				if (MathF.Sign(pad.Location.x) != rotationSide)
					continue;

				candidates++;
				string id = $"({pad.Location.x:F0},{pad.Location.y:F0})";

				float distance = car.Location.Dist(pad.Location);
				// Rallongement du retour : aller au pad puis à la destination, contre y aller direct.
				// Garde-fou seulement — écarte le pad du coin adverse, pas le tri principal.
				float detour = distance + pad.Location.Dist(finalTarget) - directDistance;

				if (detour > PadMaxDetour)
				{
					notes.Add($"{id} detour {detour:F0}>{PadMaxDetour:F0}");
					continue;
				}
				if (!pad.IsActive && pad.TimeUntilActive > Movement.EtaFor(car, pad.Location))
				{
					notes.Add($"{id} recharge {pad.TimeUntilActive:F1}s");
					continue;
				}

				// Le plus PROCHE : c'est le boost pris TÔT qui finance la rotation.
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = pad;
				}
			}

			report = best != null
				? ""
				: candidates == 0
					? $"aucun gros pad cote x={rotationSide:+0;-0}"
					: string.Join(" ; ", notes);
			return best;
		}

		/// <summary>Trace de réglage (Fixes.DebugRotation) : sert surtout à ajuster ArcMaxAngle.</summary>
		private void Debug(RUBot bot, string phase)
		{
			if (!Fixes.DebugRotation)
				return;

			bot.Renderer.Line3D(FinalTarget, FinalTarget + new Vec3(0f, 0f, 200f), Color.Orange);
			if (Pad != null && !_padDone)
				bot.Renderer.Line3D(Pad.Location, Pad.Location + new Vec3(0f, 0f, 200f), Color.Yellow);

			if (Game.Time - _lastDebug < 0.1f || bot.Me.Name != "MyBot")
				return;
			_lastDebug = Game.Time;

			float capErr = bot.Me.Forward.FlatAngle(bot.Me.Location.FlatDirection(FinalTarget)) * 180f / MathF.PI;
			// La raison du refus est répétée à chaque ligne : le choix du pad a lieu une seule fois,
			// à la construction, et une trace unique se noie dans le défilement de la console.
			string pad = Pad != null
				? $"({Pad.Location.x:F0},{Pad.Location.y:F0})"
				: $"AUCUN [{_padReport}]";
			Console.WriteLine($"[{Game.Time:F1}s][Rotate] {phase} v={bot.Me.Velocity.FlatLen():F0} " +
				$"boost={bot.Me.Boost:F0} capErr={capErr:F0}° distFinal={bot.Me.Location.FlatDist(FinalTarget):F0} " +
				$"pos=({bot.Me.Location.x:F0},{bot.Me.Location.y:F0}) dest=({FinalTarget.x:F0},{FinalTarget.y:F0}) pad={pad}");
		}
	}
}
