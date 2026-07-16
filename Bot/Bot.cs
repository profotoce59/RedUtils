using System;
using System.Collections.Generic;
using System.Drawing;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public class MyBot : RUBot
    {
#if DEBUG
        private const bool DebugMode = true;
#else
        private const bool DebugMode = false;
#endif
        private const bool AccuratePhysics = false;

        private GameStateMode _lastState;
        private FieldZone _lastZone;
        private Role? _lastRole;
        private string _lastIntent;
        private string _intent;
        private float _lastLoggedTouchTime = -1f;
        private float _lastBoostCheckTime = -1f;
        private const float BoostCheckInterval = 0.5f;

        public MyBot(string botName, int botTeam, int botIndex) : base(botName, botTeam, botIndex) { }

        private void SetAction(IAction action, string intent)
        {
            Action = action;
            _intent = intent;
        }

        public override void Run()
        {
            if (Ball.LatestTouch != null && Ball.LatestTouch.Time != _lastLoggedTouchTime && Ball.LatestTouch.PlayerIndex == Index)
            {
                Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}] TOUCHE la balle à ({Ball.LatestTouch.Location.x:F0},{Ball.LatestTouch.Location.y:F0},{Ball.LatestTouch.Location.z:F0}) intent={_intent ?? "none"}");
                _lastLoggedTouchTime = Ball.LatestTouch.Time;
            }

            GameStateMode gameState = Rotation.ComputeGameState(Me, LivingTeammates, LivingOpponents);
            Role? role = LivingTeammates.Count == 1
                ? Rotation.ComputeRole(Me, LivingTeammates[0], TheirGoal)
                : null;

            if (DebugMode)
                DrawDebug(gameState, role);

            FieldZone fieldZone = Rotation.ComputeFieldZone(OurGoal);

            // Toutes les 0.5s : interrompre GetBoost si on est Attacker et que la balle est plus proche que le pad
            if (Action is GetBoost runningBoost && role != Role.Support
                && Game.Time - _lastBoostCheckTime >= BoostCheckInterval)
            {
                _lastBoostCheckTime = Game.Time;
                if (Me.Location.Dist(Ball.Location) < Me.Location.Dist(runningBoost.ChosenBoost.Location))
                    Action = null;
            }

            if (IsKickoff && Action == null)
            {
                if (role != Role.Support)
                {
                    SetAction(new Kickoff(), "Kickoff");
                }
                else
                {
                    List<Boost> ourBoosts = [];
                    foreach (Boost b in Field.Boosts)
                        if (b.IsLarge && b.Location.y * OurGoal.Location.y > 0)
                            ourBoosts.Add(b);
                    SetAction(ourBoosts.Count > 0
                        ? new GetBoost(Me, ourBoosts, interruptible: false)
                        : new GetBoost(Me, interruptible: false), "GetBoost(kickoff)");
                }
            }
            else if (Action == null || Action.Interruptible)
            {
                if (role == Role.Support)
                {
                    if (Me.Boost < 70)
                        SetAction(new GetBoost(Me), "GetBoost");
                    else
                        SetAction(new Drive(Me, Rotation.BackupPosition(LivingTeammates[0], OurGoal)), "Drive→BackupPos");
                    return;
                }

                ShotCheck shotCheck = AccuratePhysics ? AccurateShotCheck : DefaultShotCheck;

                switch (gameState)
                {
                    case GameStateMode.NotPossessed:
                        BallSlice notPossApproach = Ball.Prediction.Find(s => s.Location.Dist(Me.Location) < 400f);
                        float notPossBallEta = notPossApproach != null ? notPossApproach.Time - Game.Time : float.MaxValue;
                        if (notPossBallEta < 1.5f)
                            SetAction(new Fifty(), "Fifty");
                        else
                            SetAction(new Drive(Me, OurGoal.Location + (Ball.Location - OurGoal.Location) * 0.6f), "Drive→Shadow");
                        break;

                    case GameStateMode.Contested:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            Shot contestedShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (contestedShot != null)
                                SetAction(contestedShot, "Shot→LeurBut");
                            else
                                SetAction(new Drive(Me, Ball.Location, allowDodges: false), "Drive→Balle");
                        }
                        else
                        {
                            BallSlice approachSlice = Ball.Prediction.Find(s => s.Location.Dist(Me.Location) < 400f);
                            float ballEta = approachSlice != null ? approachSlice.Time - Game.Time : float.MaxValue;
                            if (ballEta < 1.5f)
                                SetAction(new Fifty(), "Fifty");
                            else
                                SetAction(new Drive(Me, Ball.Location, allowDodges: false), "Drive→Balle");
                        }
                        break;

                    case GameStateMode.Possessed:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            Shot offensiveShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (offensiveShot != null)
                                SetAction(offensiveShot, "Shot→LeurBut");
                            else
                                SetAction(new Drive(Me, Ball.Location, allowDodges: false), "Drive→Balle");
                        }
                        else
                        {
                            Vec3 ballToGoal = (OurGoal.Location - Ball.Location).Normalize();
                            Vec3 toTheirGoal = (TheirGoal.Location - Ball.Location).Normalize();
                            bool goodAngle = Me.Forward.FlatNorm().Dot(toTheirGoal) > 0.15f;

                            Shot shot = goodAngle ? FindShot(shotCheck, new Target(TheirGoal)) : null;
                            if (shot != null)
                                SetAction(shot, "Shot→LeurBut");
                            else
                            {
                                bool onGoalSide = (Me.Location - Ball.Location).Normalize().Dot(ballToGoal) > 0.5f;
                                bool closeEnough = Me.Location.Dist(Ball.Location) < 400f;
                                if (onGoalSide && closeEnough)
                                {
                                    SetAction(new Dribble(Me), "Dribble");
                                }
                                else
                                {
                                    // La balle arrive-t-elle vers moi ? (indépendant de ma vitesse)
                                    Vec3 ballToMe = (Me.Location - Ball.Location).Normalize();
                                    float ballApproachSpeed = ballToMe.Dot(Ball.Velocity);

                                    if (ballApproachSpeed > 500f)
                                    {
                                        // Balle qui arrive → redirect vers leur but possible
                                        shot = FindShot(shotCheck, new Target(OurGoal, shootAwayFromGoal: true));
                                        if (shot != null)
                                            SetAction(shot, "Shot→Dégagement");
                                        else
                                            SetAction(new Drive(Me, Ball.Location + ballToGoal * 300f, allowDodges: false), "Drive→Contour");
                                    }
                                    else
                                    {
                                        // Balle fuyante : si proche → dribbler, sinon contourner goal-side
                                        if (Me.Location.Dist(Ball.Location) < 800f)
                                        {
                                            SetAction(new Dribble(Me), "Dribble");
                                        }
                                        else
                                        {
                                            Vec3 offset = ballToGoal * 300f;
                                            BallSlice contourSlice = Ball.Prediction.Find(s =>
                                                Drive.GetEta(Me, s.Location + offset) <= s.Time - Game.Time);
                                            Vec3 contourTarget = contourSlice != null
                                                ? contourSlice.Location + offset
                                                : Ball.Location + offset;
                                            SetAction(new Drive(Me, contourTarget, allowDodges: false), "Drive→Contour");
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            if (gameState != _lastState || fieldZone != _lastZone || role != _lastRole || _intent != _lastIntent)
            {
                string runningAction = Action is not null and not Drive ? $"({Action.GetType().Name})" : "";
                Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}] state={gameState} zone={fieldZone} role={role?.ToString() ?? "-"} intent={_intent ?? "none"}{runningAction} boost={Me.Boost:F0} dist={Me.Location.Dist(Ball.Location):F0}");
                _lastState = gameState;
                _lastZone = fieldZone;
                _lastRole = role;
                _lastIntent = _intent;
            }
        }

        private void DrawDebug(GameStateMode gameState, Role? role)
        {
            int x = 10, y = 10;
            Color stateColor = gameState switch
            {
                GameStateMode.NotPossessed => Color.LimeGreen,
                GameStateMode.Contested    => Color.Yellow,
                GameStateMode.Possessed    => Color.Red,
                _                          => Color.White,
            };
            Renderer.Text2D($"STATE  : {gameState}", new Vec3(x, y), 3, stateColor);
            y += 30;

            if (role.HasValue)
            {
                Color roleColor = role.Value == Role.Attacker ? Color.Cyan : Color.Orange;
                Renderer.Text2D($"ROLE   : {role.Value}", new Vec3(x, y), 3, roleColor);
                y += 30;
            }

            Renderer.Text2D($"INTENT : {_intent ?? "none"}", new Vec3(x, y), 3, Color.White);
            y += 30;

            Color boostColor = Me.Boost < 30 ? Color.Red : Me.Boost < 70 ? Color.Yellow : Color.LimeGreen;
            Renderer.Text2D($"BOOST  : {Me.Boost}", new Vec3(x, y), 3, boostColor);
        }
    }
}
