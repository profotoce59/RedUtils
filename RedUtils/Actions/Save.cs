using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>
    /// Dégagement défensif dirigé. La voiture se place GOAL-SIDE de la balle (entre elle et notre
    /// but), puis frappe selon la hauteur et renvoie la balle vers <c>ClearAim</c> (camp adverse) :
    ///   z &lt; 250  → Dodge plat au sol
    ///   z &lt; 500  → Saut + Dodge
    ///   z ≥ 500  → Saut + Boost + Dodge (aérien)
    ///
    /// <para>Calquée sur <see cref="Fifty"/>, avec trois différences qui en font un DÉGAGEMENT et non
    /// un challenge : (1) l'approche vise un point goal-side, pas la première interception venue ;
    /// (2) le dodge final est dirigé vers le camp adverse ; (3) la cible d'interception est latchée
    /// et cherchée par balayage linéaire (la grille pas-de-6 de Ball.Prediction.Find saute des slices
    /// atteignables). Remplace l'ancien fork FindShot(Save) / Drive→Save.</para>
    /// </summary>
    public class Save : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible { get; private set; }

        /// <summary>Notre but — sert à calculer le point de contact goal-side.</summary>
        private readonly Goal _ourGoal;
        /// <summary>Point du camp adverse vers lequel renvoyer la balle.</summary>
        private readonly Vec3 _clearAim;

        private enum State { Approach, Jump, Aerial, Dodge }
        private State _state = State.Approach;
        private float _jumpTimer;
        private Drive _drive;
        private IAction _dodge;

        /// <summary>Dernière interception goal-side connue, conservée quand un tick n'en trouve pas.</summary>
        private Vec3 _latchedTarget;
        private bool _hasTarget;

        /// <summary>Décalage goal-side du contact : rayon balle + demi-longueur voiture.</summary>
        private const float GoalSideOffset = Ball.Radius + 60f;
        /// <summary>Marge devant la ligne de but : au-delà, la voiture serait dans le filet.</summary>
        private const float FrontOfGoalMargin = 60f;
        /// <summary>En deçà de ce temps avant contact, on passe de l'approche à la frappe.</summary>
        private const float StrikeEta = 0.4f;

        public Save(Goal ourGoal, Vec3 clearAim)
        {
            Finished = false;
            Interruptible = true;
            _ourGoal = ourGoal;
            _clearAim = clearAim;
        }

        public void Run(RUBot bot)
        {
            switch (_state)
            {
                case State.Approach:
                    // Temps avant que la balle passe à portée
                    BallSlice near = Ball.Prediction.Find(s => s.Location.Dist(bot.Me.Location) < 400f);
                    float ballEta = near != null ? near.Time - Game.Time : float.MaxValue;

                    if (ballEta < StrikeEta)
                    {
                        // Frappe selon la hauteur (même logique que Fifty)
                        if (Ball.Location.z < 250f)
                        {
                            _dodge = new Dodge(bot.Me.Location.FlatDirection(_clearAim));
                            _state = State.Dodge;
                        }
                        else
                        {
                            _jumpTimer = 0f;
                            _state = State.Jump;
                        }
                        Interruptible = false;
                        break;
                    }

                    // Cible = point de contact goal-side de la première slice atteignable ; sinon on
                    // garde la dernière connue (latch) pour ne pas osciller vers un repli.
                    Vec3? intercept = GoalSideIntercept(bot);
                    if (intercept.HasValue)
                    {
                        _latchedTarget = intercept.Value;
                        _hasTarget = true;
                    }
                    Vec3 target = _hasTarget ? _latchedTarget : GoalSideContact(Ball.Location);

                    // Muter la cible du Drive existant plutôt que le recréer (sinon timeOnGround
                    // repart de zéro et interdit les speedflips — le bug S2).
                    if (_drive == null)
                        _drive = new Drive(bot.Me, target, wasteBoost: true);
                    else
                        _drive.Target = target;
                    _drive.Run(bot);

                    Interruptible = true;
                    break;

                case State.Jump:
                    Interruptible = false;
                    _jumpTimer += bot.DeltaTime;
                    bot.AimAt(Ball.Location);
                    bot.Controller.Jump = _jumpTimer < 0.15f;

                    if (_jumpTimer >= 0.15f && !bot.Me.IsGrounded)
                    {
                        if (Ball.Location.z >= 400f)
                            _state = State.Aerial;
                        else
                        {
                            _dodge = new Dodge(bot.Me.Location.FlatDirection(_clearAim));
                            _state = State.Dodge;
                        }
                    }
                    else if (_jumpTimer >= 0.4f)
                    {
                        _dodge = new Dodge(bot.Me.Location.FlatDirection(_clearAim));
                        _state = State.Dodge;
                    }
                    break;

                case State.Aerial:
                    Interruptible = false;
                    bot.AimAt(Ball.Location);
                    bot.Controller.Boost = true;
                    bot.Controller.Jump = false;

                    if (bot.Me.Location.Dist(Ball.Location) < 300f || bot.Me.HasDoubleJumped)
                    {
                        _dodge = new Dodge(bot.Me.Location.FlatDirection(_clearAim));
                        _state = State.Dodge;
                    }
                    break;

                case State.Dodge:
                    Interruptible = false;
                    _dodge.Run(bot);
                    if (_dodge.Finished)
                        Finished = true;
                    break;
            }
        }

        /// <summary>
        /// Première slice dont le contact goal-side est atteignable à temps et devant notre ligne.
        /// Balayage LINÉAIRE (pas Ball.Prediction.Find, dont la grille pas-de-6 saute des slices
        /// pourtant atteignables — mesuré en jeu).
        /// </summary>
        private Vec3? GoalSideIntercept(RUBot bot)
        {
            BallSlice[] slices = Ball.Prediction.Slices;
            for (int i = 0; i < slices.Length; i++)
            {
                BallSlice s = slices[i];
                float t = s.Time - Game.Time;
                if (t <= 0f) continue;
                Vec3 contact = GoalSideContact(s.Location);
                if (!InFrontOfGoal(contact)) continue;
                if (Drive.GetEta(bot.Me, contact) <= t)
                    return contact;
            }
            return null;
        }

        /// <summary>Point où la voiture bloque : goal-side de la balle (entre elle et notre but).</summary>
        private Vec3 GoalSideContact(Vec3 ball)
        {
            Vec3 toOurGoal = ball.FlatDirection(_ourGoal.Location);
            return ball + toOurGoal * GoalSideOffset;
        }

        /// <summary>Vrai si ce point de contact laisse la voiture devant sa ligne de but.</summary>
        private static bool InFrontOfGoal(Vec3 point)
        {
            return MathF.Abs(point.y) < Field.Length / 2f - FrontOfGoalMargin;
        }
    }
}
