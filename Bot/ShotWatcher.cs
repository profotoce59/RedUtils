using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Mémoire glissante des poses de jeu, qui n'en retient QUE celles qui précèdent un tir.
    ///
    /// <para><b>Le problème.</b> Un tir raté ne se rejoue pas : quand on le voit rater, la situation
    /// qui l'a produit est déjà passée. Reconstruire à la main une pose de départ « à peu près
    /// pareille » revient à tester un autre scénario que celui qui a échoué — et à corriger contre
    /// un fantôme.</para>
    ///
    /// <para><b>Ce que ça fait.</b> Chaque tick, la pose complète du match (balle + les 4 voitures)
    /// est écrite dans un tampon circulaire d'environ 1,5 s. Rien n'est conservé tant qu'il ne se
    /// passe rien. Au moment PRÉCIS où l'action bascule vers un tir, on ressort du tampon la pose
    /// d'il y a <see cref="LookBack"/> secondes — l'instant où le bot avait encore le choix — et on
    /// l'écrit sur une ligne de <c>shot_captures.jsonl</c>. C'est cette pose-là qui se recolle dans
    /// un state setter.</para>
    ///
    /// <para><b>Coût.</b> Une copie de structs par tick, aucune allocation (le tampon et les tableaux
    /// de voitures sont alloués une fois), et une écriture disque uniquement au départ d'un tir. Le
    /// watcher n'est instancié que pour la voiture observée (voir <c>MyBot.ShotWatcherCarName</c>),
    /// donc les 3 autres voitures du match ne paient rien.</para>
    ///
    /// <para>Le fichier est relu par <c>shot_watcher.py</c>, qui rejoue une capture dans le jeu ou en
    /// recrache le scénario Python prêt à coller.</para>
    /// </summary>
    public class ShotWatcher
    {
        /// <summary>Antériorité de la pose retenue. 0,5 s avant le tir = l'instant où la décision
        /// n'est pas encore prise : rejouer là redonne au bot le même choix qu'en match.</summary>
        public const float LookBack = 0.5f;

        /// <summary>Profondeur du tampon. Doit dépasser <see cref="LookBack"/> avec de la marge :
        /// à 120 Hz les 256 emplacements couvrent 2,1 s, à 60 Hz 4,2 s.</summary>
        private const int Capacity = 256;

        /// <summary>Délai minimal entre deux captures. Un tir peut être reconstruit plusieurs fois
        /// de suite (nouvelle slice, nouvelle instance) : sans ce plancher, une seule tentative
        /// remplirait le fichier de doublons décrivant la même situation.</summary>
        private const float MinCaptureInterval = 0.4f;

        private const int MaxCars = 8;

        public const string FileName = "shot_captures.jsonl";

        private struct CarPose
        {
            public int Index;
            public int Team;
            public string Name;
            public Vec3 Location;
            /// <summary>(pitch, yaw, roll) — l'ordre de Vec3(Rotator), directement collable en Rotator().</summary>
            public Vec3 Rotation;
            public Vec3 Velocity;
            public Vec3 AngularVelocity;
            public int Boost;
            public bool Demolished;
        }

        private class Frame
        {
            public float Time = float.NaN;
            public Vec3 BallLocation;
            public Vec3 BallVelocity;
            public Vec3 BallAngularVelocity;
            public readonly CarPose[] Cars = new CarPose[MaxCars];
            public int CarCount;
        }

        private readonly Frame[] _frames = new Frame[Capacity];
        private int _head;      // prochain emplacement à écrire
        private int _count;     // frames valides dans le tampon

        private readonly string _path;
        private readonly string _session;
        private readonly string _carName;

        /// <summary>Dernière action de tir vue. Comparée PAR RÉFÉRENCE : c'est le changement d'objet
        /// qui fait un nouveau tir, pas le changement de type ou d'intent (une même tentative garde
        /// son instance et rafraîchit sa cible toute seule).</summary>
        private object _lastShot;
        private float _lastCaptureTime = -99f;
        private int _captures;
        private bool _pathAnnounced;

        public ShotWatcher(string carName)
        {
            _carName = carName;
            _session = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _path = ResolvePath();

            for (int i = 0; i < Capacity; i++)
                _frames[i] = new Frame();
        }

        /// <summary>
        /// Écrit à côté de <c>Bot.sln</c>, donc à la racine du dépôt, là où vivent déjà les scripts
        /// de state setting — c'est là que <c>shot_watcher.py</c> ira le chercher sans configuration.
        /// Bot.exe tourne depuis <c>Bot/bin/Debug/net6.0</c>, d'où la remontée. Surchargeable par la
        /// variable d'environnement <c>SHOT_WATCHER_FILE</c> (chemin complet du fichier).
        /// </summary>
        private static string ResolvePath()
        {
            string forced = Environment.GetEnvironmentVariable("SHOT_WATCHER_FILE");
            if (!string.IsNullOrWhiteSpace(forced))
                return forced;

            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Bot.sln")))
                    return Path.Combine(dir.FullName, FileName);
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, FileName);
        }

        /// <summary>
        /// Un tick de watcher : mémorise la pose courante, et capture si l'action VIENT de basculer
        /// vers un tir. À appeler après que l'action du tick a été choisie.
        /// </summary>
        public void Update(float gameTime, IAction action, string intent)
        {
            Record(gameTime);

            object shot = ShotOf(action);
            if (shot == null)
            {
                _lastShot = null;
                return;
            }

            // Même tir que le tick précédent : rien de nouveau à retenir.
            if (ReferenceEquals(shot, _lastShot))
                return;
            _lastShot = shot;

            if (gameTime - _lastCaptureTime < MinCaptureInterval)
                return;
            _lastCaptureTime = gameTime;

            Capture(gameTime, shot, intent);
        }

        /// <summary>
        /// Vide le tampon. Appelé sur téléportation (state setting) : les poses d'AVANT décrivent une
        /// situation qui n'existe plus, et les relire produirait une capture mélangeant deux
        /// scénarios — exactement le genre de pose « à peu près pareille » qu'on cherche à éviter.
        /// </summary>
        public void Reset()
        {
            _head = 0;
            _count = 0;
            _lastShot = null;
            _lastCaptureTime = -99f;
        }

        /// <summary>Actions considérées comme « un tir ». Les quatre <see cref="Shot"/> (Ground,
        /// Jump, DoubleJump, Aerial) plus <see cref="QuickShot"/>, qui n'en dérive pas mais en est
        /// un. <c>Save</c> et <c>Fifty</c> sont des frappes sans visée : sous
        /// <see cref="Fixes.ShotWatcherIncludeStrikes"/> uniquement.</summary>
        private static object ShotOf(IAction action)
        {
            if (action is Shot or QuickShot)
                return action;
            if (Fixes.ShotWatcherIncludeStrikes && action is Save or Fifty)
                return action;
            return null;
        }

        /// <summary>Copie la pose du tick dans le tampon circulaire. Aucune allocation.</summary>
        private void Record(float gameTime)
        {
            Frame f = _frames[_head];
            f.Time = gameTime;
            f.BallLocation = Ball.Location;
            f.BallVelocity = Ball.Velocity;
            f.BallAngularVelocity = Ball.AngularVelocity;

            List<Car> cars = Cars.AllCars;
            int n = cars == null ? 0 : System.Math.Min(cars.Count, MaxCars);
            for (int i = 0; i < n; i++)
            {
                Car c = cars[i];
                f.Cars[i].Index = c.Index;
                f.Cars[i].Team = c.Team;
                f.Cars[i].Name = c.Name;
                f.Cars[i].Location = c.Location;
                f.Cars[i].Rotation = c.Rotation;
                f.Cars[i].Velocity = c.Velocity;
                f.Cars[i].AngularVelocity = c.AngularVelocity;
                f.Cars[i].Boost = c.Boost;
                f.Cars[i].Demolished = c.IsDemolished;
            }
            f.CarCount = n;

            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }

        /// <summary>
        /// La frame la plus récente qui a AU MOINS <see cref="LookBack"/> secondes. Si le tampon ne
        /// remonte pas si loin (tir juste après un state set), on rend la plus ancienne disponible —
        /// l'écart réel est écrit dans la capture (<c>delay</c>), pour qu'une pose trop proche du tir
        /// se voie au lieu de passer pour une pose à 0,5 s.
        /// </summary>
        private Frame FrameBefore(float gameTime)
        {
            float target = gameTime - LookBack;
            Frame oldest = null;
            for (int i = 1; i <= _count; i++)
            {
                Frame f = _frames[(_head - i + Capacity) % Capacity];
                oldest = f;
                if (f.Time <= target)
                    return f;
            }
            return oldest;
        }

        private void Capture(float gameTime, object shot, string intent)
        {
            Frame f = FrameBefore(gameTime);
            if (f == null)
                return;

            _captures++;
            float delay = gameTime - f.Time;
            string type = shot.GetType().Name;

            // Métadonnées du tir : elles ne servent pas à rejouer (le state setter n'en a pas
            // besoin) mais à RETROUVER la bonne capture dans la liste — « le jump shot vers leur
            // but à 312 s » — sans avoir à relire les logs.
            string meta = "";
            if (shot is Shot s && s.Slice != null)
            {
                meta = $",\"contact_in\":{Num(s.Slice.Time - gameTime, 3)}" +
                       $",\"shot_target\":{Vec(s.ShotTarget, 1)}" +
                       $",\"contact_point\":{Vec(s.TargetLocation, 1)}";
            }
            else if (shot is QuickShot q)
            {
                meta = $",\"shot_target\":{Vec(q.Target, 1)}";
            }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append($"\"session\":\"{_session}\"");
            sb.Append($",\"n\":{_captures}");
            sb.Append($",\"time\":{Num(f.Time, 2)}");
            sb.Append($",\"shot_time\":{Num(gameTime, 2)}");
            sb.Append($",\"delay\":{Num(delay, 3)}");
            sb.Append($",\"shot\":\"{Esc(type)}\"");
            sb.Append($",\"intent\":\"{Esc(intent ?? "none")}\"");
            sb.Append($",\"shooter\":\"{Esc(_carName)}\"");
            sb.Append(meta);
            sb.Append(",\"ball\":{");
            sb.Append($"\"location\":{Vec(f.BallLocation, 1)}");
            sb.Append($",\"velocity\":{Vec(f.BallVelocity, 1)}");
            sb.Append($",\"angular_velocity\":{Vec(f.BallAngularVelocity, 4)}");
            sb.Append('}');
            sb.Append(",\"cars\":[");
            for (int i = 0; i < f.CarCount; i++)
            {
                CarPose c = f.Cars[i];
                if (i > 0)
                    sb.Append(',');
                sb.Append('{');
                sb.Append($"\"index\":{c.Index}");
                sb.Append($",\"team\":{c.Team}");
                sb.Append($",\"name\":\"{Esc(c.Name)}\"");
                sb.Append($",\"location\":{Vec(c.Location, 1)}");
                sb.Append($",\"rotation\":{Vec(c.Rotation, 4)}");
                sb.Append($",\"velocity\":{Vec(c.Velocity, 1)}");
                sb.Append($",\"angular_velocity\":{Vec(c.AngularVelocity, 4)}");
                sb.Append($",\"boost\":{c.Boost}");
                sb.Append($",\"demolished\":{(c.Demolished ? "true" : "false")}");
                sb.Append('}');
            }
            sb.Append("]}");

            // Une capture ratée ne doit JAMAIS coûter le match : le fichier peut être ouvert par le
            // script Python au moment où on écrit.
            try
            {
                File.AppendAllText(_path, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[SHOTWATCH] ecriture impossible ({e.GetType().Name}: {e.Message})");
                return;
            }

            if (!_pathAnnounced)
            {
                _pathAnnounced = true;
                Console.WriteLine($"[SHOTWATCH] captures -> {_path} (session {_session})");
            }

            CarPose me = CarAt(f, _carName);
            Console.WriteLine($"[{gameTime:F2}s][{_carName}] [SHOTWATCH] #{_captures} {type} intent={intent ?? "none"} " +
                $"| pose a T-{delay:F2}s : ball=({f.BallLocation.x:F0},{f.BallLocation.y:F0},{f.BallLocation.z:F0}) " +
                $"tireur=({me.Location.x:F0},{me.Location.y:F0}) v={me.Velocity.Length():F0} boost={me.Boost}");
        }

        private static CarPose CarAt(Frame f, string name)
        {
            for (int i = 0; i < f.CarCount; i++)
                if (f.Cars[i].Name == name)
                    return f.Cars[i];
            return f.CarCount > 0 ? f.Cars[0] : default;
        }

        /// <summary>JSON n'accepte que le point décimal : en locale FR, une interpolation ordinaire
        /// produirait « 1234,5 » et la ligne serait illisible côté Python.</summary>
        private static string Num(float v, int decimals)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                return "0";
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static string Vec(Vec3 v, int decimals) =>
            $"[{Num(v.x, decimals)},{Num(v.y, decimals)},{Num(v.z, decimals)}]";

        private static string Esc(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
