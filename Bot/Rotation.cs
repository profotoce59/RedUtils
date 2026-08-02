using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public enum Role { Attacker, Support }
    public enum GameStateMode { NotPossessed, Contested, Possessed }
    public enum FieldZone { Defensive, Offensive }

    public static class Rotation
    {
        // Seconds added to ETA when a player would have to shoot backwards
        private const float BackwardsAnglePenalty = 2.0f;
        // ~108°: angle between (car→ball) and (ball→their goal) above which we consider the shot backwards
        private static readonly float BackwardsAngleThreshold = MathF.PI * 0.6f;

        // Avantage de score requis pour prendre le rôle d'Attacker au titulaire (anti-clignotement)
        private const float RoleSwitchMargin = 0.3f;

        // Une voiture à moins de ça de la balle est « sur la balle » : elle est l'Attacker par simple
        // proximité, en court-circuitant Movement.EtaFor (qui gonfle à plusieurs secondes pour une
        // voiture en l'air, ex. un Fifty engagé). Aligné sur MyBot.FiftyChallengeRange (600u).
        private const float OnBallDistance = 600f;

        // Tolérance (uu) : un joueur reste « goal-side » jusqu'à GoalSideMargin PASSÉ la balle (vers
        // les adversaires). Au-delà il est ball-side (dépassé / raté) → Support. Évite de le déclasser
        // au contact, où il est légitimement en train de challenger la balle.
        //
        // Descendu de 200 à 100 : la marge doit rester SOUS la distance à laquelle le bot pousse la
        // balle. À 200, un bot en dribble/poussée (mesuré : 166-188 uu du ballon) ne pouvait
        // JAMAIS être vu ball-side — l'écart en y est borné par la distance totale, donc -170 > -200
        // restait vrai quel que soit le côté où il se trouvait. Il gardait le rôle d'Attacker en
        // poussant la balle vers notre propre but.
        private const float GoalSideMargin = 100f;

        /// <summary>
        /// Determines whether this car is the attacker or the support in a 2v2.
        /// Both bots call this independently each tick and will agree without shared state:
        /// the incumbent-keeps-role conditions below are exact complements of each other.
        /// </summary>
        public static Role ComputeRole(Car me, Car teammate, Goal theirGoal, Role? currentRole = null)
        {
            // Contrainte défensive : l'Attacker doit être GOAL-SIDE (entre la balle et notre but),
            // sauf si aucun des deux ne l'est. Ce gate PRIME sur le score.
            //
            // « goal-side » = pas clairement PASSÉ la balle : profondeur (position le long de y vers
            // notre but, relative à la balle) au-dessus de -GoalSideMargin. Un joueur qui aborde la
            // balle par l'arrière le reste jusqu'au contact (profondeur ~0) ; celui qui la DÉPASSE ou
            // la RATE (la balle repart vers notre but → profondeur franchement négative) devient
            // ball-side et PASSE Support, le coéquipier resté goal-side reprenant l'attaque. C'est
            // exactement le comportement voulu, donc surtout PAS de court-circuit « déjà engagé » ici.
            // Symétrique : les deux bots évaluent les mêmes tests sur les deux voitures → ils s'accordent.
            bool meGoalSide = IsGoalSide(me);
            bool teammateGoalSide = IsGoalSide(teammate);

            // Exactement un des deux est goal-side → c'est lui l'Attacker, quel que soit l'ETA.
            // Les deux le sont, ou aucun → on retombe sur la logique de score ci-dessous.
            if (meGoalSide != teammateGoalSide)
                return meGoalSide ? Role.Attacker : Role.Support;

            float myScore = ComputeScore(me, theirGoal);
            float teammateScore = ComputeScore(teammate, theirGoal);

            // Égalité parfaite (kickoff symétrique) : sans départage, chaque bot évalue
            // « mon score <= le sien » de son côté et les DEUX se croient Attacker. L'index tranche.
            if (myScore == teammateScore)
                return me.Index < teammate.Index ? Role.Attacker : Role.Support;

            // Hystérésis : le titulaire garde son rôle tant que l'autre ne le bat pas franchement.
            // Sinon l'estimateur en escalier fait clignoter les rôles et les deux bots
            // font demi-tour ensemble plusieurs fois par seconde.
            return currentRole switch
            {
                Role.Attacker => myScore <= teammateScore + RoleSwitchMargin ? Role.Attacker : Role.Support,
                Role.Support  => myScore + RoleSwitchMargin < teammateScore  ? Role.Attacker : Role.Support,
                _             => myScore < teammateScore                     ? Role.Attacker : Role.Support,
            };
        }

        /// <summary>
        /// La voiture n'a-t-elle pas clairement DÉPASSÉ la balle ? Vrai tant qu'elle est du côté de
        /// notre but, à <see cref="GoalSideMargin"/> près — donc encore vrai au contact (profondeur
        /// ~0), faux une fois la balle ratée ou dépassée. Base du gate de <see cref="ComputeRole"/>
        /// et du test « le coéquipier est-il replacé ? » avant une rotation.
        /// </summary>
        public static bool IsGoalSide(Car car)
        {
            int side = Field.Side(car.Team);
            return (car.Location.y - Ball.Location.y) * side > -GoalSideMargin;
        }

        /// <summary>
        /// Score = ETA to reach the ball + angle penalty if the shot would go backwards.
        /// Lower is better (Attacker = lowest score).
        /// </summary>
        private static float ComputeScore(Car car, Goal theirGoal)
        {
            // Déjà SUR la balle → Attacker, quoi qu'en dise l'ETA. Pour une voiture en l'air (Fifty /
            // challenge engagé), Movement.EtaFor gonfle à plusieurs secondes et la ferait passer
            // Support, envoyant le coéquipier doubler sur la balle. On la classe donc par simple
            // proximité : score minuscule, dominant, et SYMÉTRIQUE — les deux bots calculent le même
            // score pour les deux voitures, donc restent d'accord sans état partagé. Même parade que
            // ContestDistance dans ComputeGameState.
            float ballDist = car.Location.Dist(Ball.Location);
            if (ballDist < OnBallDistance)
                return ballDist / Car.MaxSpeed;

            // First ball slice this car can physically reach in time
            BallSlice commitSlice = Ball.Prediction.Find(slice =>
                Movement.EtaFor(car, slice.Location) <= slice.Time - Game.Time);

            if (commitSlice == null)
                return float.MaxValue;

            float eta = commitSlice.Time - Game.Time;

            // Penalize if the angle car→ball→theirGoal is too wide (would shoot backwards)
            Vec3 carToBall = commitSlice.Location - car.Location;
            Vec3 ballToGoal = theirGoal.Location - commitSlice.Location;
            float angle = carToBall.FlatAngle(ballToGoal);

            return eta + (angle > BackwardsAngleThreshold ? BackwardsAnglePenalty : 0f);
        }

        // Distance de repli goal-side de la balle
        private const float BackupDistance = 2500f;
        // Décalage latéral back post : se placer du côté du poteau opposé à la balle
        private const float BackPostOffset = 800f;
        // Largeur de la rampe du décalage back post autour de x=0 (voir BackupPosition)
        private const float BackPostRamp = 1200f;
        // Marge de sécurité avec les bords du terrain
        private const float FieldMargin = 400f;

        /// <summary>
        /// Ideal backup position: goal-side of the BALL (not the attacker, who passes his own
        /// placement mistakes down to us), shifted toward the back post, clamped to the field.
        /// Being on the far post keeps the two bots off the same line: one ball can't beat both.
        /// </summary>
        public static Vec3 BackupPosition(Goal ourGoal)
        {
            Vec3 toGoal = Ball.Location.FlatDirection(ourGoal.Location);
            Vec3 pos = Ball.Location + toGoal * BackupDistance;

            // Back post : décalage du côté opposé à la balle, en RAMPE.
            // Avec un Sign() la cible saute de 1600u dès que la balle frôle x=0 — au-delà du
            // seuil de re-ciblage (RetargetDistance), donc l'Arrive est recréé en boucle avec
            // une direction inversée : le Support tourne en rond au lieu de se placer.
            pos.x -= Utils.Cap(Ball.Location.x / BackPostRamp, -1f, 1f) * BackPostOffset;

            // Jamais hors terrain ni derrière notre ligne de but
            pos.x = Utils.Cap(pos.x, -Field.Width / 2f + FieldMargin, Field.Width / 2f - FieldMargin);
            pos.y = Utils.Cap(pos.y, -Field.Length / 2f + FieldMargin, Field.Length / 2f - FieldMargin);
            pos.z = 0f;
            return pos;
        }

        // ETA advantage required before claiming (or conceding) the ball
        private const float PossessionMargin = 0.4f;
        // If an opponent reaches the ball within this, it is a contest no matter how early we get there
        private const float ContestWindow = 0.9f;
        // Opponents flick and dodge into the ball, and GetEta does not model that — be pessimistic
        private const float OpponentEtaBonus = 0.15f;
        // Above this speed a ball the opponent just hit is a projectile to challenge, not one to carry
        private const float LooseBallSpeed = 800f;
        // An opponent within this range of the ball can contest it whatever Drive.GetEta says.
        // GetEta is meaningless for a car that is mid-flip or airborne right after a challenge:
        // PredictLandingTime inflates it to several seconds while the car is in fact on the ball.
        private const float ContestDistance = 1300f;

        /// <summary>
        /// Compares our team's earliest ball ETA vs opponents' to determine possession state.
        /// Possession requires both a clear ETA advantage AND that no opponent can contest soon:
        /// arriving 0.4s before an opponent who is on the ball in 0.5s is a 50/50, not possession.
        /// </summary>
        public static GameStateMode ComputeGameState(Car me, List<Car> livingTeammates, List<Car> livingOpponents,
            out float ourEta, out float theirEta, out float oppDist)
        {
            ourEta = FirstReachableEta(me);
            foreach (Car tm in livingTeammates)
                ourEta = MathF.Min(ourEta, FirstReachableEta(tm));

            theirEta = float.MaxValue;
            oppDist = float.MaxValue;
            foreach (Car opp in livingOpponents)
            {
                theirEta = MathF.Min(theirEta, FirstReachableEta(opp));
                oppDist = MathF.Min(oppDist, opp.Location.Dist(Ball.Location));
            }

            if (theirEta != float.MaxValue)
                theirEta = MathF.Max(theirEta - OpponentEtaBonus, 0f);

            float diff = ourEta - theirEta;
            if (diff > PossessionMargin) return GameStateMode.NotPossessed;

            bool contestable = theirEta <= ContestWindow || oppDist < ContestDistance || IsIncomingProjectile(me);
            if (diff < -PossessionMargin && !contestable)
                return GameStateMode.Possessed;
            return GameStateMode.Contested;
        }

        /// <summary>
        /// True when the opponent just struck the ball and it is travelling fast.
        /// Their ETA explodes (the ball is running away from them) which reads as possession for us,
        /// but nobody controls that ball yet — it has to be challenged, not dribbled.
        /// </summary>
        private static bool IsIncomingProjectile(Car me)
        {
            return Ball.LatestTouch != null
                && Ball.LatestTouch.Team != me.Team
                && Ball.Velocity.Length() > LooseBallSpeed;
        }

        // --- Boost sur le trajet de repli (Fixes.RetreatBoost, AUDIT §2.4) ---

        // Détour maximal toléré : allongement du trajet (aller au pad puis à la destination,
        // contre y aller directement). Au-delà, ramasser le boost coûte plus que la position perdue.
        private const float MaxDetour = 900f;
        // Un petit pad doit être d'autant plus commode qu'il rapporte moins : son détour est
        // compté ce facteur de fois. Évite de courir après 12 de boost.
        private const float SmallPadPenalty = 2.5f;
        // Un pad de l'autre côté du terrain n'est jamais « sur le chemin », sauf s'il est
        // pratiquement dans l'axe.
        private const float SameSideTolerance = 500f;
        // Au-delà de ce niveau, un détour ne se justifie plus.
        private const float BoostSeekCeiling = 80f;

        /// <summary>
        /// Pad de boost à ramasser EN CHEMIN vers <paramref name="destination"/>, ou null.
        ///
        /// <para>Un repli est un trajet qu'on fait de toute façon : le boost récupéré dessus est
        /// presque gratuit. La contrainte n'est donc pas « quel est le pad le plus proche » mais
        /// « quel pad allonge le moins le trajet ». Trois filtres :</para>
        /// <list type="number">
        /// <item>le pad doit être plus près de la destination que nous — sinon on recule ;</item>
        /// <item>même côté du terrain, pour ne pas traverser ;</item>
        /// <item>détour borné par <see cref="MaxDetour"/> — un dernier homme qui part chercher du
        /// boost à l'opposé n'est plus un dernier homme.</item>
        /// </list>
        /// <para>Le score est le détour lui-même (pénalisé pour les petits pads), pas la distance
        /// au pad : c'est le détour qui se paie en position.</para>
        /// </summary>
        public static Boost RetreatBoost(Car me, Vec3 destination)
        {
            if (!Fixes.RetreatBoost || me.Boost >= BoostSeekCeiling)
                return null;

            float directDistance = me.Location.Dist(destination);
            Boost best = null;
            float bestScore = float.MaxValue;

            foreach (Boost pad in Field.Boosts)
            {
                float toPad = me.Location.Dist(pad.Location);

                // Sera-t-il rechargé quand on y arrivera ?
                if (!pad.IsActive && pad.TimeUntilActive > Movement.EtaFor(me, pad.Location))
                    continue;

                // 1) Sur le chemin : plus près de la destination que nous ne le sommes
                if (pad.Location.Dist(destination) >= directDistance)
                    continue;

                // 2) Même côté du terrain (ou quasiment dans l'axe)
                if (MathF.Abs(pad.Location.x) > SameSideTolerance
                    && MathF.Sign(pad.Location.x) != MathF.Sign(me.Location.x))
                    continue;

                // 3) Détour borné
                float detour = toPad + pad.Location.Dist(destination) - directDistance;
                if (detour > MaxDetour)
                    continue;

                float score = detour * (pad.IsLarge ? 1f : SmallPadPenalty);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = pad;
                }
            }

            return best;
        }

        // --- Poste du dernier homme (AUDIT §2.5) ---

        // Profondeur : fraction de la distance balle→ligne dont on sort du but.
        private const float CoverAdvanceFraction = 0.2f;
        // ... bornée : au-delà on n'est plus un dernier homme mais un deuxième attaquant.
        private const float CoverMaxAdvance = 1100f;
        // On ne se colle jamais à la ligne : un contact sur la ligne pousse la balle dedans.
        private const float CoverLineOffset = 120f;
        // Position latérale du poste, mesurée depuis l'axe. Un peu en retrait du poteau (890)
        // pour couvrir l'intérieur plutôt que le montant lui-même.
        private const float CoverPostX = 640f;
        // Largeur de la rampe latérale autour de x = 0 (même raison que BackupPosition).
        private const float CoverPostRamp = 1500f;

        /// <summary>
        /// Poste du dernier homme : sur le <b>deuxième poteau</b>, à une profondeur qui suit la
        /// distance de la balle au but.
        ///
        /// <para><b>Ce que remplaçait ce calcul.</b> L'ancienne version interpolait à 20 % entre le
        /// CENTRE du but et la balle. Deux défauts : elle ne tenait aucun compte du côté (le dernier
        /// homme se plaçait sur la trajectoire de la balle, donc du même côté que l'attaquant), et
        /// elle sortait du but proportionnellement à la distance de la balle sans borne — balle au
        /// rond central, le « gardien » était à 1000 uu de sa ligne.</para>
        ///
        /// <para><b>Pourquoi pas un simple lerp poteau→balle à 20 %.</b> Sur une balle dans notre
        /// corner, la ligne poteau opposé→balle est presque entièrement latérale : 20 % de ce
        /// trajet ramène au centre de la cage (x ≈ 0), pas au deuxième poteau. Les deux axes
        /// répondent à des questions différentes et sont donc calculés séparément :</para>
        /// <list type="bullet">
        /// <item><b>Latéral</b> — côté opposé à la balle, en rampe. Balle dans un corner : on est
        /// au deuxième poteau. Balle dans l'axe : on est dans l'axe.</item>
        /// <item><b>Profondeur</b> — 20 % de la distance balle→ligne, plafonnée. Balle collée au
        /// but : on est sur la ligne. Balle loin : on avance un peu, sans jamais quitter la cage.</item>
        /// </list>
        ///
        /// <para>La rampe latérale remplace un <c>Sign()</c> pour la même raison que
        /// <see cref="BackupPosition"/> : un saut de 1280 uu dès que la balle frôle <c>x = 0</c>
        /// dépasse le seuil de re-ciblage et recrée l'action en boucle.</para>
        /// </summary>
        public static Vec3 DefensivePosition(Goal ourGoal)
        {
            // Comportement d'origine : 20 % du chemin centre du but → balle, sans notion de côté
            if (!Fixes.GoalieCover)
                return ourGoal.Location + (Ball.Location - ourGoal.Location) * 0.2f;

            int side = Field.Side(ourGoal.Team);

            // Latéral : deuxième poteau = à l'opposé de la balle
            float x = -Utils.Cap(Ball.Location.x / CoverPostRamp, -1f, 1f) * CoverPostX;

            // Profondeur : d'autant plus avancé que la balle est loin, mais jamais beaucoup
            float depthToBall = MathF.Abs(Ball.Location.y - ourGoal.Location.y);
            float advance = MathF.Min(depthToBall * CoverAdvanceFraction, CoverMaxAdvance);
            float y = ourGoal.Location.y - side * (advance + CoverLineOffset);

            return new Vec3(x, y, 0f);
        }

        /// <summary>
        /// Returns Defensive if the ball is in our half of the field, Offensive otherwise.
        /// Used to avoid searching for shots deep in our own half.
        /// </summary>
        public static FieldZone ComputeFieldZone(Goal ourGoal)
        {
            bool inOurHalf = Ball.Location.y * ourGoal.Location.y > 0;
            return inOurHalf ? FieldZone.Defensive : FieldZone.Offensive;
        }

        /// <summary>Time until this car can first intercept any ball prediction slice.</summary>
        private static float FirstReachableEta(Car car)
        {
            BallSlice slice = Ball.Prediction.Find(s => Movement.EtaFor(car, s.Location) <= s.Time - Game.Time);
            return slice == null ? float.MaxValue : slice.Time - Game.Time;
        }
    }
}
