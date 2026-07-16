using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>
    /// Dribble au sol en deux phases :
    /// 1) Balle en l'air  → se placer sous le point d'atterrissage prédit,
    ///    aligné à la vitesse horizontale de la balle, et attendre qu'elle retombe.
    /// 2) Balle au sol    → pousser doucement dans la direction cible.
    /// Terminé si la balle s'éloigne trop (possession perdue).
    /// </summary>
    public class Dribble : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible { get; private set; }

        private const float GroundThreshold = 200f;

        private readonly Drive _drive;

        public Dribble(Car car)
        {
            Finished = false;
            Interruptible = true;
            _drive = new Drive(car, Ball.Location, targetSpeed: 1100f, allowDodges: false);
        }

        public void Run(RUBot bot)
        {
            float dist = bot.Me.Location.Dist(Ball.Location);

            if (Ball.Location.z > GroundThreshold)
            {
                // Phase 1 : balle en l'air → cibler le point d'atterrissage tôt (z < 350) pour arriver avant la balle
                BallSlice landing = Ball.Prediction.Find(s => s.Location.z < 350f && s.Velocity.z < 0);
                Vec3 landingPos = landing != null
                    ? new Vec3(landing.Location.x, landing.Location.y, 0)
                    : new Vec3(Ball.Location.x, Ball.Location.y, 0);

                float ballHSpeed = new Vec3(Ball.Velocity.x, Ball.Velocity.y, 0).Length();
                _drive.Target = landingPos;
                _drive.TargetSpeed = MathF.Max(ballHSpeed + 200f, 1000f);
                _drive.AllowDodges = true;
                _drive.WasteBoost = true;
            }
            else
            {
                // Phase 2 : loin → wavedash/boost pour rattraper ; proche → contact doux
                Vec3 botToBall = (Ball.Location - bot.Me.Location).FlatNorm();
                float throughOffset = Utils.Cap(dist - 93f, 0f, 150f);
                _drive.Target = Ball.Location + botToBall * throughOffset;
                _drive.AllowDodges = dist > 250f;
                _drive.WasteBoost = dist > 250f;
                _drive.TargetSpeed = Car.MaxSpeed;
            }

            _drive.Run(bot);
            Finished = bot.Me.Location.Dist(Ball.Location) > 700f;
            Interruptible = true;
        }
    }
}
