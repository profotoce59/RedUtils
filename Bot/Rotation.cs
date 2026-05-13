using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public enum Role { Attacker, Support }
    public enum GameStateMode { Offensive, Contested, Defensive }

    public static class Rotation
    {
        // Seconds added to ETA when a player would have to shoot backwards
        private const float BackwardsAnglePenalty = 2.0f;
        // ~108°: angle between (car→ball) and (ball→their goal) above which we consider the shot backwards
        private static readonly float BackwardsAngleThreshold = MathF.PI * 0.6f;

        /// <summary>
        /// Determines whether this car is the attacker or the support in a 2v2.
        /// Both bots call this independently each tick and will agree without shared state.
        /// </summary>
        public static Role ComputeRole(Car me, Car teammate, Goal theirGoal)
        {
            float myScore = ComputeScore(me, theirGoal);
            float teammateScore = ComputeScore(teammate, theirGoal);
            return myScore <= teammateScore ? Role.Attacker : Role.Support;
        }

        /// <summary>
        /// Score = ETA to reach the ball + angle penalty if the shot would go backwards.
        /// Lower is better (Attacker = lowest score).
        /// </summary>
        private static float ComputeScore(Car car, Goal theirGoal)
        {
            // First ball slice this car can physically reach in time
            BallSlice commitSlice = Ball.Prediction.Find(slice =>
                Drive.GetEta(car, slice.Location) <= slice.Time - Game.Time);

            if (commitSlice == null)
                return float.MaxValue;

            float eta = commitSlice.Time - Game.Time;

            // Penalize if the angle car→ball→theirGoal is too wide (would shoot backwards)
            Vec3 carToBall = commitSlice.Location - car.Location;
            Vec3 ballToGoal = theirGoal.Location - commitSlice.Location;
            float angle = carToBall.FlatAngle(ballToGoal);

            return eta + (angle > BackwardsAngleThreshold ? BackwardsAnglePenalty : 0f);
        }

        /// <summary>
        /// Ideal backup position: 1500 units goalside of the attacker.
        /// Keeps the support player directly behind Player1, ready to take over.
        /// </summary>
        public static Vec3 BackupPosition(Car attacker, Goal ourGoal)
        {
            Vec3 toGoal = (ourGoal.Location - attacker.Location).Normalize();
            return attacker.Location + toGoal * 1500f;
        }

        /// <summary>
        /// Compares our team's earliest ball ETA vs opponents' to determine possession state.
        /// Margin of 0.3s before declaring offensive or defensive — avoids flickering on 50/50s.
        /// </summary>
        public static GameStateMode ComputeGameState(Car me, List<Car> livingTeammates, List<Car> livingOpponents)
        {
            float ourEta = FirstReachableEta(me);
            foreach (Car tm in livingTeammates)
                ourEta = MathF.Min(ourEta, FirstReachableEta(tm));

            float theirEta = float.MaxValue;
            foreach (Car opp in livingOpponents)
                theirEta = MathF.Min(theirEta, FirstReachableEta(opp));

            float diff = ourEta - theirEta;
            if (diff < -0.3f) return GameStateMode.Offensive;
            if (diff >  0.3f) return GameStateMode.Defensive;
            return GameStateMode.Contested;
        }

        /// <summary>
        /// Defensive fallback position: 20% of the way from our goal toward the ball.
        /// Stays close to goal to shadow incoming shots.
        /// </summary>
        public static Vec3 DefensivePosition(Goal ourGoal)
        {
            return ourGoal.Location + (Ball.Location - ourGoal.Location) * 0.2f;
        }

        /// <summary>Time until this car can first intercept any ball prediction slice.</summary>
        private static float FirstReachableEta(Car car)
        {
            BallSlice slice = Ball.Prediction.Find(s => Drive.GetEta(car, s.Location) <= s.Time - Game.Time);
            return slice == null ? float.MaxValue : slice.Time - Game.Time;
        }
    }
}
