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
        // Distance goal-side de la balle à laquelle l'Attacker vient presser en zone offensive
        private const float PressGap = 1100f;
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
        /// RUBot detected a teleport (ball/car moved further in one tick than physics allows -
        /// state setting, e.g. the test scripts in state_setting_tests_*.py) and already reset
        /// Action. Clear our own latched decision state too, so the very next tick recomputes
        /// state/role/intent from scratch instead of half-following the previous scenario.
        /// </summary>
        protected override void OnStateSet()
        {
            // Chaque state set relance une mesure du banc d'étalonnage
            _benchRunning = false;
            _benchDone = false;

            _stableState = GameStateMode.Contested;
            _pendingState = GameStateMode.Contested;
            _pendingSince = -1f;
            _lastRole = null;
            _collectingBoost = false;
            _lastBoostCheckTime = -1f;
            _lastLoggedTouchTime = -1f;

            // Force the next Run() to log the fresh state instead of staying silent because
            // gameState/zone/role/intent happen to match what was latched before the reset.
            _lastState = (GameStateMode)(-1);
            _lastZone = (FieldZone)(-1);
            _lastIntent = null;
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

        /// <summary>
        /// Vrai si un Shot du même intent est déjà en cours.
        /// Un Shot gère son propre cycle de vie (JumpShot par ex. rafraîchit sa cible toutes les
        /// 0.2s via SetTargetLocation, et s'auto-abandonne — Finished=true — via ses gardes internes
        /// abortEta/abortInvalid/...). Rappeler FindShot et réassigner à chaque tick, comme pour
        /// Drive avant SetDrive/SetArrive, jette cet état (son Arrive interne, ses timers) et impose
        /// une cible choisie à froid par FindShot — qui peut flip-flop d'un tick à l'autre sur les
        /// cas limites (Drive.GetEta franchit ou non le seuil). Résultat observé : le bot fonce droit
        /// sur la balle au lieu de mener l'interception, un tick sur deux. Tant que l'intent ne
        /// change pas, on laisse le shot en cours se corriger tout seul.
        /// </summary>
        private bool ShotInProgress(string intent) => Action is Shot && _intent == intent;


        // --- Banc d'étalonnage ETA (Fixes.EtaBench) ---
        private Vec3 _benchTarget;
        private float _benchPredicted;
        private float _benchStartTime;
        private float _benchStartSpeed;
        private float _benchStartBoost;
        private float _benchStartDist;
        private bool _benchRunning;
        private bool _benchDone;
        private bool _benchAllowDodges;
        private int _benchFlips;
        private bool _benchWasFlipping;
        private EtaBreakdown _benchDetail;
        /// <summary>Rayon d'arrivée. Drive.Finished utilise la même valeur.</summary>
        private const float BenchArrivedDist = 100f;

        /// <summary>
        /// Le script de test signale « conduire sans dodge » via le boost du coéquipier garé.
        ///
        /// <para>Passer par un flag C# imposerait de recompiler entre chaque course, ce qui rend une
        /// session d'étalonnage impraticable. Le coéquipier est garé dans un coin et n'a aucune
        /// influence sur notre trajet : son niveau de boost est donc un canal libre. 0 = dodges
        /// autorisés (conduite normale), 100 = dodges interdits. Comparer les deux à distance égale
        /// mesure le coût réel du flip au lieu de le déduire.</para>
        /// </summary>
        private bool BenchDodgesAllowed()
        {
            List<Car> mates = Teammates;
            return mates.Count == 0 || mates[0].Boost < 50;
        }

        /// <summary>
        /// Mesure « temps minimal pour aller de A à B ». Le bot roule à fond vers la balle,
        /// que le script de test place sur le point d'arrivée voulu.
        ///
        /// <para>Toute la stratégie est court-circuitée : on ne mesure QUE le déplacement, sans
        /// qu'un changement d'état vienne réorienter la voiture en cours de route.</para>
        ///
        /// <para>Biais connu : on chronomètre l'entrée dans un rayon de 100 uu autour de la cible,
        /// alors que GetEta prédit le temps jusqu'au point exact. À 2000 uu/s cela sous-estime le
        /// temps réel d'environ 0.05 s — à garder en tête pour ne pas courir après cet écart.</para>
        /// </summary>
        private void RunEtaBench()
        {
            if (_benchDone)
                return;

            if (!_benchRunning)
            {
                // La balle marque la cible ; on la fige au départ pour que le point ne bouge plus
                _benchTarget = Ball.Location;
                _benchPredicted = Fixes.MovementEngine
                    ? Movement.Eta(Me, _benchTarget, out _benchDetail)
                    : Drive.GetEta(Me, _benchTarget);
                _benchStartTime = Game.Time;
                _benchStartSpeed = Me.Velocity.Length();
                _benchStartBoost = Me.Boost;
                _benchStartDist = Me.Location.Dist(_benchTarget);
                _benchAllowDodges = BenchDodgesAllowed();
                _benchFlips = 0;
                _benchWasFlipping = false;
                _benchRunning = true;

                float angle = Me.Forward.FlatAngle(Me.Location.FlatDirection(_benchTarget)) * 180f / MathF.PI;
                Console.WriteLine($"[BENCH] DEPART dist={_benchStartDist:F0} angle={angle:F0}° " +
                    $"v0={_benchStartSpeed:F0} boost0={_benchStartBoost:F0} dodges={(_benchAllowDodges ? "oui" : "NON")} " +
                    $"moteur={(Fixes.MovementEngine ? "Movement" : "Drive")} prevu={_benchPredicted:F3}s" +
                    (Fixes.MovementEngine ? $"  {_benchDetail}" : ""));
            }

            // Plein régime, boost autorisé. Les dodges suivent le canal du script de test.
            if (Action is not Drive benchDrive || benchDrive.Target.Dist(_benchTarget) > 1f)
                Action = new Drive(Me, _benchTarget, Car.MaxSpeed, allowDodges: _benchAllowDodges, wasteBoost: true);

            // Compte les flips réellement déclenchés : c'est la donnée qui permettra de calibrer
            // leur coût dans Movement.cs, plutôt que de l'inférer de l'écart global.
            if (Action is Drive running)
            {
                bool flipping = running.Action is Dodge or SpeedFlip or Wavedash or HalfFlip;
                if (flipping && !_benchWasFlipping)
                    _benchFlips++;
                _benchWasFlipping = flipping;
            }

            float elapsed = Game.Time - _benchStartTime;
            float remaining = Me.Location.Dist(_benchTarget);

            if (remaining < BenchArrivedDist)
            {
                // On détecte l'arrivée dans un rayon de 100 uu, mais GetEta prédit le point EXACT.
                // Sans compenser ce reliquat, un trajet court est fatalement jugé trop rapide :
                // sur 300 uu, ces 100 uu sont un tiers du parcours et suffisent à inverser le
                // signe de l'erreur. On extrapole donc la fin avec le modèle d'accélération.
                float speed = Me.Velocity.Length();
                float tail = Drive.TimeToCoverDistance(speed, Me.Boost, remaining);
                float actual = elapsed + tail;
                float error = actual - _benchPredicted;
                string pct = _benchPredicted > 0.01f ? $" ({error / _benchPredicted * 100:+0;-0}%)" : "";
                Console.WriteLine($"[BENCH] ARRIVE dist={_benchStartDist:F0} v0={_benchStartSpeed:F0} boost0={_benchStartBoost:F0} " +
                    $"dodges={(_benchAllowDodges ? "oui" : "NON")} flips={_benchFlips} " +
                    $"prevu={_benchPredicted:F3} reel={actual:F3} erreur={error:+0.000;-0.000}{pct} " +
                    $"[chrono={elapsed:F3} + {tail:F3} pour les {remaining:F0} derniers uu] " +
                    $"vArrivee={speed:F0} boostRestant={Me.Boost:F0}");
                _benchDone = true;
                Action = null;
            }
            else if (elapsed > _benchPredicted * 3f + 2f)
            {
                Console.WriteLine($"[BENCH] ECHEC dist={_benchStartDist:F0} prevu={_benchPredicted:F3} " +
                    $"abandon apres {elapsed:F2}s, il reste {remaining:F0} uu");
                _benchDone = true;
                Action = null;
            }
        }

        public override void Run()
        {
            if (Fixes.EtaBench)
            {
                RunEtaBench();
                return;
            }

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

            TraceShot();
            TrackEta();
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

                    // Ils ont la balle DANS NOTRE MOITIÉ : le Support est le dernier homme, il couvre
                    // le but — boost ou pas. Balle dans leur moitié : pas de danger immédiat, il monte
                    // en soutien (BackupPosition, plus bas) au lieu d'abandonner le terrain.
                    if (gameState == GameStateMode.NotPossessed
                        && (!Fixes.OffensivePressing || fieldZone == FieldZone.Defensive))
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
                        else if (Fixes.OffensivePressing && fieldZone == FieldZone.Offensive)
                        {
                            // Balle dans leur moitié : on vient la presser au lieu de se replier au
                            // milieu (le shadow à 60% depuis notre but = le rond central dans ce cas).
                            //
                            // Cible = PressGap goal-side de la BALLE, pas un slice d'interception :
                            // le slice atteignable est une fonction en escalier qui saute de plusieurs
                            // milliers d'unités d'un tick à l'autre, ce qui casse le latch de SetDrive,
                            // recrée le Drive à chaque frame et remet son timeOnGround à zéro —
                            // donc plus aucun dodge ni speedflip (Drive.cs:160). Cette cible-ci est
                            // continue en Ball.Location, le Drive survit et le bot arrive vite.
                            // Le contact reste géré par le Fifty ci-dessus dès que la balle est à portée.
                            Vec3 pressTarget = ClampToField(Ball.Location + Ball.Location.FlatDirection(OurGoal.Location) * PressGap);
                            SetDrive(pressTarget, "Drive→Pressing");
                        }
                        else
                            SetDrive(OurGoal.Location + (Ball.Location - OurGoal.Location) * 0.6f, "Drive→Shadow");
                        break;

                    case GameStateMode.Contested:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            if (ShotInProgress("Shot→LeurBut"))
                                break;

                            Shot contestedShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (contestedShot != null)
                            {
                                LogShotPick("Shot→LeurBut", contestedShot);
                                SetAction(contestedShot, "Shot→LeurBut");
                            }
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
                            if (ShotInProgress("Shot→LeurBut"))
                                break;

                            Shot offensiveShot = FindShot(shotCheck, new Target(TheirGoal));
                            if (offensiveShot != null)
                            {
                                LogShotPick("Shot→LeurBut", offensiveShot);
                                SetAction(offensiveShot, "Shot→LeurBut");
                            }
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
                                    if (!ShotInProgress("Shot→Dégagement"))
                                    {
                                        Shot clearShot = FindShot(shotCheck, ClearTarget());
                                        if (clearShot != null)
                                        {
                                            LogShotPick("Shot→Dégagement", clearShot);
                                            SetAction(clearShot, "Shot→Dégagement");
                                        }
                                        else
                                            SetDrive(Ball.Location + ballToGoal * 300f, "Drive→Contour", allowDodges: false);
                                    }
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
                if (ShotInProgress("Shot→Save"))
                    return true;

                Shot save = FindShot(Defensible(shotCheck), ClearTarget());
                if (save != null)
                {
                    LogShotPick("Shot→Save", save);
                    SetAction(save, "Shot→Save");
                    return true;
                }

                // Aucun tir jouable : interception d'urgence sur la trajectoire, AVANT la ligne.
                // wasteBoost — une save justifie de brûler du boost (Drive n'en utilise jamais sinon).
                BallSlice intercept = FindInterceptSlice(goalSlice.Time);
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
            if (ShotInProgress("Shot→Dégagement"))
                return true;

            Shot clear = FindShot(Defensible(shotCheck), ClearTarget());
            if (clear != null)
            {
                LogShotPick("Shot→Dégagement", clear);
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

        /// <summary>
        /// PREMIÈRE slice atteignable avant que la balle ne franchisse notre ligne.
        ///
        /// <para>Une version « dernière slice atteignable » a été essayée, dans l'idée qu'elle
        /// offrirait plus de marge. C'est vrai temporellement, mais c'est un contresens défensif :
        /// la dernière interception possible est par construction celle qui a lieu au ras du but.
        /// Observé en jeu — la cible s'enfonçait de 4362 puis 4506 (ligne de but à 5120) et la
        /// voiture la suivait jusque dans la cage. Sur une save on veut frapper la balle AUSSI TÔT
        /// que possible, donc le plus loin possible de notre but.</para>
        /// </summary>
        private BallSlice FindInterceptSlice(float beforeTime)
        {
            return Ball.Prediction.Find(s =>
                s.Time < beforeTime
                && s.Time > Game.Time
                && IsDefensibleContact(s.Location)
                && Movement.EtaFor(Me, s.Location) <= s.Time - Game.Time);
        }

        /// <summary>
        /// Enveloppe un ShotCheck en refusant les contacts trop enfoncés dans notre camp.
        ///
        /// <para>Sur une save, `FindShot` balaie les slices de la plus tôt à la plus tard. Si les
        /// premières ne sont pas jouables il retient une slice plus tardive — c'est-à-dire une
        /// balle plus proche de notre but. Comme le point de contact est goal-side de la balle, il
        /// se retrouve alors derrière la ligne, et le bot conduit dans sa propre cage.</para>
        ///
        /// <para>On filtre donc sur la position de la BALLE et sur le point de contact : au-delà,
        /// mieux vaut ne pas trouver de tir et laisser l'interception d'urgence jouer.</para>
        /// </summary>
        private ShotCheck Defensible(ShotCheck inner)
        {
            return (slice, target) =>
            {
                if (!IsDefensibleContact(slice.Location))
                    return null;
                Shot shot = inner(slice, target);
                if (shot == null)
                    return null;
                return IsDefensibleContact(shot.TargetLocation) ? shot : null;
            };
        }

        /// <summary>
        /// Vrai si toucher la balle à cet endroit a encore un sens défensif, c'est-à-dire si la
        /// voiture peut se placer derrière elle sans être elle-même dans le but.
        ///
        /// <para>Sans cette garde, rien n'empêche le bot de poursuivre des slices toujours plus
        /// profondes : chaque tick la balle avance vers la cage, la cible avec elle, et il finit
        /// par conduire dans son propre but. L'ancienne cible de dégagement masquait le problème
        /// par accident — elle plaçait le point de contact côté terrain (donc à l'écart du but),
        /// pour la mauvaise raison qu'elle poussait la balle vers notre cage.</para>
        /// </summary>
        private bool IsDefensibleContact(Vec3 ballLocation)
        {
            float depth = MathF.Abs(ballLocation.y);
            return depth < Field.Length / 2f - LastDefensibleMargin;
        }

        /// <summary>Vrai si on est entre la balle et notre but (contact défensif sûr).</summary>
        private bool IsGoalSide()
        {
            Vec3 towardOurGoal = Ball.Location.FlatDirection(OurGoal.Location);
            return (Me.Location - Ball.Location).Normalize().Dot(towardOurGoal) > 0.2f;
        }

        // En deçà de cette distance de la ligne de but, un contact n'est plus défendable : pour se
        // placer derrière la balle, la voiture devrait entrer dans sa propre cage.
        //
        // Réglé sur le cas observé : le bot poursuivait des points de contact à y=4362 puis 4506
        // (ligne à 5120) et finissait dans le but. Il faut compter 165 uu pour le contact, la
        // longueur de la voiture, et surtout son rayon de virage pour s'y présenter — d'où 900,
        // qui aurait refusé les deux cibles ci-dessus.
        //
        // Au-delà, on ne cherche plus de tir : l'interception d'urgence prend le relais et, si elle
        // ne trouve rien non plus, le bot se poste devant sa cage au lieu d'y plonger.
        private const float LastDefensibleMargin = 900f;

        // Fenêtre de dégagement : large porte posée dans la moitié adverse
        private const float ClearGateDepth = 2000f;      // à quelle profondeur dans leur camp
        private const float ClearGateHalfWidth = 2500f;  // demi-largeur (terrain = ±4096)
        private const float ClearGateHeight = 1000f;

        /// <summary>
        /// Cible de dégagement : une large fenêtre dans la moitié adverse. Envoyer la balle
        /// n'importe où à travers cette porte est un dégagement valable — on ne cherche pas à cadrer.
        ///
        /// <para>Remplace <c>new Target(OurGoal, shootAwayFromGoal: true)</c>, qui ne fait PAS ce que
        /// son nom annonce : inverser les coins du but bascule Target.Clamp dans sa branche "la balle
        /// est derrière la cible", laquelle renvoie <c>balle + direction × 1000</c> avec une direction
        /// rabattue sur un poteau de NOTRE but. Mesuré en jeu (Fixes.DebugShot) : le point de contact
        /// tombait en (172, 5234) — derrière notre propre ligne de but — et la poussée était dirigée
        /// vers notre camp.</para>
        ///
        /// <para>Orientation : les deux coins sont ordonnés pour que la normale de la surface pointe
        /// vers NOTRE moitié. La balle est donc "devant" la cible et Clamp emprunte sa première
        /// branche — celle qu'utilisent les tirs normaux, et qui fonctionne.</para>
        /// </summary>
        private Target ClearTarget()
        {
            int side = Field.Side(Team);
            float gateY = -side * ClearGateDepth;
            return new Target(
                new Vec3(-ClearGateHalfWidth * side, gateY, ClearGateHeight),
                new Vec3(ClearGateHalfWidth * side, gateY, 0f));
        }

        /// <summary>Ramène une position dans les limites du terrain (marge 400), au sol.</summary>
        private static Vec3 ClampToField(Vec3 pos)
        {
            pos.x = Utils.Cap(pos.x, -Field.Width / 2f + 400f, Field.Width / 2f - 400f);
            pos.y = Utils.Cap(pos.y, -Field.Length / 2f + 400f, Field.Length / 2f - 400f);
            pos.z = 0f;
            return pos;
        }

        // --- Banc de mesure de Drive.GetEta (Fixes.DebugEta) ---
        private bool _etaActive;
        private Vec3 _etaTarget;
        private float _etaPredicted;
        private float _etaStartTime;
        private float _etaStartSpeed;
        private float _etaStartBoost;
        private float _etaStartDist;
        private string _etaIntent;
        /// <summary>En deçà de cette distance, on considère la cible atteinte (Drive.Finished utilise 100).</summary>
        private const float EtaArrivedDist = 120f;
        /// <summary>Au-delà de cette dérive de cible, la mesure ne porte plus sur la même chose.</summary>
        private const float EtaAbandonDrift = 400f;

        /// <summary>
        /// Compare l'ETA prédit par Drive.GetEta au temps réellement mis pour atteindre la cible.
        ///
        /// <para>Sans cette mesure, régler GetEta revient à deviner : on ne sait pas si un tir raté
        /// vient d'une estimation trop optimiste (le bot s'engage sur l'impossible) ou d'autre chose.
        /// Chaque ligne [ETA] est un point de mesure exploitable, avec les conditions de départ
        /// (vitesse, boost, distance) pour pouvoir rejouer le cas.</para>
        /// </summary>
        private void TrackEta()
        {
            if (!Fixes.DebugEta)
                return;

            // Cible courante de l'action en cours, quelle qu'elle soit
            Vec3? current = Action switch
            {
                Drive d    => d.Target,
                Arrive a   => a.Target,
                Shot s     => s.TargetLocation,
                GetBoost g => g.ChosenBoost.Location,
                _          => null,
            };

            if (current == null)
            {
                if (_etaActive)
                    ReportEta("ABANDON", "action terminée");
                return;
            }

            Vec3 target = current.Value;

            if (_etaActive && (_intent != _etaIntent || _etaTarget.Dist(target) > EtaAbandonDrift))
            {
                ReportEta("ABANDON", _intent != _etaIntent ? "intent changé" : $"cible déplacée de {_etaTarget.Dist(target):F0}");
            }

            if (!_etaActive)
            {
                _etaActive = true;
                _etaTarget = target;
                _etaIntent = _intent;
                _etaStartTime = Game.Time;
                _etaPredicted = Movement.EtaFor(Me, target);
                _etaStartSpeed = Me.Velocity.Length();
                _etaStartBoost = Me.Boost;
                _etaStartDist = Me.Location.Dist(target);
                return;
            }

            // La cible peut bouger un peu (slice qui s'affine) : on suit sans réinitialiser
            _etaTarget = target;

            if (Me.Location.Dist(target) < EtaArrivedDist)
                ReportEta("ARRIVE", null);
            else if (Game.Time - _etaStartTime > _etaPredicted * 3f + 1f)
                ReportEta("JAMAIS", "abandon après 3x l'ETA prédit");
        }

        private void ReportEta(string outcome, string note)
        {
            float actual = Game.Time - _etaStartTime;
            string head = $"[{Game.Time:F2}s][{Me.Name}#{Index}] [ETA] {outcome} {_etaIntent}";
            string conditions = $"dist0={_etaStartDist:F0} v0={_etaStartSpeed:F0} boost0={_etaStartBoost:F0}";

            if (outcome == "ARRIVE")
            {
                float error = actual - _etaPredicted;
                string pct = _etaPredicted > 0.01f ? $" ({error / _etaPredicted * 100:+0;-0}%)" : "";
                Console.WriteLine($"{head} prevu={_etaPredicted:F2} reel={actual:F2} " +
                    $"erreur={error:+0.00;-0.00}{pct} {conditions}");
            }
            else
            {
                // Mesure interrompue : `actual` est le temps écoulé avant l'interruption, pas un
                // temps d'arrivée. Le comparer à l'ETA prédit produirait une « erreur » énorme et
                // purement fictive — on n'affiche donc aucun écart, seulement la raison.
                Console.WriteLine($"{head} — mesure non conclusive apres {actual:F2}s " +
                    $"(prevu {_etaPredicted:F2}) {conditions}" + (note != null ? $" [{note}]" : ""));
            }
            _etaActive = false;
        }

        private float _lastShotTrace = -1f;
        private string _lastTracedShot = null;

        /// <summary>
        /// Trace un tir en cours (Fixes.DebugShot), 10x/s. Objectif : départager par la mesure les
        /// trois raisons possibles d'un tir raté, au lieu de les supposer.
        ///
        /// <para>• <b>cote</b> — de quel côté de la balle le tir nous fait passer. C'est la projection
        /// de (TargetLocation − Slice.Location) sur la direction balle→NOTRE but.
        /// <b>Positif</b> = on se place entre la balle et notre but, donc on la repousse vers le camp
        /// adverse : c'est ce qu'on veut pour une save. <b>Négatif</b> = on se place côté terrain et on
        /// pousse la balle VERS notre but — il faut alors traverser sa trajectoire pour s'y placer,
        /// ce qui explique un contact manqué de peu.</para>
        ///
        /// <para>• <b>dTgt</b> — distance restante jusqu'au point de contact. Si elle ne descend pas
        /// vers 0 quand tRem→0, le bot n'arrive tout simplement pas : problème de vitesse/trajectoire.</para>
        ///
        /// <para>• <b>derive</b> — écart entre la position où le tir attend la balle et celle que la
        /// prédiction annonce maintenant pour le même instant. Au-delà de 60 uu, ShotValid invalide
        /// le tir (Shot.cs) : la balle n'ira pas là où on l'attendait.</para>
        /// </summary>
        private void TraceShot()
        {
            if (!Fixes.DebugShot)
                return;

            if (Action is not Shot shot)
            {
                _lastTracedShot = null;
                return;
            }

            // Toujours tracer la première frame d'un nouveau tir, puis 10x/s
            string id = $"{_intent}@{shot.Slice.Time:F2}";
            bool isNew = id != _lastTracedShot;
            if (!isNew && Game.Time - _lastShotTrace < 0.1f)
                return;
            _lastShotTrace = Game.Time;
            _lastTracedShot = id;

            float tRem = shot.Slice.Time - Game.Time;

            // De quel côté de la balle le point de contact nous place-t-il ?
            Vec3 ballToOurGoal = shot.Slice.Location.FlatDirection(OurGoal.Location);
            float side = (shot.TargetLocation - shot.Slice.Location).FlatNorm().Dot(ballToOurGoal);

            // Dérive de la prédiction pour l'instant visé
            Vec3 predictedNow = shot.Slice.Location;
            foreach (BallSlice s in Ball.Prediction.Slices)
            {
                if (s.Time >= shot.Slice.Time) { predictedNow = s.Location; break; }
            }
            float drift = predictedNow.Dist(shot.Slice.Location);

            Console.WriteLine($"[{Game.Time:F2}s][{Me.Name}#{Index}] {(isNew ? "NEW " : "    ")}{_intent} " +
                $"tRem={tRem:F2} dTgt={Me.Location.Dist(shot.TargetLocation):F0} v={Me.Velocity.Length():F0} boost={Me.Boost:F0} " +
                $"cote={side:F2} derive={drift:F0} " +
                $"shotTgt=({shot.ShotTarget.x:F0},{shot.ShotTarget.y:F0},{shot.ShotTarget.z:F0}) " +
                $"tgtLoc=({shot.TargetLocation.x:F0},{shot.TargetLocation.y:F0},{shot.TargetLocation.z:F0}) " +
                $"ball=({Ball.Location.x:F0},{Ball.Location.y:F0},{Ball.Location.z:F0})");
        }

        private static string Fmt(float eta) => eta == float.MaxValue ? "∞" : eta.ToString("F2");

        /// <summary>
        /// Diagnostic pour DEF1 : imprimé uniquement quand un NOUVEAU tir est retenu (le latch
        /// ShotInProgress empêche déjà le spam à chaque tick). Si le bot rate encore une save,
        /// ces lignes montrent le slice et la cible exacts choisis par FindShot, et permettent de
        /// voir si deux tirs proches dans le temps ont flip-flop vers des cibles différentes.
        /// </summary>
        private void LogShotPick(string intent, Shot shot)
        {
            float timeRemaining = shot.Slice.Time - Game.Time;
            // Même ETA que celui utilisé par IsValid (alignement compris), sinon la marge affichée
            // est calculée sur un trajet que le bot ne conduira pas et ne veut rien dire.
            float carEta = Drive.GetEta(Me, shot.TargetLocation, shot.ShotDirection.FlatNorm());
            // Vitesse à laquelle Arrive va se caler pour arriver pile à l'heure (Arrive.cs:59).
            // C'est elle qui décide si le bot boost ou se laisse rouler : sous 1400, aucun boost.
            float paceSpeed = Drive.GetDistance(Me, shot.TargetLocation) / MathF.Max(timeRemaining, 0.001f);
            Console.WriteLine($"[{Game.Time:F2}s][{Me.Name}#{Index}] {intent} → {shot.GetType().Name} " +
                $"slice@{shot.Slice.Time:F2}s loc=({shot.Slice.Location.x:F0},{shot.Slice.Location.y:F0},{shot.Slice.Location.z:F0}) " +
                $"target=({shot.TargetLocation.x:F0},{shot.TargetLocation.y:F0},{shot.TargetLocation.z:F0}) " +
                $"carEta={carEta:F2} tRem={timeRemaining:F2} marge={timeRemaining - carEta:F2} " +
                $"vArrivee={paceSpeed:F0} boost={Me.Boost:F0}");
        }

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
