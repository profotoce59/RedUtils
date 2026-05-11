using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public enum Role { Attacker, Support }

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
    }
}
