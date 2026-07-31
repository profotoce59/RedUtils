using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>
    /// Challenge défensif 50/50 — toujours agressif quelle que soit la hauteur :
    ///   Approche  : Drive vers intercept prédit (sans dodge interne)
    ///   Contact (ballEta &lt; 0.4s) :
    ///     z &lt; 250  → Dodge plat (sol)
    ///     z &lt; 500  → Saut + Dodge
    ///     z ≥ 500  → Saut + Boost + Dodge (aérien)
    /// </summary>
    public class Fifty : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible { get; private set; }

        private enum State { Approach, Jump, Aerial, Dodge }
        private State _state = State.Approach;
        private float _jumpTimer;
        private IAction _driveOrDodge;

        public Fifty()
        {
            Finished = false;
            Interruptible = true;
        }

        public void Run(RUBot bot)
        {
            switch (_state)
            {
                case State.Approach:
                    BallSlice approach = Ball.Prediction.Find(s => s.Location.Dist(bot.Me.Location) < 400f);
                    float ballEta = approach != null ? approach.Time - Game.Time : float.MaxValue;

                    if (ballEta > 1.5f)
                    {
                        Finished = true;
                        return;
                    }

                    if (ballEta < 0.4f)
                    {
                        if (Ball.Location.z < 250f)
                        {
                            _driveOrDodge = new Dodge(bot.Me.Location.FlatDirection(Ball.Location));
                            _state = State.Dodge;
                        }
                        else
                        {
                            _jumpTimer = 0f;
                            _state = State.Jump;
                        }
                    }
                    else
                    {
                        // Approche : drive vers intercept
                        if (_driveOrDodge == null || (_driveOrDodge is Drive && _driveOrDodge.Interruptible))
                        {
                            BallSlice intercept = Ball.Prediction.Find(s => Movement.EtaFor(bot.Me, s.Location) <= s.Time - Game.Time);
                            Vec3 target = intercept?.Location ?? Ball.Location;
                            _driveOrDodge = new Drive(bot.Me, target, allowDodges: false);
                        }
                        _driveOrDodge.Run(bot);
                        if (_driveOrDodge.Finished) _driveOrDodge = null;
                    }
                    Interruptible = true;
                    break;

                case State.Jump:
                    Interruptible = false;
                    _jumpTimer += bot.DeltaTime;
                    bot.AimAt(Ball.Location);
                    bot.Controller.Jump = _jumpTimer < 0.15f;

                    if (_jumpTimer >= 0.15f && !bot.Me.IsGrounded)
                    {
                        // Suffisamment monté : passer en aérien si balle haute, sinon dodge direct
                        if (Ball.Location.z >= 400f)
                            _state = State.Aerial;
                        else
                        {
                            _driveOrDodge = new Dodge(Ball.Location - bot.Me.Location);
                            _state = State.Dodge;
                        }
                    }
                    else if (_jumpTimer >= 0.4f)
                    {
                        // Timeout sécurité
                        _driveOrDodge = new Dodge(Ball.Location - bot.Me.Location);
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
                        _driveOrDodge = new Dodge(Ball.Location - bot.Me.Location);
                        _state = State.Dodge;
                    }
                    break;

                case State.Dodge:
                    Interruptible = false;
                    _driveOrDodge.Run(bot);
                    if (_driveOrDodge.Finished)
                        Finished = true;
                    break;
            }
        }
    }
}
