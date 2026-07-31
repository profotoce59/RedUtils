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
    ///
    /// <para><b>Point de contact prédit (AUDIT §1.6)</b> : dès le passage en phase de frappe, on
    /// latche l'INSTANT du contact et on vise, à chaque tick, la position que la prédiction annonce
    /// pour cet instant — pas <c>Ball.Location</c>. Viser la balle telle qu'elle est maintenant
    /// introduit un décalage systématique : entre le saut (0.15 s) et le dodge, une balle à
    /// 2000 uu/s a parcouru 300 uu. On visait donc toujours derrière elle.</para>
    /// </summary>
    public class Fifty : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible { get; private set; }

        private enum State { Approach, Jump, Aerial, Dodge }
        private State _state = State.Approach;
        private float _jumpTimer;
        private IAction _driveOrDodge;

        /// <summary>Instant de jeu du contact visé. -1 tant que la phase d'approche dure.</summary>
        private float _contactTime = -1f;

        /// <summary>Distance à laquelle on considère la balle « à portée » pour engager le contact.</summary>
        private const float ContactRange = 400f;
        /// <summary>En deçà de ce temps avant contact, on passe de l'approche à la frappe.</summary>
        private const float StrikeEta = 0.4f;
        /// <summary>Au-delà, la balle n'arrive pas assez tôt : ce n'est plus un 50/50.</summary>
        private const float AbandonEta = 1.5f;

        public Fifty()
        {
            Finished = false;
            Interruptible = true;
        }

        /// <summary>
        /// Position visée : la balle à l'instant du contact latché, rafraîchie depuis la prédiction
        /// à chaque tick (elle se corrige donc si la trajectoire change). Retombe sur
        /// <c>Ball.Location</c> tant qu'aucun contact n'est latché, ou si la prédiction ne couvre
        /// plus cet instant.
        /// </summary>
        private Vec3 ContactPoint()
        {
            if (_contactTime < 0f)
                return Ball.Location;
            BallSlice slice = Ball.Prediction.AtTime(_contactTime);
            return slice?.Location ?? Ball.Location;
        }

        public void Run(RUBot bot)
        {
            switch (_state)
            {
                case State.Approach:
                    BallSlice approach = Ball.Prediction.Find(s => s.Location.Dist(bot.Me.Location) < ContactRange);
                    float ballEta = approach != null ? approach.Time - Game.Time : float.MaxValue;

                    if (ballEta > AbandonEta)
                    {
                        Finished = true;
                        return;
                    }

                    if (ballEta < StrikeEta)
                    {
                        // Latch de l'instant du contact : à partir d'ici, tout vise ce point-là
                        _contactTime = approach.Time;
                        Vec3 contact = ContactPoint();

                        if (contact.z < 250f)
                        {
                            _driveOrDodge = new Dodge(bot.Me.Location.FlatDirection(contact));
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
                            BallSlice intercept = Ball.Prediction.Find(s => Drive.GetEta(bot.Me, s.Location) <= s.Time - Game.Time);
                            Vec3 target = intercept?.Location ?? Ball.Location;
                            _driveOrDodge = new Drive(bot.Me, target, allowDodges: false);
                        }
                        _driveOrDodge.Run(bot);
                        if (_driveOrDodge.Finished) _driveOrDodge = null;
                    }
                    Interruptible = true;
                    break;

                case State.Jump:
                {
                    Interruptible = false;
                    _jumpTimer += bot.DeltaTime;
                    Vec3 contact = ContactPoint();
                    bot.AimAt(contact);
                    bot.Controller.Jump = _jumpTimer < 0.15f;

                    if (_jumpTimer >= 0.15f && !bot.Me.IsGrounded)
                    {
                        // Suffisamment monté : passer en aérien si balle haute, sinon dodge direct
                        if (contact.z >= 400f)
                            _state = State.Aerial;
                        else
                        {
                            _driveOrDodge = new Dodge(contact - bot.Me.Location);
                            _state = State.Dodge;
                        }
                    }
                    else if (_jumpTimer >= 0.4f)
                    {
                        // Timeout sécurité
                        _driveOrDodge = new Dodge(contact - bot.Me.Location);
                        _state = State.Dodge;
                    }
                    break;
                }

                case State.Aerial:
                {
                    Interruptible = false;
                    Vec3 contact = ContactPoint();
                    bot.AimAt(contact);
                    bot.Controller.Boost = true;
                    bot.Controller.Jump = false;

                    if (bot.Me.Location.Dist(contact) < 300f || bot.Me.HasDoubleJumped)
                    {
                        _driveOrDodge = new Dodge(contact - bot.Me.Location);
                        _state = State.Dodge;
                    }
                    break;
                }

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
