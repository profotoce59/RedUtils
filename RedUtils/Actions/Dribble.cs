using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>
    /// Dribble/contrôle au sol.
    /// <para>Avec <see cref="Fixes.DirectionalDribble"/> : deux états —</para>
    /// <para>• CATCH (balle en l'air) : se placer sous le point de chute prédit, décalé pour que la balle
    ///   retombe sur le NEZ de la voiture, à vitesse synchronisée (arrive pile à temps, sans dépasser).</para>
    /// <para>• CARRY (balle sur la voiture) : garder la balle légèrement en avant du nez et pousser vers
    ///   <see cref="_target"/>, en roulant le long des murs latéraux au lieu d'y monter.</para>
    /// Terminé si la balle s'éloigne trop (possession perdue).
    /// </summary>
    public class Dribble : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible { get; private set; }

        /// <summary>Seuil (comportement d'origine) balle en l'air / au sol.</summary>
        private const float GroundThreshold = 200f;

        // --- Réglages du contrôle directionnel (Fixes.DirectionalDribble) ---
        /// <summary>Hauteur (z du centre balle) sous laquelle la balle est considérée "posée sur la voiture".
        /// Balle rayon 93 + toit ~45 → ~140 au repos ; on prend une marge.</summary>
        private const float CatchHeight = 190f;
        /// <summary>Décalage (uu) du point de catch derrière le point de chute, pour poser la balle sur le nez.</summary>
        private const float NoseOffset = 60f;
        /// <summary>Distance (uu) au-delà de laquelle on s'autorise dodge/boost pour rejoindre le catch.</summary>
        private const float CatchDodgeDist = 400f;
        /// <summary>Distance (uu) devant le nez où l'on veut maintenir la balle en portage.</summary>
        private const float CarryFrontOffset = 40f;
        /// <summary>Gain proportionnel du contrôle de vitesse en portage.</summary>
        private const float CarrySpeedGain = 3f;
        /// <summary>Anticipation (s) de la position de la balle : la voiture vise là où la balle VA (la suit).</summary>
        private const float CarryLeadTime = 0.2f;
        /// <summary>Recul (uu) de la cible DERRIÈRE la balle (côté notre camp) → la voiture pousse la balle
        /// vers l'adversaire au lieu de passer devant et de la renvoyer chez nous.</summary>
        private const float CarryBehindOffset = 70f;
        /// <summary>Distance à un mur latéral (uu) sous laquelle on évite de pousser dedans.</summary>
        private const float WallAvoidDist = 900f;

        private readonly Drive _drive;
        /// <summary>Point vers lequel on veut envoyer la balle (typiquement le but adverse).</summary>
        private readonly Vec3 _target;

        public Dribble(Car car, Vec3 target)
        {
            Finished = false;
            Interruptible = true;
            _target = target;
            _drive = new Drive(car, Ball.Location, targetSpeed: 1100f, allowDodges: false);
        }

        public void Run(RUBot bot)
        {
            float dist = bot.Me.Location.Dist(Ball.Location);

            if (!Fixes.DirectionalDribble)
            {
                RunOriginal(bot, dist);
            }
            else if (Ball.Location.z > CatchHeight)
            {
                // ===== CATCH : balle en l'air → se placer sous le point de chute, sur le nez =====
                BallSlice landing = Ball.Prediction.Find(s => s.Location.z < CatchHeight && s.Velocity.z < 0);
                Vec3 landingXY = landing != null
                    ? new Vec3(landing.Location.x, landing.Location.y, 0)
                    : new Vec3(Ball.Location.x, Ball.Location.y, 0);
                float tLand = landing != null ? MathF.Max(landing.Time - Game.Time, 0.02f) : 0.3f;

                Vec3 desired = DesiredDirection(landingXY);
                // Poser la balle sur le nez : viser légèrement EN ARRIÈRE du point de chute le long de "desired"
                Vec3 catchPos = landingXY - desired * NoseOffset;
                float distToCatch = bot.Me.Location.FlatDist(catchPos);

                _drive.Target = catchPos;
                // Vitesse pour arriver PILE à tLand → pas de dépassement quand on est déjà en place (≈ vitesse balle)
                _drive.TargetSpeed = Utils.Cap(distToCatch / tLand, 0f, Car.MaxSpeed);
                _drive.AllowDodges = distToCatch > CatchDodgeDist;
                _drive.WasteBoost = distToCatch > CatchDodgeDist;
            }
            else
            {
                // ===== CARRY : SUIVRE la balle (cible ancrée sur elle) et la pousser doucement vers _target =====
                Vec3 desired = DesiredDirection(Ball.Location);
                Vec3 ballGround = new Vec3(Ball.Location.x, Ball.Location.y, 0f);
                Vec3 ballVelFlat = new Vec3(Ball.Velocity.x, Ball.Velocity.y, 0f);

                // Cible ANCRÉE sur la balle (là où elle va) → la voiture la suit, mais reculée DERRIÈRE la balle
                // (côté notre camp) → elle pousse la balle vers l'adversaire au lieu de passer devant.
                _drive.Target = ballGround + ballVelFlat * CarryLeadTime - desired * CarryBehindOffset;

                // Contrôle de vitesse : garder la balle ~CarryFrontOffset devant le nez.
                float fwdErr = (Ball.Location - bot.Me.Location).Dot(bot.Me.Forward);
                float ballHSpeed = ballVelFlat.Length();
                _drive.TargetSpeed = Utils.Cap(ballHSpeed + (fwdErr - CarryFrontOffset) * CarrySpeedGain, 0f, 1600f);
                _drive.AllowDodges = false;
                _drive.WasteBoost = false;
            }

            _drive.Run(bot);
            Finished = bot.Me.Location.Dist(Ball.Location) > 700f;
            Interruptible = true;
        }

        /// <summary>Comportement d'origine : deux phases, poussée dans l'axe voiture→balle (aveugle).</summary>
        private void RunOriginal(RUBot bot, float dist)
        {
            if (Ball.Location.z > GroundThreshold)
            {
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
                Vec3 botToBall = (Ball.Location - bot.Me.Location).FlatNorm();
                float throughOffset = Utils.Cap(dist - 93f, 0f, 150f);
                _drive.Target = Ball.Location + botToBall * throughOffset;
                _drive.AllowDodges = dist > 250f;
                _drive.WasteBoost = dist > 250f;
                _drive.TargetSpeed = Car.MaxSpeed;
            }
        }

        /// <summary>Direction (à plat) dans laquelle envoyer la balle : vers _target, mais jamais dans un mur
        /// latéral proche — dans ce cas on projette la direction sur la tangente au mur (la balle roule le long).</summary>
        /// <param name="ballPos">Position (au sol) de référence pour juger la proximité du mur.</param>
        private Vec3 DesiredDirection(Vec3 ballPos)
        {
            Vec3 d = ballPos.FlatDirection(_target);

            // Anti-mur latéral (x = ±Field.Width/2). La direction "vers le mur" est retirée.
            const float halfWidth = Field.Width / 2f; // 4096
            if (MathF.Abs(ballPos.x) > halfWidth - WallAvoidDist)
            {
                float sign = MathF.Sign(ballPos.x);   // +1 mur droit, -1 mur gauche
                Vec3 intoWall = new Vec3(sign, 0, 0);
                float into = d.Dot(intoWall);
                if (into > 0)
                    d = (d - intoWall * into).FlatNorm();
            }

            return d;
        }
    }
}
