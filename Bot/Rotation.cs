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

        /// <summary>
        /// Determines whether this car is the attacker or the support in a 2v2.
        /// Both bots call this independently each tick and will agree without shared state:
        /// the incumbent-keeps-role conditions below are exact complements of each other.
        /// </summary>
        public static Role ComputeRole(Car me, Car teammate, Goal theirGoal, Role? currentRole = null)
        {
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
        /// Score = ETA to reach the ball + angle penalty if the shot would go backwards.
        /// Lower is better (Attacker = lowest score).
        /// </summary>
        private static float ComputeScore(Car car, Goal theirGoal)
        {
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

        /// <summary>
        /// Defensive fallback position: 20% of the way from our goal toward the ball.
        /// Stays close to goal to shadow incoming shots.
        /// </summary>
        public static Vec3 DefensivePosition(Goal ourGoal)
        {
            return ourGoal.Location + (Ball.Location - ourGoal.Location) * 0.2f;
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
