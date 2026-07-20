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

        // A new game state must hold this long before we act on it (see StabilizeState)
        private const float StateHoldTime = 0.25f;
        private GameStateMode _stableState = GameStateMode.Contested;
        private GameStateMode _pendingState = GameStateMode.Contested;
        private float _pendingSince = -1f;

        // Au-delà de cette dérive de cible, on recrée le Drive/Arrive au lieu de le retargeter
        private const float RetargetDistance = 800f;
        // Hystérésis de collecte de boost du Support : entre sous Low, sort à High
        private const float SupportBoostLow = 30f;
        private const float SupportBoostHigh = 60f;
        private bool _collectingBoost;

        public MyBot(string botName, int botTeam, int botIndex) : base(botName, botTeam, botIndex) { }

        private void SetAction(IAction action, string intent)
        {
            Action = action;
            _intent = intent;
        }

        /// <summary>
        /// Pointe un Drive vers la cible en réutilisant l'action en cours si possible.
        /// Recréer un Drive à chaque tick remet son timeOnGround à zéro (Drive.cs:160),
        /// ce qui interdit dodges/speedflips/wavedashes — le bot roule alors à vitesse de base.
        /// </summary>
        private void SetDrive(Vec3 target, string intent, bool allowDodges = true)
        {
            if (Action is Drive drive && _intent == intent && drive.AllowDodges == allowDodges
                && drive.Target.Dist(target) < RetargetDistance)
            {
                drive.Target = target;
                return;
            }
            SetAction(new Drive(Me, target, allowDodges: allowDodges), intent);
        }

        /// <summary>
        /// Même latch que SetDrive, pour Arrive. Arrive resynchronise lui-même son Drive
        /// interne à chaque tick (Arrive.cs:95), donc muter Target/Direction suffit.
        /// </summary>
        private void SetArrive(Vec3 target, Vec3 direction, string intent)
        {
            if (Action is Arrive arrive && _intent == intent && arrive.Target.Dist(target) < RetargetDistance)
            {
                arrive.Target = target;
                arrive.Direction = direction;
                return;
            }
            SetAction(new Arrive(Me, target, direction), intent);
        }

        public override void Run()
        {
            if (Ball.LatestTouch != null && Ball.LatestTouch.Time != _lastLoggedTouchTime && Ball.LatestTouch.PlayerIndex == Index)
            {
                Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}#{Index}] TOUCHE la balle à ({Ball.LatestTouch.Location.x:F0},{Ball.LatestTouch.Location.y:F0},{Ball.LatestTouch.Location.z:F0}) intent={_intent ?? "none"}");
                _lastLoggedTouchTime = Ball.LatestTouch.Time;
            }

            GameStateMode rawState = Rotation.ComputeGameState(Me, LivingTeammates, LivingOpponents,
                out float ourEta, out float theirEta, out float oppDist);
            GameStateMode gameState = StabilizeState(rawState);
            Role? role = LivingTeammates.Count == 1
                ? Rotation.ComputeRole(Me, LivingTeammates[0], TheirGoal, _lastRole)
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

            SelectAction(gameState, fieldZone, role);

            if (gameState != _lastState || fieldZone != _lastZone || role != _lastRole || _intent != _lastIntent)
            {
                string runningAction = Action is not null and not Drive ? $"({Action.GetType().Name})" : "";
                string raw = rawState != gameState ? $" raw={rawState}" : "";
                Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}#{Index}] state={gameState}{raw} zone={fieldZone} role={role?.ToString() ?? "-"} intent={_intent ?? "none"}{runningAction} boost={Me.Boost:F0} dist={Me.Location.Dist(Ball.Location):F0} eta={Fmt(ourEta)}/{Fmt(theirEta)} oppDist={Fmt(oppDist)} ballV={Ball.Velocity.Length():F0} lastTouch={(Ball.LatestTouch == null ? "-" : Ball.LatestTouch.Team == Me.Team ? "nous" : "eux")}");
                _lastState = gameState;
                _lastZone = fieldZone;
                _lastRole = role;
                _lastIntent = _intent;
            }
        }

        /// <summary>
        /// Choisit l'action de ce tick. Séparé de Run() pour que les sorties anticipées
        /// (branche Support) ne court-circuitent pas le log de fin de tick.
        /// </summary>
        private void SelectAction(GameStateMode gameState, FieldZone fieldZone, Role? role)
        {
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
                ShotCheck defenseShotCheck = AccuratePhysics ? AccurateShotCheck : DefaultShotCheck;

                // Priorités défensives (Fixes.DefensiveOverhaul) :
                // save sur tir cadré (tous rôles), dégagement + discipline goal-side (Attacker)
                if (TryDefensivePriority(defenseShotCheck, fieldZone, role))
                    return;

                if (role == Role.Support)
                {
                    // Hystérésis 30/60 : sans bande morte le Support oscille entre collecte et placement
                    if (Me.Boost < SupportBoostLow) _collectingBoost = true;
                    else if (Me.Boost >= SupportBoostHigh) _collectingBoost = false;

                    // Ils ont la balle : le Support est le dernier homme, il couvre le but — boost ou pas
                    if (gameState == GameStateMode.NotPossessed)
                    {
                        Vec3 cover = Rotation.DefensivePosition(OurGoal);
                        SetArrive(cover, cover.FlatDirection(Ball.Location), "Arrive→Couverture");
                        return;
                    }

                    if (_collectingBoost)
                    {
                        // Ne pas recréer le GetBoost en cours : son Drive interne perdrait son timeOnGround
                        if (Action is GetBoost)
                            return;

                        // Uniquement les gros pads goal-side de la balle : pas question de
                        // traverser le terrain vers un coin adverse pour du boost
                        List<Boost> safeBoosts = [];
                        foreach (Boost b in Field.Boosts)
                            if (b.IsLarge && (b.Location.y - Ball.Location.y) * OurGoal.Location.y > 0)
                                safeBoosts.Add(b);
                        if (safeBoosts.Count > 0)
                        {
                            SetAction(new GetBoost(Me, safeBoosts), "GetBoost");
                            return;
                        }
                        // Aucun pad sûr : on se replace quand même, tant pis pour le boost
                    }

                    // Position de soutien basée sur la balle (goal-side + back post), face au jeu
                    Vec3 backup = Rotation.BackupPosition(OurGoal);
                    SetArrive(backup, backup.FlatDirection(Ball.Location), "Arrive→BackupPos");
                    return;
                }

                ShotCheck shotCheck = AccuratePhysics ? AccurateShotCheck : DefaultShotCheck;

                switch (gameState)
                {
                    case GameStateMode.NotPossessed:
                        BallSlice notPossApproach = Ball.Prediction.Find(s => s.Location.Dist(Me.Location) < 400f);
                        float notPossBallEta = notPossApproach != null ? notPossApproach.Time - Game.Time : float.MaxValue;
                        if (notPossBallEta < 1.5f)
                        {
                            // Ne pas recréer le Fifty s'il tourne déjà : il doit progresser Approach → Jump → Dodge.
                            if (Action is not Fifty)
                                SetAction(new Fifty(), "Fifty");
                        }
                        else
                            SetDrive(OurGoal.Location + (Ball.Location - OurGoal.Location) * 0.6f, "Drive→Shadow");
                        break;

                    case GameStateMode.Contested:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            Shot contestedShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (contestedShot != null)
                                SetAction(contestedShot, "Shot→LeurBut");
                            else
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false);
                        }
                        else
                        {
                            BallSlice approachSlice = Ball.Prediction.Find(s => s.Location.Dist(Me.Location) < 400f);
                            float ballEta = approachSlice != null ? approachSlice.Time - Game.Time : float.MaxValue;
                            if (ballEta < 1.5f)
                            {
                                if (Action is not Fifty)
                                    SetAction(new Fifty(), "Fifty");
                            }
                            else
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false);
                        }
                        break;

                    case GameStateMode.Possessed:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            Shot offensiveShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (offensiveShot != null)
                                SetAction(offensiveShot, "Shot→LeurBut");
                            else
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false);
                        }
                        else
                        {
                            // Possession dans NOTRE moitié : on CONTRÔLE la balle (carry vers l'adversaire).
                            // Pas de tir sur leur but ici (trop loin, ça casse le dribble) — le tir se déclenche
                            // tout seul via la branche Offensive dès que la balle passe le milieu de terrain.
                            Vec3 ballToGoal = (OurGoal.Location - Ball.Location).Normalize();
                            bool onGoalSide = (Me.Location - Ball.Location).Normalize().Dot(ballToGoal) > 0.5f;
                            bool closeEnough = Me.Location.Dist(Ball.Location) < 400f;

                            if (onGoalSide && closeEnough)
                            {
                                // Ne pas recréer le Dribble s'il tourne déjà : préserve l'état de portage (anti-wobble).
                                if (Action is not Dribble)
                                    SetAction(new Dribble(Me, TheirGoal.Location), "Dribble");
                            }
                            else
                            {
                                // La balle arrive-t-elle vers moi ? (indépendant de ma vitesse)
                                Vec3 ballToMe = (Me.Location - Ball.Location).Normalize();
                                float ballApproachSpeed = ballToMe.Dot(Ball.Velocity);

                                if (ballApproachSpeed > 500f)
                                {
                                    // Balle qui arrive vite → dégagement (tir loin de NOTRE but)
                                    Shot clearShot = FindShot(shotCheck, new Target(OurGoal, shootAwayFromGoal: true));
                                    if (clearShot != null)
                                        SetAction(clearShot, "Shot→Dégagement");
                                    else
                                        SetDrive(Ball.Location + ballToGoal * 300f, "Drive→Contour", allowDodges: false);
                                }
                                else
                                {
                                    // Balle fuyante : si proche → dribbler, sinon contourner goal-side
                                    if (Me.Location.Dist(Ball.Location) < 800f)
                                    {
                                        if (Action is not Dribble)
                                            SetAction(new Dribble(Me, TheirGoal.Location), "Dribble");
                                    }
                                    else
                                    {
                                        Vec3 offset = ballToGoal * 300f;
                                        BallSlice contourSlice = Ball.Prediction.Find(s =>
                                            Drive.GetEta(Me, s.Location + offset) <= s.Time - Game.Time);
                                        Vec3 contourTarget = contourSlice != null
                                            ? contourSlice.Location + offset
                                            : Ball.Location + offset;
                                        SetDrive(contourTarget, "Drive→Contour", allowDodges: false);
                                    }
                                }
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Priorités défensives (Fixes.DefensiveOverhaul). Retourne true si une action a été choisie.
        /// Ordre : 1) SAVE si la prédiction voit la balle entrer dans notre but (tous rôles),
        /// 2) DÉGAGEMENT si la balle est dangereuse dans notre zone (Attacker),
        /// 3) GOAL-SIDE si on n'est pas entre la balle et notre but (Attacker) — anti-CSC.
        /// </summary>
        private bool TryDefensivePriority(ShotCheck shotCheck, FieldZone fieldZone, Role? role)
        {
            if (!Fixes.DefensiveOverhaul)
                return false;

            // --- 1) Tir cadré : la prédiction voit la balle franchir NOTRE ligne ---
            // FindGoal(team) = balle marquant EN FAVEUR de team → notre but encaisse pour team adverse
            BallSlice goalSlice = Ball.Prediction.FindGoal(1 - Me.Team);
            if (goalSlice != null)
            {
                Shot save = FindShot(shotCheck, new Target(OurGoal, shootAwayFromGoal: true));
                if (save != null)
                {
                    SetAction(save, "Shot→Save");
                    return true;
                }

                // Aucun tir jouable : interception d'urgence sur la trajectoire, AVANT la ligne.
                // wasteBoost — une save justifie de brûler du boost (Drive n'en utilise jamais sinon).
                BallSlice intercept = Ball.Prediction.Find(s =>
                    s.Time < goalSlice.Time && Drive.GetEta(Me, s.Location) <= s.Time - Game.Time);
                Vec3 saveTarget = intercept != null
                    ? intercept.Location
                    : OurGoal.Location + OurGoal.Location.FlatDirection(Ball.Location) * 300f;

                if (Action is Drive saveDrive && _intent == "Drive→Save"
                    && saveDrive.Target.Dist(saveTarget) < RetargetDistance)
                    saveDrive.Target = saveTarget;
                else
                    SetAction(new Drive(Me, saveTarget, wasteBoost: true), "Drive→Save");
                return true;
            }

            // --- 2) & 3) réservés à l'Attacker : le Support garde sa couverture ---
            if (role == Role.Support || fieldZone != FieldZone.Defensive)
                return false;

            Vec3 towardOurGoal = Ball.Location.FlatDirection(OurGoal.Location);
            bool inOurThird = MathF.Abs(Ball.Location.y - OurGoal.Location.y) < 3400f;
            bool headingToUs = Ball.Velocity.Dot(towardOurGoal) > 300f;
            if (!inOurThird && !headingToUs)
                return false;

            // --- 2) Balle dangereuse → dégagement (tir loin de notre but) ---
            Shot clear = FindShot(shotCheck, new Target(OurGoal, shootAwayFromGoal: true));
            if (clear != null)
            {
                SetAction(clear, "Shot→Dégagement");
                return true;
            }

            // --- 3) Pas goal-side → se replier entre la balle et notre but AVANT tout contact.
            // C'est LE cas qui fabrique les CSC : toucher la balle en la poursuivant vers notre but.
            if (!IsGoalSide())
            {
                Vec3 contour = ClampToField(Ball.Location + towardOurGoal * 1200f);
                SetDrive(contour, "Drive→GoalSide");
                return true;
            }

            // Goal-side, pas de tir jouable : la logique standard (Fifty / Drive) prend le relais —
            // depuis goal-side, la poussée voiture→balle part vers le camp adverse, c'est sain.
            return false;
        }

        /// <summary>Vrai si on est entre la balle et notre but (contact défensif sûr).</summary>
        private bool IsGoalSide()
        {
            Vec3 towardOurGoal = Ball.Location.FlatDirection(OurGoal.Location);
            return (Me.Location - Ball.Location).Normalize().Dot(towardOurGoal) > 0.2f;
        }

        /// <summary>Ramène une position dans les limites du terrain (marge 400), au sol.</summary>
        private static Vec3 ClampToField(Vec3 pos)
        {
            pos.x = Utils.Cap(pos.x, -Field.Width / 2f + 400f, Field.Width / 2f - 400f);
            pos.y = Utils.Cap(pos.y, -Field.Length / 2f + 400f, Field.Length / 2f - 400f);
            pos.z = 0f;
            return pos;
        }

        private static string Fmt(float eta) => eta == float.MaxValue ? "∞" : eta.ToString("F2");

        /// <summary>
        /// Absorbs the noise of the ETA estimator: a new state must hold for StateHoldTime before
        /// we adopt it. Without this a single jittery frame flips the whole strategy — on a 50/50
        /// the raw state can swing NotPossessed → Possessed in 100ms while nothing real changed.
        /// </summary>
        private GameStateMode StabilizeState(GameStateMode rawState)
        {
            if (rawState == _stableState)
            {
                _pendingState = _stableState;
                return _stableState;
            }

            if (rawState != _pendingState)
            {
                _pendingState = rawState;
                _pendingSince = Game.Time;
            }
            else if (Game.Time - _pendingSince >= StateHoldTime)
            {
                _stableState = rawState;
            }

            return _stableState;
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
