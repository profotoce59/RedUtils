using System;
using System.Threading;
using System.Drawing;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public class MyBot : RUBot
    {
        // Automatically true in Debug build, false in Release build
#if DEBUG
        private const bool DebugMode = true;
#else
        private const bool DebugMode = false;
#endif
        // Toggle between default (simplified) and accurate (RocketSim) hit physics for FindShot
        private const bool AccuratePhysics = false;

        public MyBot(string botName, int botTeam, int botIndex) : base(botName, botTeam, botIndex) { }
    // Your bot class! :D

        public override void Run()
        {
            GameStateMode gameState = Rotation.ComputeGameState(Me, LivingTeammates, LivingOpponents);
            Role? role = LivingTeammates.Count == 1
                ? Rotation.ComputeRole(Me, LivingTeammates[0], TheirGoal)
                : (Role?)null;

            if (DebugMode)
                DrawDebug(gameState, role);

            if (IsKickoff && Action == null)
            {
                bool goingForKickoff = true;
                foreach (Car teammate in Teammates)
                {
                    goingForKickoff = goingForKickoff && Me.Location.Dist(Ball.Location) <= teammate.Location.Dist(Ball.Location);
                }
                Action = goingForKickoff ? new Kickoff() : new GetBoost(Me, interruptible: false);
            }
            else if (Action == null || (Action is Drive && Action.Interruptible))
            {
                if (role == Role.Support)
                {
                    Action = Me.Boost < 70
                        ? (IAction)new GetBoost(Me)
                        : new Drive(Me, Rotation.BackupPosition(LivingTeammates[0], OurGoal));
                    return;
                }

                ShotCheck shotCheck = AccuratePhysics ? AccurateShotCheck : DefaultShotCheck;

                switch (gameState)
                {
                    case GameStateMode.Defensive:
                        Action = new Drive(Me, Ball.Location);
                        break;

                    case GameStateMode.Contested:
                        Shot contestedShot = FindShot(shotCheck, new Target(TheirGoal));
                        Action = (IAction)contestedShot ?? new Drive(Me, Ball.Location);
                        break;

                    case GameStateMode.Offensive:
                        bool opponentLastTouched = Ball.LatestTouch != null && Ball.LatestTouch.Team != Me.Team;
                        Target target = opponentLastTouched
                            ? new Target(OurGoal, shootAwayFromGoal: true)
                            : new Target(TheirGoal);
                        Shot offensiveShot = FindShot(shotCheck, target);
                        Action = (IAction)offensiveShot ?? new Drive(Me, Ball.Location);
                        break;
                }
            }
        }

        private void DrawDebug(GameStateMode gameState, Role? role)
        {
            int x = 10, y = 10;

            // Game state — colour-coded
            Color stateColor = gameState switch
            {
                GameStateMode.Offensive => Color.LimeGreen,
                GameStateMode.Contested => Color.Yellow,
                GameStateMode.Defensive => Color.Red,
                _                       => Color.White,
            };
            Renderer.Text2D($"STATE  : {gameState}", new Vec3(x, y), 3, stateColor);
            y += 30;

            // Role — only shown in 2v2
            if (role.HasValue)
            {
                Color roleColor = role.Value == Role.Attacker ? Color.Cyan : Color.Orange;
                Renderer.Text2D($"ROLE   : {role.Value}", new Vec3(x, y), 3, roleColor);
                y += 30;
            }

            // Current action
            Renderer.Text2D($"ACTION : {(Action != null ? Action.ToString() : "none")}", new Vec3(x, y), 3, Color.White);
            y += 30;

            // Boost amount — red when low
            Color boostColor = Me.Boost < 30 ? Color.Red : Me.Boost < 70 ? Color.Yellow : Color.LimeGreen;
            Renderer.Text2D($"BOOST  : {Me.Boost}", new Vec3(x, y), 3, boostColor);
        }
    }
}
