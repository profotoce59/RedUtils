using rlbot.flat;
using System;
using System.Collections.Generic;

namespace RedUtils
{
    /// <summary>
    /// Processed version of the <see cref="rlbot.flat.BallPrediction"/> that uses sane data structures.
    /// </summary>
    public struct BallPrediction
    {
        public BallSlice this[int index] { get { return Slices[index]; } }

        /// <summary>A list of all of the future ball slices</summary>
        public BallSlice[] Slices;
        public int Length => Slices.Length;

        public BallPrediction(rlbot.flat.BallPrediction ballPrediction)
        {
            Slices = new BallSlice[ballPrediction.SlicesLength];
            for (int i = 0; i < ballPrediction.SlicesLength; i++)
                Slices[i] = new BallSlice(ballPrediction.Slices(i).Value);
        }

        /// <summary>Finds the first ball slice that fits the given predicate
        /// <para>This function is more effecient then the normal "Find" function, and accounts for scoring</para>
        /// </summary>
        /// <remarks>
        /// Balayage grossier au pas de 6 slices (0.1 s), puis raffinage sur les six slices
        /// précédentes dès qu'un point convient — on cherche la PREMIÈRE slice satisfaisante,
        /// pas n'importe laquelle.
        ///
        /// <para>Correctif (AUDIT §0.2) : la version d'origine ne renvoyait <c>Slices[i]</c> dans
        /// AUCUN cas. Quand le prédicat était vrai en <c>i</c> mais faux sur les six slices
        /// précédentes, la boucle interne se terminait sans rien retourner et le balayage
        /// continuait — pour finir sur <c>null</c> alors qu'une slice valide venait d'être
        /// trouvée. Le cas est fréquent : « puis-je atteindre cette slice à temps » n'est pas un
        /// prédicat monotone (la balle s'éloigne puis revient, la voiture accélère), donc rien ne
        /// garantit qu'un des six prédécesseurs convienne. Conséquence en jeu : ETA renvoyé à
        /// <c>float.MaxValue</c> sur des situations parfaitement jouables — état de possession
        /// faussé, rôles inversés, tirs jouables refusés.</para>
        ///
        /// <para>La sortie sur <c>|y| &gt; 5250</c> reste : au-delà la balle est dans un but et la
        /// prédiction n'a plus de sens.</para>
        /// </remarks>
        public BallSlice Find(Predicate<BallSlice> predicate)
        {
            if (Length == 0)
                return null;

            for (int i = 6; i < Length; i += 6)
            {
                if (!predicate(Slices[i]))
                {
                    if (MathF.Abs(Slices[i].Location.y) > 5250) break;
                    continue;
                }

                // Slices[i] convient : on cherche la première des six précédentes qui convienne aussi
                for (int j = i - 6; j < i; j++)
                {
                    if (MathF.Abs(Slices[j].Location.y) > 5250) return null;
                    if (predicate(Slices[j])) return Slices[j];
                }

                // Aucune des six ne convient : Slices[i] EST la première. C'est le retour qui manquait.
                return MathF.Abs(Slices[i].Location.y) > 5250 ? null : Slices[i];
            }

            return null;
        }
        
        /// <summary>Finds the first ball slice that is scoring in favor of the parameter team </summary>
        public BallSlice FindGoal(int team)
        {
            int otherSide = -Field.Side(team);
            if (Length > 0)
            {
                for (int i = 6; i < Length; i += 6)
                {
                    if (Slices[i].Location.y * otherSide > 5250)
                    {
                        for (int j = i - 6; j < i; j++)
                        {
                            if (Slices[j].Location.y * otherSide > 5250) 
                                return Slices[j];
                        }
                    }
                }
            }

            return null;
        }
    }
}
