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
        /// <summary>
        /// Moteur d'évaluation des tirs. Piloté par <see cref="Fixes.RocketSimShotCheck"/> —
        /// l'ancienne constante <c>AccuratePhysics</c> imposait de recompiler pour changer.
        /// </summary>
        private ShotCheck CurrentShotCheck => Fixes.RocketSimShotCheck ? AccurateShotCheck : DefaultShotCheck;

        private GameStateMode _lastState;
        private FieldZone _lastZone;
        private Role? _lastRole;
        /// <summary>Instant où l'on a perdu le rôle d'Attacker (contest/tir joué) — une rotation est due.
        /// -1 = rien en attente. Latché parce que la bascule ne dure qu'un tick et tombe souvent
        /// pendant une action non-interruptible (Kickoff, Shot), où SelectAction ne décide pas.</summary>
        private float _rotationPendingTime = -1f;
        /// <summary>Au-delà, la bascule est trop vieille pour justifier encore une rotation.</summary>
        private const float RotationTriggerWindow = 2f;
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
        // Distance goal-side de la balle visée pour CONTESTER en défense (2v2, Support qui couvre) :
        // plus serrée que le pressing offensif — on ferme sur le porteur pour déclencher le Fifty.
        private const float ContestGap = 500f;
        // Plafond d'anticipation du contest. On vise où sera la balle quand on l'aura RÉELLEMENT
        // rejointe (lead = notre ETA vers la balle), pas un temps fixe : plus on est loin, plus
        // l'adversaire l'aura déplacée avant notre arrivée. Capé ici car au-delà l'extrapolation
        // linéaire d'un dribble (l'adversaire tourne/tire) ne veut plus rien dire.
        private const float ContestMaxLead = 2.5f;
        // En deçà de cette distance adversaire→balle, on considère qu'il la contrôle (ou est assez
        // près pour la disputer) : on anticipe son accélération possible vers notre but.
        private const float ContestCarryDistance = 500f;
        // Avancée goal-side maximale anticipée pour le contest : borne le standoff pour qu'on tienne
        // une ligne devant le porteur sans s'effondrer dans notre propre but quand on est loin.
        private const float ContestMaxAdvance = 1800f;
        // Hystérésis de collecte de boost du Support : entre sous Low, sort à High
        private const float SupportBoostLow = 30f;
        private const float SupportBoostHigh = 60f;
        private bool _collectingBoost;

        // --- Challenge d'un dribble adverse plutôt qu'une save passive (Fixes.ChallengeOverDriveSave) ---
        // En deçà de cette distance à la balle, pour NOUS comme pour l'adversaire le plus proche, on
        // considère qu'on est tous deux « sur la balle » : c'est un 50/50 à disputer, pas une
        // trajectoire à intercepter en reculant.
        private const float FiftyChallengeRange = 600f;
        // Balle plus haute que ça = ce n'est plus un dribble au sol contestable par un Fifty plat.
        private const float ChallengeMaxBallHeight = 300f;

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

            // ... et une mesure du banc wavedash
            _wdRunning = false;
            _wdDone = false;
            _wdLeftGround = false;
            _wdSettleTime = 0f;

            // ... et une mesure du banc rotation. SANS ce reset, _rotBenchDone reste vrai après le
            // premier scénario : RunRotationBench sort aussitôt, ne remet jamais Action à null, et
            // le framework continue d'exécuter l'ANCIENNE Rotate — plan (pad, destination) calculé
            // pour la pose précédente, alors que la voiture vient d'être téléportée ailleurs.
            _rotBenchRunning = false;
            _rotBenchDone = false;
            _rotBenchAction = null;

            _stableState = GameStateMode.Contested;
            _pendingState = GameStateMode.Contested;
            _pendingSince = -1f;
            _lastRole = null;
            _collectingBoost = false;
            _lastBoostCheckTime = -1f;
            _lastLoggedTouchTime = -1f;

            _saveTargetTime = -1f;
            _saveArrivalTime = -1f;

            // Force the next Run() to log the fresh state instead of staying silent because
            // gameState/zone/role/intent happen to match what was latched before the reset.
            _lastState = (GameStateMode)(-1);
            _lastZone = (FieldZone)(-1);
            _lastIntent = null;

            // Marqueur « début de scénario » : dumpe la pose exacte posée par le state setter, pour
            // pouvoir relier un comportement bizarre à ses conditions de départ sans les deviner.
            // yaw = cap de la voiture en degrés (0 = +x, 90 = +y vers le but orange).
            if (Fixes.DebugShot)
            {
                if(Me.Name == "MyBot")
                {
                    float yaw = MathF.Atan2(Me.Forward.y, Me.Forward.x) * 180f / MathF.PI;
                Console.WriteLine($"[{Game.Time:F2}s][{Me.Name}#{Index}] ===== STATE SET ===== " +
                    $"car=({Me.Location.x:F0},{Me.Location.y:F0}) yaw={yaw:F0}° v={Me.Velocity.Length():F0} boost={Me.Boost:F0} | " +
                    $"ball=({Ball.Location.x:F0},{Ball.Location.y:F0},{Ball.Location.z:F0}) " +
                    $"ballV=({Ball.Velocity.x:F0},{Ball.Velocity.y:F0},{Ball.Velocity.z:F0})");
                }
                
            }
        }

        /// <summary>
        /// Pointe un Drive vers la cible en réutilisant l'action en cours si possible.
        /// Recréer un Drive à chaque tick remet son timeOnGround à zéro (Drive.cs:160),
        /// ce qui interdit dodges/speedflips/wavedashes — le bot roule alors à vitesse de base.
        /// </summary>
        private void SetDrive(Vec3 target, string intent, bool allowDodges = true, bool wasteBoost = false)
        {
            if (Action is Drive drive && _intent == intent && drive.AllowDodges == allowDodges
                && drive.WasteBoost == wasteBoost && drive.Target.Dist(target) < RetargetDistance)
            {
                drive.Target = target;
                return;
            }
            SetAction(new Drive(Me, target, allowDodges: allowDodges, wasteBoost: wasteBoost), intent);
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
        /// Se replace sur <paramref name="destination"/> en passant par un pad de boost s'il s'en
        /// trouve un quasiment sur le chemin (AUDIT §2.4). Un repli est un trajet qu'on fait de
        /// toute façon : le boost ramassé dessus ne coûte que le détour.
        ///
        /// <para>Le trajet vers le pad utilise <c>Drive</c> et non <c>Arrive</c> : Arrive décale sa
        /// cible en arrière pour s'aligner sur une direction d'arrivée (Arrive.cs:82), ce qui ferait
        /// passer À CÔTÉ du pad. On veut le traverser, pas s'y présenter proprement.</para>
        ///
        /// <para>Le changement d'intent au moment du ramassage est voulu : c'est un vrai changement
        /// de phase, pas le clignotement que <c>SetDrive</c>/<c>SetArrive</c> cherchent à éviter.
        /// <c>RetreatBoost</c> est stable pendant l'approche (le détour tend vers 0 à mesure qu'on
        /// s'en rapproche) et le pad sort du calcul dès qu'il est pris, le boost passant au-dessus
        /// du plafond de recherche.</para>
        /// </summary>
        private void SetArriveVia(Vec3 destination, string intent)
        {
            Boost detour = Rotation.RetreatBoost(Me, destination);
            if (detour != null)
            {
                SetDrive(detour.Location, intent + "+Boost");
                return;
            }

            SetArrive(destination, destination.FlatDirection(Ball.Location), intent);
        }

        /// <summary>
        /// Tient un poste (<see cref="Cover"/>) en réutilisant l'action en cours si possible, et en
        /// passant par un pad de boost s'il s'en trouve un sur le chemin (§2.4).
        ///
        /// <para>Même latch que <see cref="SetDrive"/> : recréer l'action remettrait à zéro le
        /// <c>timeOnGround</c> de son Drive interne, et surtout son état de tenue — la voiture
        /// repartirait en approche alors qu'elle est déjà en place.</para>
        /// </summary>
        private void SetCover(Vec3 target, string intent)
        {
            Boost detour = Rotation.RetreatBoost(Me, target);
            if (detour != null)
            {
                SetDrive(detour.Location, intent + "+Boost");
                return;
            }

            if (Action is Cover cover && _intent == intent && cover.Target.Dist(target) < RetargetDistance)
            {
                cover.Target = target;
                cover.FacePoint = Ball.Location;
                return;
            }

            SetAction(new Cover(Me, target, Ball.Location), intent);
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

        // --- Banc de mesure du Wavedash (Fixes.WavedashBench) ---
        private Wavedash _wdAction;
        private bool _wdRunning;
        private bool _wdDone;
        private bool _wdLeftGround;
        private float _wdStartTime;
        private float _wdStartSpeed;
        private float _wdStartBoost;
        private float _wdPeakSpeed;
        private bool _wdReference;
        private float _wdRefBoostTime;
        private Vec3 _wdStartLoc;
        private float _wdSettleTime;
        /// <summary>Seule cette voiture mesure (PLAYER_ORANGE1 dans le script de test). Sans ce
        /// filtre, les 4 voitures du match — qui tournent toutes ce code — déclenchent chacune leur
        /// propre wavedash et polluent la sortie. Changer si le scénario déplace une autre voiture.</summary>
        private const int WavedashBenchCarIndex = 2;

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

        /// <summary>
        /// Le script de test choisit « conduite classique de référence » (au lieu du wavedash) via le
        /// boost du coéquipier garé, comme le banc ETA : 0 = wavedash, ≥ 50 = référence. Évite de
        /// recompiler entre les deux courses qu'on veut comparer.
        /// </summary>
        private bool BenchWavedashReference()
        {
            List<Car> mates = Teammates;
            return mates.Count > 0 && mates[0].Boost >= 50;
        }

        /// <summary>
        /// Mesure UNE manœuvre de wavedash, depuis la vitesse initiale imposée par le state setter,
        /// throttle à fond et sans jamais demander de boost. La mesure s'arrête PILE à l'atterrissage
        /// (fin de la manœuvre) — on ne mesure que le wavedash lui-même.
        ///
        /// <para>Sortie (ligne FIN) : vitesse de départ / d'arrivée (gain), pic, boost consommé (~0),
        /// durée = temps de NON-DISPONIBILITÉ (Drive.cs impose +0.2s de timeOnGround avant de relancer
        /// un flip), et distance parcourue PENDANT la manœuvre.</para>
        ///
        /// <para>Mode REFERENCE (canal boost coéquipier) : conduite classique, throttle seul, mesurée
        /// sur la durée NOMINALE d'un wavedash (Wavedash.Duration ≈ 1.0s), pour comparer à v0 égale la
        /// distance parcourue avec/sans wavedash — et savoir si le wavedash gagne du terrain.</para>
        /// </summary>
        private void RunWavedashBench()
        {
            // Toutes les voitures du match tournent ce code : on ne mesure que la voiture désignée
            // par le scénario, sinon les 3 autres (garées) impriment chacune leur propre course.
            if (Index != WavedashBenchCarIndex)
                return;

            if (_wdDone)
                return;

            if (!_wdRunning)
            {
                // Le state setter téléporte la voiture à z=17 : elle REBONDIT encore quelques ticks.
                // Lancer le wavedash sur une voiture pas stabilisée corrompt le saut/dodge (bond raté).
                // On attend donc qu'elle soit posée ET verticalement calme, de façon continue, avant de
                // démarrer — sinon la mesure ne vaut rien.
                if (!Me.IsGrounded || MathF.Abs(Me.Velocity.z) > 50f)
                {
                    _wdSettleTime = 0f;
                    return;
                }
                _wdSettleTime += DeltaTime;
                if (_wdSettleTime < 0.15f)
                    return;

                _wdReference = BenchWavedashReference();
                _wdStartTime = Game.Time;
                _wdStartLoc = Me.Location;
                _wdStartSpeed = Me.Velocity.FlatLen();
                _wdStartBoost = Me.Boost;
                _wdPeakSpeed = _wdStartSpeed;
                _wdLeftGround = false;
                _wdRunning = true;

                if (_wdReference)
                {
                    // Conduite classique : aucune action, throttle seul (réglé plus bas). Le canal (boost
                    // coéquipier > 50) impose un temps de boost : (boost - 50) / 100 secondes.
                    _wdRefBoostTime = Teammates.Count > 0 ? MathF.Max(0f, (Teammates[0].Boost - 50f) / 100f) : 0f;
                    _wdAction = null;
                    Action = null;
                }
                else
                {
                    _wdAction = new Wavedash(Me.Forward, Fixes.WavedashBenchBoost);
                    Action = _wdAction;
                }

                Console.WriteLine($"[WDBENCH] DEPART mode={(_wdReference ? "REFERENCE" : "wavedash")} " +
                    $"v0={_wdStartSpeed:F0} boost0={_wdStartBoost:F0}" +
                    (_wdReference ? $" refBoost={_wdRefBoostTime:F2}s" : ""));
            }

            // Throttle à fond, jamais de boost. En mode wavedash, l'action pilote saut/dodge par-dessus
            // ce throttle ; en mode référence, la voiture avance simplement tout droit.
            Controller.Throttle = 1;

            if (!Me.IsGrounded)
                _wdLeftGround = true;
            _wdPeakSpeed = MathF.Max(_wdPeakSpeed, Me.Velocity.FlatLen());

            float elapsed = Game.Time - _wdStartTime;

            // Référence avec temps de boost imposé : on boost les premières secondes puis on coast.
            if (_wdReference && elapsed < _wdRefBoostTime)
                Controller.Boost = true;

            if (!_wdReference)
            {
                // WAVEDASH : on s'arrête PILE à l'atterrissage (fin de manœuvre), et on ne mesure que
                // le wavedash — vitesse, gain, pic, boost, durée de non-dispo, distance parcourue.
                if (_wdAction.Finished && _wdLeftGround)
                {
                    float vFin = Me.Velocity.FlatLen();
                    float dist = Me.Location.FlatDist(_wdStartLoc);
                    float boostUsed = _wdStartBoost - Me.Boost;
                    Console.WriteLine($"[WDBENCH] FIN wavedash v0={_wdStartSpeed:F0} vFin={vFin:F0} " +
                        $"gain={vFin - _wdStartSpeed:+0;-0} vPic={_wdPeakSpeed:F0} boostUtilise={boostUsed:F0} " +
                        $"duree={elapsed:F3}s (=non-dispo, +0.2s avant relance Drive) dist={dist:F0}");
                    _wdDone = true;
                    Action = null;
                }
                else if (elapsed > 3f)
                {
                    Console.WriteLine($"[WDBENCH] TIMEOUT wavedash v0={_wdStartSpeed:F0} pas d'atterrissage " +
                        $"apres {elapsed:F2}s (leftGround={(_wdLeftGround ? "oui" : "non")}) — wavedash au sol casse ?");
                    _wdDone = true;
                    Action = null;
                }
            }
            else
            {
                // REFERENCE : conduite classique, mesurée sur une fenêtre fixe de 1s (indépendante de
                // Wavedash.Duration qui varie désormais selon la variante), pour comparer à v0 égale.
                float refDuration = 1f;
                if (elapsed >= refDuration)
                {
                    float dist = Me.Location.FlatDist(_wdStartLoc);
                    float vNow = Me.Velocity.FlatLen();
                    Console.WriteLine($"[WDBENCH] FIN REFERENCE v0={_wdStartSpeed:F0} v={vNow:F0} dist={dist:F0} " +
                        $"refBoost={_wdRefBoostTime:F2}s boostUtilise={_wdStartBoost - Me.Boost:F0} " +
                        $"(conduite classique sur {refDuration:F2}s)");
                    _wdDone = true;
                    Action = null;
                }
            }
        }

        // --- Banc de mesure de la ROTATION (Fixes.RotationBench) ---
        // Seule cette voiture mesure et imprime : les 4 tournent le même bot. Filtré sur le NOM et
        // non sur l'index, comme Rotate.Debug et le reste des traces : avec un filtre d'index, le
        // banc mesurait une voiture GARÉE pendant que les lignes [Rotate] venaient d'une autre.
        private const string RotationBenchCarName = "MyBot";
        // En dessous, on considère la vitesse « cassée » — c'est ce que la rotation doit éviter.
        private const float RotationBenchSlowSpeed = 1000f;
        private const float RotationBenchTimeout = 12f;

        private Rotate _rotBenchAction;
        private bool _rotBenchRunning;
        private bool _rotBenchDone;
        private float _rotBenchStartTime;
        private float _rotBenchStartSpeed;
        private float _rotBenchStartBoost;
        private Vec3 _rotBenchStartLoc;
        private float _rotBenchMinSpeed;
        private float _rotBenchMaxSpeed;
        private float _rotBenchSpeedSum;
        private int _rotBenchSamples;
        private float _rotBenchSlowTime;
        private float _rotBenchBoostCollected;
        private float _rotBenchLastBoost;
        private bool _rotBenchHadPad;

        /// <summary>
        /// Mesure UNE sortie de rotation, sans aucune stratégie : le bot ne joue pas la balle, il
        /// exécute juste une <see cref="Rotate"/> depuis la pose imposée par le state setter.
        ///
        /// <para>La question à laquelle ce banc répond est <b>« garde-t-on la vitesse ? »</b> — d'où
        /// la métrique centrale <c>vMin</c> (vitesse la plus basse du trajet) et <c>tempsLent</c>
        /// (temps passé sous <see cref="RotationBenchSlowSpeed"/>). Si l'arc est trop serré,
        /// <c>ArcMaxAngle</c> est à baisser et ça se voit immédiatement sur ces deux colonnes.</para>
        ///
        /// <para>La balle n'est PAS jouée : elle sert seulement de marqueur, puisque la destination
        /// (<c>Rotation.DefensivePosition</c>) en dépend. Elle est lue UNE fois au départ, puis la
        /// destination est figée — le trajet mesuré ne bouge donc plus.</para>
        /// </summary>
        private void RunRotationBench()
        {
            if (Me.Name != RotationBenchCarName || _rotBenchDone)
                return;

            if (!_rotBenchRunning)
            {
                Vec3 dest = Rotation.DefensivePosition(OurGoal);
                // Même règle qu'en jeu : on tourne par le côté opposé à celui où l'on est.
                int side = Me.Location.x >= 0f ? -1 : 1;

                _rotBenchAction = new Rotate(Me, dest, side, OurGoal);
                _rotBenchStartTime = Game.Time;
                _rotBenchStartSpeed = Me.Velocity.FlatLen();
                _rotBenchStartBoost = Me.Boost;
                _rotBenchStartLoc = Me.Location;
                _rotBenchLastBoost = Me.Boost;
                _rotBenchMinSpeed = float.MaxValue;
                _rotBenchMaxSpeed = 0f;
                _rotBenchSpeedSum = 0f;
                _rotBenchSamples = 0;
                _rotBenchSlowTime = 0f;
                _rotBenchBoostCollected = 0f;
                _rotBenchHadPad = _rotBenchAction.Pad != null;
                _rotBenchRunning = true;

                string pad = _rotBenchAction.Pad == null
                    ? "AUCUN (aucun gros pad sous 45°)"
                    : $"({_rotBenchAction.Pad.Location.x:F0},{_rotBenchAction.Pad.Location.y:F0})";
                Console.WriteLine($"[ROTBENCH] DEPART v0={_rotBenchStartSpeed:F0} boost0={_rotBenchStartBoost:F0} " +
                    $"pos=({Me.Location.x:F0},{Me.Location.y:F0}) cote={(side > 0 ? "+x" : "-x")} " +
                    $"dest=({dest.x:F0},{dest.y:F0}) pad={pad}");
            }

            // Échantillonnage : c'est la vitesse MINIMALE qui juge la rotation, pas la moyenne.
            float v = Me.Velocity.FlatLen();
            _rotBenchMinSpeed = MathF.Min(_rotBenchMinSpeed, v);
            _rotBenchMaxSpeed = MathF.Max(_rotBenchMaxSpeed, v);
            _rotBenchSpeedSum += v;
            _rotBenchSamples++;
            if (v < RotationBenchSlowSpeed)
                _rotBenchSlowTime += DeltaTime;
            if (Me.Boost > _rotBenchLastBoost)
                _rotBenchBoostCollected += Me.Boost - _rotBenchLastBoost;
            _rotBenchLastBoost = Me.Boost;

            if (Action is not Rotate)
                Action = _rotBenchAction;

            float elapsed = Game.Time - _rotBenchStartTime;
            bool timedOut = elapsed > RotationBenchTimeout;

            if (_rotBenchAction.Finished || timedOut)
            {
                float avg = _rotBenchSamples > 0 ? _rotBenchSpeedSum / _rotBenchSamples : 0f;
                float slowPct = elapsed > 0.01f ? _rotBenchSlowTime / elapsed * 100f : 0f;
                Console.WriteLine($"[ROTBENCH] {(timedOut ? "TIMEOUT" : "FIN")} duree={elapsed:F2}s " +
                    $"v0={_rotBenchStartSpeed:F0} vMin={_rotBenchMinSpeed:F0} vMoy={avg:F0} vMax={_rotBenchMaxSpeed:F0} " +
                    $"vFin={Me.Velocity.FlatLen():F0} tempsLent={_rotBenchSlowTime:F2}s ({slowPct:F0}%) " +
                    $"boostPris=+{_rotBenchBoostCollected:F0} boostFin={Me.Boost:F0} " +
                    $"dist={_rotBenchStartLoc.FlatDist(Me.Location):F0} " +
                    $"padVise={(_rotBenchHadPad ? "oui" : "non")} " +
                    $"resteAFaire={Me.Location.FlatDist(_rotBenchAction.FinalTarget):F0}");
                _rotBenchDone = true;
                Action = null;
            }
        }

        public override void Run()
        {
            if (Fixes.RotationBench)
            {
                RunRotationBench();
                return;
            }

            if (Fixes.WavedashBench)
            {
                RunWavedashBench();
                return;
            }

            if (Fixes.EtaBench)
            {
                RunEtaBench();
                return;
            }

            if (Ball.LatestTouch != null && Ball.LatestTouch.Time != _lastLoggedTouchTime && Ball.LatestTouch.PlayerIndex == Index)
            {
                if(Me.Name == "MyBot")
                {
                    Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}] TOUCHE la balle à ({Ball.LatestTouch.Location.x:F0},{Ball.LatestTouch.Location.y:F0},{Ball.LatestTouch.Location.z:F0}) intent={_intent ?? "none"}");
                    _lastLoggedTouchTime = Ball.LatestTouch.Time;
                }
            }

            GameStateMode rawState = Rotation.ComputeGameState(Me, LivingTeammates, LivingOpponents,
                out float ourEta, out float theirEta, out float oppDist);
            GameStateMode gameState = StabilizeState(rawState);
            Role? role = LivingTeammates.Count == 1
                ? Rotation.ComputeRole(Me, LivingTeammates[0], TheirGoal, _lastRole)
                : null;

            // Sortie de rotation : on vient de perdre le rôle d'Attacker, le contest/tir est joué.
            // Détecté ICI et non dans SelectAction : la bascule ne dure qu'un tick, et elle tombe
            // typiquement pendant une action NON-INTERRUPTIBLE (Kickoff, Shot) où SelectAction ne
            // re-décide pas — le front serait purement et simplement perdu (mesuré : role=Support
            // arrive pendant intent=Kickoff, et au tick suivant _lastRole vaut déjà Support).
            if (_lastRole == Role.Attacker && role == Role.Support)
                _rotationPendingTime = Game.Time;
            else if (role != Role.Support)
                _rotationPendingTime = -1f;

            if (DebugMode)
                DrawDebug(gameState, role);

            FieldZone fieldZone = Rotation.ComputeFieldZone(OurGoal);

            // Toutes les 0.5s : interrompre GetBoost si on est Attacker et que la balle est plus proche que le pad
            if (Action is GetBoost runningBoost && runningBoost.Found && role != Role.Support
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
                if(Me.Name == "MyBot")
                {
                    Console.WriteLine($"[{Game.Time:F1}s][{Me.Name}] state={gameState}{raw} zone={fieldZone} role={role?.ToString() ?? "-"} intent={_intent ?? "none"}{runningAction} boost={Me.Boost:F0} dist={Me.Location.Dist(Ball.Location):F0} eta={Fmt(ourEta)}/{Fmt(theirEta)} oppDist={Fmt(oppDist)} ballV={Ball.Velocity.Length():F0} ballZ={Ball.Location.z:F0} challenge={(IsCloseContestGoalSide() ? "oui" : "non")} lastTouch={(Ball.LatestTouch == null ? "-" : Ball.LatestTouch.Team == Me.Team ? "nous" : "eux")}");
                }
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

                    // GetBoost.Found est faux quand aucun pad candidat n'est utilisable (tous en
                    // cooldown au-delà de notre ETA). On retombe alors sur l'ensemble des gros pads,
                    // puis sur le kickoff — plutôt que d'assigner une action inerte (AUDIT §0.1).
                    GetBoost kickoffBoost = ourBoosts.Count > 0
                        ? new GetBoost(Me, ourBoosts, interruptible: false)
                        : new GetBoost(Me, interruptible: false);
                    if (!kickoffBoost.Found && ourBoosts.Count > 0)
                        kickoffBoost = new GetBoost(Me, interruptible: false);

                    if (kickoffBoost.Found)
                        SetAction(kickoffBoost, "GetBoost(kickoff)");
                    else
                        SetAction(new Kickoff(), "Kickoff");
                }
            }
            else if (Action == null || Action.Interruptible)
            {
                ShotCheck shotCheck = CurrentShotCheck;

                // Priorités défensives (Fixes.DefensiveOverhaul) :
                // save sur tir cadré (tous rôles), dégagement + discipline goal-side (Attacker)
                if (TryDefensivePriority(shotCheck, gameState, fieldZone, role))
                    return;

                if (role == Role.Support)
                {
                    // --- Sortie de rotation (Fixes.RotationMode) ---
                    // Une Rotate en cours n'est PAS re-décidée : c'est tout son intérêt. On se contente
                    // de suivre la destination, sinon on retombe dans le re-ciblage permanent qui
                    // empêchait de tenir une vitesse.
                    if (Action is Rotate running && !running.Finished)
                    {
                        running.FinalTarget = RotationDestination(gameState, fieldZone);
                        return;
                    }

                    // On vient de lâcher le rôle d'Attacker : le contest/tir est joué, on sort.
                    // Le latch (posé dans Run) survit aux actions non-interruptibles ; la fenêtre
                    // évite de déclencher une rotation sur une bascule devenue trop ancienne.
                    // Condition : le coéquipier est bien replacé goal-side. S'il ne l'est PAS, on est
                    // le dernier recours — trajectoire courte, replacement classique ci-dessous.
                    if (Fixes.RotationMode && _rotationPendingTime >= 0f
                        && Game.Time - _rotationPendingTime < RotationTriggerWindow
                        && LivingTeammates.Count == 1 && Rotation.IsGoalSide(LivingTeammates[0]))
                    {
                        _rotationPendingTime = -1f;
                        // On tourne par le côté OPPOSÉ à celui où l'on vient de contester (= le nôtre).
                        int rotationSide = Me.Location.x >= 0f ? -1 : 1;
                        SetAction(new Rotate(Me, RotationDestination(gameState, fieldZone), rotationSide, OurGoal), "Rotation");
                        return;
                    }

                    // Hystérésis 30/60 : sans bande morte le Support oscille entre collecte et placement
                    if (Me.Boost < SupportBoostLow) _collectingBoost = true;
                    else if (Me.Boost >= SupportBoostHigh) _collectingBoost = false;

                    // Ils ont la balle DANS NOTRE MOITIÉ : le Support est le dernier homme, il couvre
                    // le but — boost ou pas. Balle dans leur moitié : pas de danger immédiat, il monte
                    // en soutien (BackupPosition, plus bas) au lieu d'abandonner le terrain.
                    if ((gameState == GameStateMode.NotPossessed || gameState == GameStateMode.Contested)
                        && (fieldZone == FieldZone.Defensive))
                    {
                        // Dernier homme : on TIENT le poste (arrêt + nez vers la balle) au lieu de
                        // le traverser à pleine vitesse, en ramassant un pad s'il est sur la route.
                        if (Fixes.GoalieCover)
                            SetCover(Rotation.DefensivePosition(OurGoal), "Couverture");
                        else
                            SetArriveVia(Rotation.DefensivePosition(OurGoal), "Arrive→Couverture");
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
                            // Found est faux si tous les pads goal-side sont en cooldown au-delà de
                            // notre ETA : on ne pose alors PAS l'action (elle serait inerte) et on
                            // enchaîne sur le replacement (AUDIT §0.1).
                            GetBoost collect = new GetBoost(Me, safeBoosts);
                            if (collect.Found)
                            {
                                SetAction(collect, "GetBoost");
                                return;
                            }
                        }
                        // Aucun pad sûr : on se replace quand même, tant pis pour le boost
                    }

                    // Position de soutien basée sur la balle (goal-side + back post), face au jeu
                    SetArriveVia(Rotation.BackupPosition(OurGoal), "Arrive→BackupPos");
                    return;
                }

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
                            SetDrive(pressTarget, "Drive→Pressing", wasteBoost: true);
                        }
                        else if (role == Role.Attacker)
                        {
                            // 2v2 : le premier homme CONTESTE la balle au lieu de shadow — le Support
                            // couvre déjà le but (branche Support → DefensivePosition). En 1v1 (role == null,
                            // pas de coéquipier vivant) on garde le shadow : sans couverture derrière, on
                            // contient. Cible = point d'interception anticipé (ContestPoint), goal-side du
                            // porteur (anti-CSC) ; le Fifty prend le relais dès qu'on est à portée.
                            // Cible = point d'interception anticipé (ContestPoint) qui tient compte de
                            // l'accélération POSSIBLE du porteur : on vise là où la balle pourrait être quand
                            // on l'aura rejointe, pas où elle est. Le contact tombe alors au bon endroit même
                            // si l'adversaire lance la balle — inutile de brider le flip, la cible est juste.
                            SetDrive(ClampToField(ContestPoint()), "Drive→Contest", wasteBoost: true);
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
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false, wasteBoost: true);
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
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false, wasteBoost: true);
                        }
                        break;

                    case GameStateMode.Possessed:
                        if (fieldZone == FieldZone.Offensive)
                        {
                            if (ShotInProgress("Shot→LeurBut"))
                                break;

                            // On a la balle et du temps : Patient refuse les slices mal alignées
                            // tant que l'angle s'améliore, pour ne pas tirer du côté (AUDIT §2.2).
                            Shot offensiveShot = FindShot(Patient(shotCheck), new Target(TheirGoal));
                            if (offensiveShot != null)
                            {
                                LogShotPick("Shot→LeurBut", offensiveShot);
                                SetAction(offensiveShot, "Shot→LeurBut");
                            }
                            else
                                // Aucun tir bien orienté pour l'instant : on continue d'avancer sur la
                                // balle, le tir se déclenchera dès que l'alignement sera bon.
                                SetDrive(Ball.Location, "Drive→Balle", allowDodges: false, wasteBoost: true);
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
        /// Où une rotation se termine : le même point que celui où le Support se serait placé, pour
        /// que la fin de la rotation et le replacement classique visent la même chose.
        /// </summary>
        private Vec3 RotationDestination(GameStateMode gameState, FieldZone fieldZone)
        {
            bool lastMan = (gameState == GameStateMode.NotPossessed || gameState == GameStateMode.Contested)
                           && fieldZone == FieldZone.Defensive;
            return lastMan ? Rotation.DefensivePosition(OurGoal) : Rotation.BackupPosition(OurGoal);
        }

        /// <summary>
        /// Priorités défensives (Fixes.DefensiveOverhaul). Retourne true si une action a été choisie.
        /// Ordre : 1) SAVE si la prédiction voit la balle entrer dans notre but (tous rôles),
        /// 2) DÉGAGEMENT si la balle est dangereuse dans notre zone (Attacker),
        /// 3) GOAL-SIDE si on n'est pas entre la balle et notre but (Attacker) — anti-CSC.
        /// </summary>
        private bool TryDefensivePriority(ShotCheck shotCheck, GameStateMode gameState, FieldZone fieldZone, Role? role)
        {
            if (!Fixes.DefensiveOverhaul)
                return false;

            // --- 1) Tir cadré : la prédiction voit la balle franchir NOTRE ligne ---
            // FindGoal(team) = balle marquant EN FAVEUR de team → notre but encaisse pour team adverse
            BallSlice goalSlice = Ball.Prediction.FindGoal(1 - Me.Team);

            // Le save ultime est réservé à l'Attacker (et au solo, role == null). Avec la règle de
            // proximité de ComputeScore, l'Attacker EST le plus proche de la balle : c'est donc lui
            // qui va au save/challenge, pendant que le Support tient la couverture du but. Sans cette
            // garde, les deux bots déclenchaient la save en même temps et convergeaient sur la balle.
            if (goalSlice != null && role != Role.Support)
            {
                // La prédiction voit un but PARCE QUE l'adversaire porte la balle vers notre cage
                // (elle ignore sa voiture et extrapole tout droit). Mais si c'est un 50/50 à nos pieds
                // et qu'on est goal-side, temporiser sur l'interception (Arrive→Save) laisse le porteur
                // frapper le premier : on CHALLENGE. Placé avant les deux circuits de save — la décision
                // de disputer ne dépend pas de Fixes.UnifiedSave. Fifty reste interruptible : si la
                // trajectoire devient un tir cadré imparable, on repasse en save au tick suivant.
                if (Fixes.ChallengeOverDriveSave && IsCloseContestGoalSide())
                {
                    if (Action is not Fifty)
                        SetAction(new Fifty(), "Fifty");
                    return true;
                }

                // Nouveau circuit unifié (Fixes.UnifiedSave) : l'action Save se place goal-side et
                // frappe selon la hauteur en renvoyant la balle vers le camp adverse.
                if (Fixes.UnifiedSave)
                {
                    if (Action is not Save)
                        SetAction(new Save(OurGoal, ClearAimPoint()), "Save");
                    return true;
                }

                // Ancien circuit (défaut) : tir dirigé si FindShot en trouve un jouable, sinon
                // interception d'urgence sur la trajectoire — la voiture se met goal-side dans le
                // chemin de la balle. Cible latchée : on garde la dernière interception connue quand
                // un tick n'en trouve pas, au lieu de sauter au repli près-but (anti-oscillation).
                if (ShotInProgress("Shot→Save"))
                    return true;

                Shot save = FindShot(Defensible(shotCheck), ClearTarget());
                if (save != null)
                {
                    SetAction(save, "Shot→Save");
                    return true;
                }

                // Cible = première slice atteignable avec marge de confort (au-devant de la balle),
                // et on note l'instant où la balle y sera : c'est lui qui donne la cadence à l'Arrive.
                BallSlice intercept = FindSaveInterceptSlice(goalSlice.Time);
                Vec3 saveTarget;
                float saveArrivalTime;
                if (intercept != null)
                {
                    saveTarget = GoalSideContact(intercept.Location, intercept.Velocity);
                    saveArrivalTime = intercept.Time;
                    _saveTarget = saveTarget;
                    _saveArrivalTime = saveArrivalTime;
                    _saveTargetTime = Game.Time;
                }
                else if (Game.Time - _saveTargetTime < SaveLatchTime)
                {
                    saveTarget = _saveTarget;
                    saveArrivalTime = _saveArrivalTime;
                }
                else
                {
                    // Aucune interception : repli près-but, sans cadence imposée (arrivalTime < 0).
                    saveTarget = OurGoal.Location + OurGoal.Location.FlatDirection(Ball.Location) * 300f;
                    saveArrivalTime = -1f;
                }

                // Arrive (et non Drive) : il DOSE sa vitesse (distance / temps restant) pour arriver
                // PILE à saveArrivalTime au lieu de foncer et de dépasser la balle.
                //
                // SANS direction d'arrivée (null) : la mise en ligne d'Arrive recule le point
                // d'approche de ~0.6× la distance À CONTRE-SENS de la direction visée — donc vers
                // NOTRE but — et le plante DANS LE FILET sur un save profond (Arrive.cs:82). Ce shift
                // ne s'active en plus que lorsqu'on temporise (targetSpeed < 2200), soit exactement
                // notre cas. On garde donc le seul pacing et on vise le point de contact directement :
                // il est déjà DEVANT la ligne (ContactInFrontOfGoal a filtré le slice), et comme il est
                // goal-side de la balle, le contact la renvoie vers le terrain sans qu'on ait à orienter
                // la voiture.
                if (Action is Arrive saveArrive && _intent == "Arrive→Save"
                    && saveArrive.Target.Dist(saveTarget) < RetargetDistance)
                {
                    saveArrive.Target = saveTarget;
                    saveArrive.ArrivalTime = saveArrivalTime;
                }
                else
                    SetAction(new Arrive(Me, saveTarget, null, saveArrivalTime), "Arrive→Save");
                return true;
            }

            // --- 2) & 3) réservés à l'Attacker : le Support garde sa couverture ---
            if (role == Role.Support || fieldZone != FieldZone.Defensive)
                return false;

            Vec3 towardOurGoal = Ball.Location.FlatDirection(OurGoal.Location);
            bool inOurThird = MathF.Abs(Ball.Location.y - OurGoal.Location.y) < 3400f;
            bool headingToUs = Ball.Velocity.Dot(towardOurGoal) > 300f;

            // Une balle simplement POSÉE dans notre tiers n'est pas dangereuse : si aucun adversaire
            // n'est à portée de la disputer, on la CONTRÔLE (dribble/carry) au lieu de la dégager en
            // catastrophe — c'est le cas DEF5 « balle lente non dangereuse ». Le danger réel = balle
            // qui fonce vers notre but (headingToUs, quoi qu'il arrive), OU balle dans notre tiers
            // qu'un adversaire peut contester. La proximité adverse est mesurée ici même (pas via le
            // gameState stabilisé, qui accuse 0,25 s de retard après un state set et lirait Contested).
            float oppBallDist = float.MaxValue;
            foreach (Car opp in LivingOpponents)
                oppBallDist = MathF.Min(oppBallDist, opp.Location.Dist(Ball.Location));
            bool contested = oppBallDist < ContestClearDistance;

            bool dangerous = headingToUs || (inOurThird && contested);
            if (!dangerous)
                return false;

            // --- 2) Balle dangereuse → dégagement (tir loin de notre but) ---
            // Seulement si on n'est PAS clairement battu à la balle. Un dégagement suppose qu'on
            // atteigne la balle en premier ; en NotPossessed l'adversaire y arrive > 0.4s avant nous
            // (souvent il la contrôle déjà, oppDist petit). FindShot, qui ignore les touches adverses,
            // s'accroche alors à un slice lointain que l'adversaire aura frappé bien avant — d'où un
            // Shot→Dégagement fantôme qui flip-flop avec le Contest. On laisse la logique NotPossessed
            // (DriveContest / Fifty) se rapprocher pour arriver en Contested et disputer le 50/50.
            if (gameState != GameStateMode.NotPossessed)
            {
                if (ShotInProgress("Shot→Dégagement"))
                    return true;

                Shot clear = FindShot(Defensible(shotCheck), ClearTarget());
                if (clear != null)
                {
                    LogShotPick("Shot→Dégagement", clear);
                    SetAction(clear, "Shot→Dégagement");
                    return true;
                }
            }

            // --- 3) Pas goal-side → se replier entre la balle et notre but AVANT tout contact.
            // C'est LE cas qui fabrique les CSC : toucher la balle en la poursuivant vers notre but.
            if (!IsGoalSide())
            {
                Vec3 contour = ClampToField(Ball.Location + towardOurGoal * 1200f);
                SetDrive(contour, "Drive→GoalSide", wasteBoost: true);
                return true;
            }

            // Goal-side, pas de tir jouable : la logique standard (Fifty / Drive) prend le relais —
            // depuis goal-side, la poussée voiture→balle part vers le camp adverse, c'est sain.
            return false;
        }

        // --- Patience de tir (Fixes.PatientShot, AUDIT §2.2) ---

        /// <summary>Au-delà de cet écart latéral, la balle part vers le corner : plus la peine d'attendre l'alignement.</summary>
        private const float PatienceMaxX = 3000f;
        /// <summary>Vitesse latérale minimale vers l'axe pour croire à une amélioration de l'angle.</summary>
        private const float PatienceClosingSpeed = 100f;

        /// <summary>
        /// Enveloppe un ShotCheck en REFUSANT les slices mal alignées avec le but quand la balle
        /// est en train de revenir vers l'axe.
        ///
        /// <para>Problème corrigé : <c>Ball.Prediction.Find</c> renvoie la PREMIÈRE slice jouable,
        /// donc le bot tire systématiquement le plus tôt possible — quel que soit l'angle. Une
        /// balle qui traverse depuis le corner est frappée pendant qu'elle est encore de côté,
        /// alors qu'attendre trois dixièmes de seconde la place face au but.</para>
        ///
        /// <para>Critère d'alignement : l'écart latéral au-delà du poteau
        /// (<c>|x| − demi-largeur du but</c>) comparé à la distance restante jusqu'à la ligne. Tant
        /// que l'écart dépasse cette distance, le tir part de trop loin sur le côté — c'est
        /// grossièrement un angle de plus de 45° par rapport à la cage.</para>
        ///
        /// <para>On n'attend QUE si la situation s'améliore d'elle-même (<c>vx</c> dirigée vers
        /// l'axe) et que la balle n'est pas déjà partie dans le corner. Sinon on tire : refuser un
        /// tir sans perspective de mieux, c'est ne jamais tirer. Comme <c>Find</c> balaie dans
        /// l'ordre chronologique, refuser les slices précoces suffit à faire choisir la première
        /// slice correctement alignée — aucun second balayage n'est nécessaire.</para>
        ///
        /// <para>Réservé à <c>Possessed</c> en zone offensive : c'est le seul état où l'on a
        /// réellement le temps d'attendre. En <c>Contested</c> l'adversaire arrive, et sur un
        /// dégagement ou une save la question ne se pose pas.</para>
        ///
        /// <para><b>Limite connue, à surveiller en test.</b> Quand toutes les slices sont refusées,
        /// <c>FindShot</c> renvoie null et on retombe sur <c>Drive→Balle</c> — qui roule VERS la
        /// balle. Un contact peut donc survenir quand même, à l'angle qu'on voulait éviter. La
        /// version aboutie irait se placer derrière la balle par rapport à leur but au lieu de la
        /// suivre. Si le bot chippe la balle de côté pendant les phases d'attente, c'est ça.</para>
        /// </summary>
        private ShotCheck Patient(ShotCheck inner)
        {
            if (!Fixes.PatientShot)
                return inner;

            return (slice, target) =>
            {
                if (slice != null && WorthWaitingForAlignment(slice))
                    return null;
                return inner(slice, target);
            };
        }

        /// <summary>Vrai si cette slice est mal alignée avec leur but ET que l'angle s'améliore.</summary>
        private bool WorthWaitingForAlignment(BallSlice slice)
        {
            float lateral = MathF.Abs(slice.Location.x);

            // Déjà dans le corner : l'angle ne reviendra pas, inutile de patienter
            if (lateral > PatienceMaxX)
                return false;

            // De combien on dépasse le poteau, contre ce qu'il reste à parcourir jusqu'à la ligne
            float pastPost = lateral - Goal.Width / 2f + Ball.Radius;
            float depthToGoal = MathF.Abs(slice.Location.y - TheirGoal.Location.y);
            if (pastPost <= depthToGoal)
                return false; // angle déjà correct

            // La balle revient-elle vers l'axe ? Sinon attendre ne sert à rien.
            float closing = -slice.Velocity.x * MathF.Sign(slice.Location.x);
            return closing > PatienceClosingSpeed;
        }

        /// <summary>
        /// Enveloppe un ShotCheck en refusant les contacts où la voiture serait dans son propre but.
        /// Le point de contact d'un tir (`shot.TargetLocation`) EST déjà la position de la voiture ;
        /// il suffit de vérifier qu'elle reste devant la ligne. Utilisé par le dégagement (priorité 2).
        /// </summary>
        private ShotCheck Defensible(ShotCheck inner)
        {
            return (slice, target) =>
            {
                Shot shot = inner(slice, target);
                if (shot == null)
                    return null;
                return ContactInFrontOfGoal(shot.TargetLocation) ? shot : null;
            };
        }

        // ---- Ancien circuit de save (Fixes.UnifiedSave == false) ----

        /// <summary>Marge de confort VISÉE : on préfère la première slice qu'on atteint avec ce battement,
        /// pour aller au-devant de la balle sans finir sur un point à marge nulle. C'est une préférence,
        /// pas un plancher (voir la note de repli dans <see cref="FindSaveInterceptSlice"/>). Volontairement
        /// petit : sur une balle rapide, 0.15 s pousserait déjà l'interception bien trop profond.</summary>
        private const float SaveInterceptMargin = 0.1f;

        /// <summary>
        /// Slice à intercepter pour le save : la PLUS TÔT qu'on atteint avec la marge de confort
        /// <see cref="SaveInterceptMargin"/>. On va ainsi AU-DEVANT de la balle (haut, loin du but)
        /// plutôt que de l'attendre devant la cage (ce que faisait « la dernière slice », trop passif),
        /// sans pour autant viser un point à marge nulle (« la première slice », trop fragile).
        ///
        /// <para><b>La marge est une préférence, pas une barrière.</b> Si aucune slice atteignable ne
        /// l'offre — balle rapide, fenêtre étroite — on retombe sur la <b>plus tôt atteignable tout
        /// court</b>, même serrée : un save juste vaut mieux que pas de save. Le pacing de l'Arrive
        /// empêche le dépassement dans les deux cas.</para>
        ///
        /// <para>L'atteignabilité passe par <c>Movement.EtaFor</c> (moteur documenté « aller à un point
        /// au sol »), via <see cref="InterceptSlack"/>.</para>
        /// </summary>
        private BallSlice FindSaveInterceptSlice(float beforeTime)
        {
            BallSlice earliest = null;   // repli : la plus tôt atteignable, même à marge nulle
            foreach (BallSlice s in Ball.Prediction.Slices)
            {
                // Slices chronologiques : passé la ligne de but, plus rien d'utile à tester.
                if (s.Time >= beforeTime)
                    break;
                if (s.Time <= Game.Time)
                    continue;

                float slack = InterceptSlack(s);
                if (float.IsNaN(slack) || slack < 0f)
                    continue;               // contact dans le filet, ou hors de portée à temps

                earliest ??= s;
                if (slack >= SaveInterceptMargin)
                    return s;               // la plus tôt qui tient la marge de confort
            }
            return earliest;
        }

        /// <summary>
        /// Battement pour bloquer cette slice goal-side : (temps avant la slice) − (notre ETA vers le
        /// point de contact). Positif = atteignable avec cette marge ; négatif = hors de portée à temps.
        /// <c>NaN</c> si le contact tomberait DERRIÈRE notre ligne (slice inutile — c'est le filtre qui
        /// interdit d'accepter une balle déjà entrée). ETA via <c>Movement.EtaFor</c>.
        /// </summary>
        private float InterceptSlack(BallSlice s)
        {
            Vec3 contact = GoalSideContact(s.Location, s.Velocity);
            if (!ContactInFrontOfGoal(contact))
                return float.NaN;
            return (s.Time - Game.Time) - Movement.EtaFor(Me, contact);
        }

        /// <summary>En deçà de cette vitesse, la direction de la balle n'est pas fiable pour en déduire
        /// le point de contact (elle roule/hésite) : on retombe sur « vers notre but ».</summary>
        private const float SlowBallSpeed = 300f;

        /// <summary>
        /// Point de contact pour BLOQUER la balle : sur SON chemin, du côté de notre but, décalé du
        /// rayon balle + demi-voiture pour que les carrosseries se touchent quand la balle arrive.
        ///
        /// <para>La direction retenue est celle de la balle (on la bloque de face, ce qui est plus juste
        /// qu'un décalage vers le centre du but sur un tir qui rentre en angle). Sur une balle lente
        /// (&lt; <see cref="SlowBallSpeed"/>) sa vitesse n'indique plus rien, on retombe sur la direction
        /// vers notre but.</para>
        ///
        /// <para>On ne clampe PAS le résultat devant la ligne ici : c'est <see cref="ContactInFrontOfGoal"/>
        /// qui filtre les slices dont le contact tomberait dans le filet. Clamper masquerait ce test et
        /// ferait accepter une slice déjà passée derrière la ligne.</para>
        /// </summary>
        private Vec3 GoalSideContact(Vec3 ballLocation, Vec3 ballVelocity)
        {
            Vec3 toGoalSide = ballVelocity.FlatLen() > SlowBallSpeed
                ? ballVelocity.FlatNorm()
                : ballLocation.FlatDirection(OurGoal.Location);
            return ballLocation + toGoalSide * (Ball.Radius + CarHalfLength);
        }

        /// <summary>
        /// Vrai si la voiture, à cette position de contact, est encore DEVANT sa ligne de but.
        ///
        /// <para>Remplace l'ancien seuil sur la profondeur de la BALLE, qui était le mauvais critère :
        /// un save profond en étant goal-side est parfaitement valable, ce qui compte c'est de ne pas
        /// finir dans le filet. Ce seuil-là tombait pile sur la fenêtre atteignable (y=4220) et le
        /// bruit d'ETA le faisait osciller un tick sur deux — mesuré via [SAVEMISS].</para>
        /// </summary>
        private bool ContactInFrontOfGoal(Vec3 carContactPoint)
        {
            return MathF.Abs(carContactPoint.y) < Field.Length / 2f - CarHalfLength;
        }

        /// <summary>Vrai si on est entre la balle et notre but (contact défensif sûr).</summary>
        private bool IsGoalSide()
        {
            Vec3 towardOurGoal = Ball.Location.FlatDirection(OurGoal.Location);
            return (Me.Location - Ball.Location).Normalize().Dot(towardOurGoal) > 0.2f;
        }

        /// <summary>Adversaire vivant le plus proche de la balle, avec sa distance à la balle.</summary>
        private Car NearestOpponentToBall(out float distance)
        {
            Car nearest = null;
            distance = float.MaxValue;
            foreach (Car opp in LivingOpponents)
            {
                float d = opp.Location.Dist(Ball.Location);
                if (d < distance) { distance = d; nearest = opp; }
            }
            return nearest;
        }

        /// <summary>
        /// Vrai quand la situation est un 50/50 à nos pieds plutôt qu'une save passive : l'adversaire
        /// le plus proche ET nous sommes tous deux « sur la balle » (&lt; <see cref="FiftyChallengeRange"/>),
        /// la balle est basse (dribble sol) et on est goal-side — donc challenger la pousse vers le camp
        /// adverse, pas dans notre but. Détection PURE (sans le flag), partagée par l'override de
        /// <see cref="TryDefensivePriority"/> et par la ligne de log (champ <c>challenge=</c>).
        /// </summary>
        private bool IsCloseContestGoalSide()
        {
            NearestOpponentToBall(out float oppDist);
            return oppDist < FiftyChallengeRange
                && Me.Location.Dist(Ball.Location) < FiftyChallengeRange
                && Ball.Location.z < ChallengeMaxBallHeight
                && IsGoalSide();
        }

        /// <summary>
        /// Point de contest défensif : sur la ligne balle→NOTRE but, goal-side de la balle, à un
        /// standoff qui anticipe l'avancée de la balle VERS notre but.
        ///
        /// <para>On projette le long de l'axe balle→but (l'axe dangereux, invariant), PAS du cap
        /// actuel du porteur : ce cap est volatile — il peut tourner et couper la balle derrière nous
        /// vers le but pendant qu'on court vers son ancienne direction. En tenant la ligne du but, on
        /// est déjà devant lui sur l'axe qui compte ; il ne peut plus aller que sur les côtés → on le
        /// repousse vers le corner.</para>
        ///
        /// <para>Seule la composante d'avancée VERS le but est anticipée (le latéral vers le corner
        /// est ignoré, on ne le suit pas). Un porteur au contact peut booster → on projette avec
        /// accélération, bornée par ContestMaxAdvance pour ne pas s'effondrer dans le but. Le standoff
        /// s'auto-ajuste : loin (grand ETA) on contient profond sur la ligne, près on ferme pour
        /// challenger. Le Fifty prend le relais au contact.</para>
        /// </summary>
        private Vec3 ContestPoint()
        {
            Car carrier = NearestOpponentToBall(out float carrierDist);
            bool canPush = carrier != null && carrierDist < ContestCarryDistance;

            // Axe dangereux : de la balle vers NOTRE but. C'est la ligne qu'on tient.
            Vec3 toGoal = Ball.Location.FlatDirection(OurGoal.Location);

            // Vitesse d'avancée VERS notre but (composante sur l'axe ; négatif = s'éloigne → 0).
            Vec3 baseVel = canPush ? carrier.Velocity : Ball.Velocity;
            float goalwardSpeed = MathF.Max(0f, baseVel.Dot(toGoal));
            // Un porteur au contact peut booster ; une balle libre n'accélère pas d'elle-même.
            float accel = canPush ? Car.BoostAccel : 0f;

            // Standoff = avancée anticipée (bornée) + marge goal-side pour rester DEVANT.
            float lead = MathF.Min(Movement.EtaFor(Me, Ball.Location), ContestMaxLead);
            float standoff = MathF.Min(ReachDistance(goalwardSpeed, accel, lead), ContestMaxAdvance) + ContestGap;
            // Une itération de point fixe : ré-estime notre ETA vers le point ainsi obtenu.
            lead = MathF.Min(Movement.EtaFor(Me, Ball.Location + toGoal * standoff), ContestMaxLead);
            standoff = MathF.Min(ReachDistance(goalwardSpeed, accel, lead), ContestMaxAdvance) + ContestGap;

            return Ball.Location + toGoal * standoff;
        }

        /// <summary>Distance parcourue en <paramref name="time"/> s à partir de <paramref name="speed0"/>,
        /// en accélérant à <paramref name="accel"/> uu/s² (moyenne trapézoïdale, vitesse bornée à MaxSpeed).</summary>
        private static float ReachDistance(float speed0, float accel, float time)
        {
            speed0 = Utils.Cap(speed0, 0f, Car.MaxSpeed);
            float vEnd = MathF.Min(Car.MaxSpeed, speed0 + accel * time);
            return (speed0 + vEnd) / 2f * time;
        }

        // Demi-longueur de la voiture (Octane ~118). Sert à placer le contact goal-side (rayon balle
        // + ce décalage) et à garder le nez devant la ligne de but (voir ContactInFrontOfGoal).
        private const float CarHalfLength = 60f;

        // En dessous de cette distance adversaire→balle, une balle dans notre tiers est jugée
        // contestable et déclenche le dégagement ; au-delà on garde la possession et on contrôle.
        private const float ContestClearDistance = 2500f;

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

        /// <summary>Centre de la porte de dégagement : point de la moitié adverse vers lequel
        /// l'action Save renvoie la balle.</summary>
        private Vec3 ClearAimPoint()
        {
            return new Vec3(0f, -Field.Side(Team) * ClearGateDepth, 0f);
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
                // Found peut être faux : GetBoost n'a alors ni pad ni cible (AUDIT §0.1)
                GetBoost g => g.Found ? (Vec3?)g.ChosenBoost.Location : null,
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
            if (Me.Name != "MyBo") return;
            float actual = Game.Time - _etaStartTime;
            string head = $"[{Game.Time:F2}s][{Me.Name}] [ETA] {outcome} {_etaIntent}";
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
        // Ancien circuit de save : cible d'interception latchée (gardée quand un tick n'en trouve pas).
        private Vec3 _saveTarget;
        private float _saveArrivalTime = -1f;   // instant où la balle atteint la cible (pace de l'Arrive)
        private float _saveTargetTime = -1f;
        private const float SaveLatchTime = 0.4f;

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
            if(Me.Name == "MyBo")
                {
                Console.WriteLine($"[{Game.Time:F2}s][{Me.Name}#{Index}] {(isNew ? "NEW " : "    ")}{_intent} " +
                    $"tRem={tRem:F2} dTgt={Me.Location.Dist(shot.TargetLocation):F0} v={Me.Velocity.Length():F0} boost={Me.Boost:F0} " +
                    $"cote={side:F2} derive={drift:F0} " +
                    $"shotTgt=({shot.ShotTarget.x:F0},{shot.ShotTarget.y:F0},{shot.ShotTarget.z:F0}) " +
                    $"tgtLoc=({shot.TargetLocation.x:F0},{shot.TargetLocation.y:F0},{shot.TargetLocation.z:F0}) " +
                    $"ball=({Ball.Location.x:F0},{Ball.Location.y:F0},{Ball.Location.z:F0})");
                }
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
            if(Me.Name != "MyBo") return;
            float timeRemaining = shot.Slice.Time - Game.Time;
            // Même ETA que celui utilisé par IsValid (alignement compris), sinon la marge affichée
            // est calculée sur un trajet que le bot ne conduira pas et ne veut rien dire.
            float carEta = Drive.GetEta(Me, shot.TargetLocation, shot.ShotDirection.FlatNorm());
            // Vitesse à laquelle Arrive va se caler pour arriver pile à l'heure (Arrive.cs:59).
            // C'est elle qui décide si le bot boost ou se laisse rouler : sous 1400, aucun boost.
            float paceSpeed = Drive.GetDistance(Me, shot.TargetLocation) / MathF.Max(timeRemaining, 0.001f);
            Console.WriteLine($"[{Game.Time:F2}s][{Me.Name}] {intent} → {shot.GetType().Name} " +
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
