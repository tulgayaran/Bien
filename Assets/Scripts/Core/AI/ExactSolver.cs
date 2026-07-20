using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// KÜÇÜK TUR KESİN ÇÖZÜCÜ (Tulga'nın "olasılıkla çözelim" fikri).
    /// İhale anında: rakip dünyalarını örnekler (Monte Carlo), her dünyada tam-bilgili
    /// minimax'la iki sınır hesaplar:
    ///   max = alabileceğim EN ÇOK el (rakipler beni engellemeye çalışırken)
    ///   min = mahkûm olduğum EN AZ el (kaçmaya çalışırken bile — koz mecburiyeti vs.)
    /// İhale b o dünyada tutturulabilir ⇔ min ≤ b ≤ max.
    /// P(b tutar) = dünya oranı; ihale = argmax P(b)·(b²+bonus).
    /// Rakiplerin hasmane oynadığı varsayımı temkinli bir alt sınırdır — küçük turda isabetli.
    /// </summary>
    public static class ExactSolver
    {
        /// <summary>Her b için tutturma olasılığı [0..n].</summary>
        public static double[] MakeProbabilities(IReadOnlyList<Card> myHand, int mySeat, Suit? trump,
                                                 Card? flipped, int leaderSeat, Random rng, int worlds)
        {
            int n = myHand.Count;
            var mySet = new HashSet<Card>(myHand);
            var unseen = new List<Card>(52);
            foreach (Suit s in Enum.GetValues(typeof(Suit)))
                for (int r = 2; r <= 14; r++)
                {
                    var c = new Card(s, (Rank)r);
                    if (mySet.Contains(c)) continue;
                    if (flipped.HasValue && flipped.Value == c) continue;
                    unseen.Add(c);
                }

            var makeCount = new int[n + 1];
            var hands = new List<Card>[4];
            var work = unseen.ToArray();

            for (int w = 0; w < worlds; w++)
            {
                // Fisher-Yates: ilk 3n kart rakiplere
                for (int i = 0; i < 3 * n; i++)
                {
                    int j = i + rng.Next(work.Length - i);
                    (work[i], work[j]) = (work[j], work[i]);
                }
                int idx = 0;
                for (int s = 0; s < 4; s++)
                {
                    if (s == mySeat) { hands[s] = new List<Card>(myHand); continue; }
                    hands[s] = new List<Card>(n);
                    for (int k = 0; k < n; k++) hands[s].Add(work[idx++]);
                }

                int mx = Solve(CloneHands(hands), leaderSeat, trump, mySeat, maximize: true);
                int mn = Solve(CloneHands(hands), leaderSeat, trump, mySeat, maximize: false);
                for (int b = mn; b <= mx && b <= n; b++) makeCount[b]++;
            }

            var probs = new double[n + 1];
            for (int b = 0; b <= n; b++) probs[b] = (double)makeCount[b] / worlds;
            return probs;
        }

        static List<Card>[] CloneHands(List<Card>[] h)
        {
            var c = new List<Card>[4];
            for (int i = 0; i < 4; i++) c[i] = new List<Card>(h[i]);
            return c;
        }

        static int Solve(List<Card>[] hands, int leader, Suit? trump, int me, bool maximize)
            => Rec(hands, new List<Card>(4), leader, trump, me, maximize, 0);

        /// <summary>Ben max/min el peşindeyim; üç rakip TERSİNİ dayatmaya çalışıyor (hasmane).</summary>
        static int Rec(List<Card>[] hands, List<Card> trick, int leader, Suit? trump,
                       int me, bool maximize, int myTricks)
        {
            int seat = (leader + trick.Count) % 4;
            if (hands[seat].Count == 0) return myTricks; // tur bitti

            Suit? led = trick.Count > 0 ? trick[0].Suit : (Suit?)null;
            var legal = GameRules.LegalPlays(hands[seat], led, trump);

            bool seatWantsMax = (seat == me) == maximize; // rakipler benim hedefimin tersini ister
            int best = seatWantsMax ? int.MinValue : int.MaxValue;

            foreach (var card in legal)
            {
                hands[seat].Remove(card);
                trick.Add(card);

                int val;
                if (trick.Count == 4)
                {
                    int winOff = GameRules.TrickWinnerOffset(trick, trick[0].Suit, trump);
                    int winner = (leader + winOff) % 4;
                    var saved = new List<Card>(trick);
                    trick.Clear();
                    val = Rec(hands, trick, winner, trump, me, maximize,
                              myTricks + (winner == me ? 1 : 0));
                    trick.AddRange(saved);
                    trick.RemoveAt(trick.Count - 1); // az sonra tekrar çıkarılacak kartı düzelt
                    trick.Add(card);
                }
                else
                {
                    val = Rec(hands, trick, leader, trump, me, maximize, myTricks);
                }

                trick.RemoveAt(trick.Count - 1);
                hands[seat].Add(card);

                if (seatWantsMax) { if (val > best) best = val; }
                else { if (val < best) best = val; }
            }
            return best;
        }
    }
}
