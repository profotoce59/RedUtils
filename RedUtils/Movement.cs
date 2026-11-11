using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>Décomposition d'un ETA, pour voir quelle phase dérape au banc.</summary>
    public struct EtaBreakdown
    {
        public float Landing;   // attente avant de toucher le sol
        public float Brake;     // freinage quand on s'éloigne de la cible
        public float Drive;     // trajet proprement dit
        public float Turn;      // surcoût de virage
        public float Flip;      // surcoût de flip
        public float Distance;  // distance retenue (freinage inclus)
        public float Total;

        public override string ToString() =>
            $"total={Total:F3} [sol={Landing:F3} frein={Brake:F3} route={Drive:F3} " +
            $"virage={Turn:F3} flip={Flip:F3} dist={Distance:F0}]";
    }

    /// <summary>
    /// Moteur de déplacement : « combien de temps pour aller d'ici à là ».
    ///
    /// <para>Toutes les constantes de ce fichier sont ÉTALONNÉES SUR MESURES, pas estimées.
    /// Le banc (<see cref="Fixes.EtaBench"/>, state_setting_tests_eta.py) produit les valeurs et
    /// ETA_MESURES.md les consigne avec leur méthode. Ne pas les modifier sans repasser le banc :
    /// c'est précisément le tâtonnement qu'on cherche à éliminer.</para>
    ///
    /// <para>L'ETA décrit ce que la voiture FAIT, pas un optimum théorique. Un flip la ralentit
    /// quand elle a du boost : le modèle doit le dire, sinon le bot s'engage sur des interceptions
    /// qu'il ne tiendra pas.</para>
    ///
    /// <para><b>DOMAINE DE VALIDITÉ — à lire avant d'ajouter un appelant.</b></para>
    ///
    /// <para>Le banc (<c>state_setting_tests_eta.py</c>) mesure <b>uniquement</b> des trajets
    /// <b>point à point au sol, sans contrainte d'orientation à l'arrivée</b> : la voiture part
    /// d'une pose donnée, roule vers un point, on chronomètre. C'est donc le seul cas où ce modèle
    /// est étalonné — et le seul où il bat <c>Drive.GetEta</c>.</para>
    ///
    /// <para>Un tir ne pose pas cette question. Il ne demande pas « quand puis-je être à ce
    /// point », mais « quand puis-je y être <b>en roulant dans la bonne direction</b> » — la phase
    /// d'alignement d'<see cref="Arrive"/> pèse alors une grande part du trajet.
    /// <c>Drive.GetEta(car, target, arrivalDirection)</c> modélise ce cas et se comporte bien en
    /// jeu. Une surcharge directionnelle a été tentée ici, en greffant la géométrie d'Arrive sur ce
    /// modèle : <b>mesurée en match, elle joue moins bien</b>. C'était une extrapolation hors du
    /// domaine mesuré — précisément ce que l'en-tête ci-dessus interdit. Elle a été retirée.</para>
    ///
    /// <para><b>Répartition actuelle, délibérée :</b></para>
    /// <list type="bullet">
    /// <item>Déplacement au sol vers un point — <c>Rotation.FirstReachableEta</c> /
    /// <c>ComputeScore</c> (possession, rôles), <c>MyBot.ContestPoint</c>,
    /// <c>MyBot.InterceptReachable</c> (interception de save) → <b>Movement</b>.</item>
    /// <item>Tout ce qui vise un contact orienté — <c>Shot.IsValid</c> des quatre mécaniques,
    /// <c>Fifty</c>, <c>Save</c>, <c>GetBoost</c>, <c>Arrive</c> → <b>Drive.GetEta</b>.</item>
    /// </list>
    ///
    /// <para>Ce partage était auparavant un accident d'emplacement (cette classe vivait dans
    /// <c>Bot/</c>, hors de portée des actions de RedUtils). Il est maintenant explicite et
    /// intentionnel. Pour étendre Movement à un nouveau cas : <b>étalonner ce cas au banc
    /// d'abord</b> — c'est la seule chose qui distingue ce fichier d'un réglage au jugé.</para>
    /// </summary>
    public static class Movement
    {
        // ---- Virage (ETA_MESURES.md, série A, 1500 uu depuis l'arrêt) ----
        // Surcoût mesuré par rapport à la même distance en ligne droite. Interpolation linéaire
        // entre les points : aucune loi inventée, on ne prolonge pas au-delà de ce qui est mesuré.
        // La géométrie d'origine (arc / SpeedFromTurnRadius) facturait 0.844 s à 90° contre
        // 0.165 s réellement observé — cinq fois trop.
        private static readonly float[] TurnAngles = { 0f, 0.524f, 0.785f, 1.047f, 1.571f, 2.356f };
        private static readonly float[] TurnCosts  = { 0f, 0.013f, 0.029f, 0.051f, 0.165f, 0.745f };

        // Au-delà, la voiture fait demi-tour en marche arrière plutôt que de tourner.
        // Drive.GetEta gère déjà ce cas correctement (mesure A6 : +2 %), on lui délègue.
        private const float ReverseAngle = 2.6f;   // ~150°

        // ---- Flip (ETA_MESURES.md, séries D/N) ----
        // Coût mesuré deux fois au millième près par différence avec/sans dodge à distance égale :
        // 2501 uu et 3001 uu donnent tous deux +0.207 s. Le flip coûte doublement — du temps en
        // l'air, et le boost qu'on ne consomme pas pendant ce temps.
        private const float FlipCostAtRest = 0.207f;
        // Le surcoût décroît avec la vitesse d'entrée et s'annule là où Drive cesse de flipper
        // (garde carSpeed < 2000 dans Drive.cs). Recoupé par V1 : entrée à 991 uu/s -> 0.118 s
        // mesuré, 0.104 s prédit par cette droite.
        private const float FlipNeutralSpeed = 2000f;
        // Drive ne flippe pas en dessous : flips=0 à 2001 uu, flips=1 à 2501 uu.
        private const float FlipMinDistance = 2250f;

        /// <summary>
        /// Un flip ne coûte du temps que si la voiture avait du boost à dépenser à la place.
        /// À court de boost il devient au contraire le seul moyen d'accélérer — mesures C1
        /// (24 de boost, le flip fait GAGNER 6 %) contre D5 (100 de boost, il fait perdre 13 %).
        /// </summary>
        private const float FlipBoostCoverage = 0.5f;

        /// <summary>
        /// Le contrôleur ne conduit jamais parfaitement : corrections de direction, throttle
        /// proportionnel, alignement imparfait. Quinze mesures sans flip donnaient toutes le même
        /// biais, entre +1,5 % et +3,4 %, TOUJOURS dans le sens « la réalité est plus lente ».
        ///
        /// <para>Corriger ce biais compte plus qu'il n'y paraît : sans lui le modèle est optimiste,
        /// et un ETA optimiste fait s'engager le bot sur des interceptions hors de portée — c'est
        /// exactement le défaut qu'on traque. Avec, l'erreur résiduelle bascule du côté pessimiste,
        /// qui coûte au pire un tir refusé.</para>
        /// </summary>
        private const float ControllerLoss = 1.022f;

        /// <summary>Temps estimé pour rejoindre la cible, en conduisant comme Drive le fait.</summary>
        public static float Eta(Car car, Vec3 target) => Eta(car, target, out _);

        /// <summary>
        /// Point d'entrée des appelants « déplacement au sol vers un point » : moteur étalonné ou
        /// <see cref="Drive.GetEta"/> selon <see cref="Fixes.MovementEngine"/>, pour comparer les
        /// deux sur un même scénario sans toucher aux appelants.
        ///
        /// <para>Il n'existe volontairement <b>pas</b> de surcharge à direction d'arrivée : ce cas
        /// n'est pas étalonné ici et appartient à <c>Drive.GetEta</c>. Voir « domaine de validité »
        /// en tête de classe.</para>
        /// </summary>
        public static float EtaFor(Car car, Vec3 target)
            => Fixes.MovementEngine ? Eta(car, target) : Drive.GetEta(car, target);

        /// <summary>Idem, en exposant le détail par phase (banc d'étalonnage).</summary>
        public static float Eta(Car car, Vec3 target, out EtaBreakdown b)
        {
            b = default;

            Vec3 startPos = car.PredictLandingPosition();
            b.Landing = car.PredictLandingTime();

            Vec3 surfaceNormal = car.IsGrounded
                ? Field.NearestSurface(car.Location).Normal
                : Field.FindLandingSurface(car).Normal;
            Vec3 forward = car.IsGrounded
                ? car.Forward
                : (car.Velocity.FlatLen() > 500 ? car.Velocity.FlatNorm(surfaceNormal) : startPos.FlatDirection(target, surfaceNormal));

            float angle = forward.FlatAngle(startPos.FlatDirection(target, surfaceNormal), surfaceNormal);
            angle = MathF.Abs(angle);

            // Demi-tour : la voiture recule au lieu de tourner. Cas déjà fidèle dans Drive.
            if (angle > ReverseAngle)
            {
                b.Total = Drive.GetEta(car, target);
                b.Drive = b.Total;
                b.Distance = Field.DistanceBetweenPoints(startPos, target);
                return b.Total;
            }

            float distance = Field.DistanceBetweenPoints(startPos, target);
            float speed = forward.Dot(car.Velocity);

            // --- Freinage : la voiture s'éloigne de sa cible ---
            // Sans ce terme le modèle écrasait la vitesse négative à zéro et traitait la voiture
            // comme à l'arrêt, alors qu'elle doit d'abord s'arrêter ET rattraper le terrain perdu.
            // C'était la plus grosse erreur mesurée : V4, +34 %.
            if (speed < 0f)
            {
                b.Brake = -speed / Car.BrakeAccel;
                distance += speed * speed / (2f * Car.BrakeAccel);
                speed = 0f;
            }

            b.Distance = distance;
            b.Drive = Drive.TimeToCoverDistance(speed, car.Boost, distance);
            b.Turn = TurnCost(angle);
            b.Flip = FlipCost(distance, speed, car.Boost, b.Drive);

            b.Total = (b.Landing + b.Brake + b.Drive + b.Turn + b.Flip) * ControllerLoss;
            return b.Total;
        }

        /// <summary>Surcoût de virage, interpolé entre les angles mesurés.</summary>
        private static float TurnCost(float angle)
        {
            if (angle <= TurnAngles[0])
                return TurnCosts[0];

            for (int i = 1; i < TurnAngles.Length; i++)
            {
                if (angle <= TurnAngles[i])
                {
                    float t = (angle - TurnAngles[i - 1]) / (TurnAngles[i] - TurnAngles[i - 1]);
                    return Utils.Lerp(t, TurnCosts[i - 1], TurnCosts[i]);
                }
            }

            // Entre le dernier point mesuré et ReverseAngle : on tient la dernière valeur connue
            // plutôt que d'extrapoler une courbe très raide hors de son domaine de validité.
            return TurnCosts[TurnCosts.Length - 1];
        }

        /// <summary>
        /// Surcoût du flip, quand Drive va en déclencher un et qu'il aurait mieux valu booster.
        /// </summary>
        private static float FlipCost(float distance, float startSpeed, float boost, float driveTime)
        {
            if (distance < FlipMinDistance || startSpeed >= FlipNeutralSpeed)
                return 0f;

            // À court de boost, le flip est le seul moyen de gagner de la vitesse : pas de pénalité.
            float boostSeconds = boost / Car.BoostConsumption;
            if (boostSeconds < driveTime * FlipBoostCoverage)
                return 0f;

            return FlipCostAtRest * MathF.Max(1f - startSpeed / FlipNeutralSpeed, 0f);
        }
    }
}
