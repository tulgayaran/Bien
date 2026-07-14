using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// Yerel olasılık motoru: "bu kartı ŞİMDİ oynarsam eli alma olasılığım kaç?"
    /// Hafızadaki görülmemiş kart kümesinden hipergeometrik hesap — kombinasyon
    /// patlaması yok, kart başına mikrosaniyelik iş.
    /// Dikkati düşük ajanın 'görülmemiş' kümesi şişik kalır → olasılıkları doğal yanılır.
    /// </summary>
    public static class Prob
    {
        /// <summary>h kart çeken rakibin, U görülmemiş karttan m tehdit kartının
        /// en az birini tutuyor olma olasılığı.</summary>
        public static double HasAtLeastOne(int m, int h, int U)
        {
            if (m <= 0 || h <= 0 || U <= 0) return 0;
            if (m >= U) return 1;
            double pNone = 1;
            for (int i = 0; i < h && i < U; i++)
                pNone *= Math.Max(0, U - m - i) / (double)(U - i);
            return 1 - pNone;
        }

        /// <summary>Rakibin istenen renkte hiç kartı olmama (boş olma) olasılığı.</summary>
        public static double VoidProb(int suitUnseen, int h, int U)
        {
            if (suitUnseen <= 0) return 1;
            if (h <= 0 || U <= 0) return 0;
            double pNone = 1;
            for (int i = 0; i < h && i < U; i++)
                pNone *= Math.Max(0, U - suitUnseen - i) / (double)(U - i);
            return pNone;
        }

        /// <summary>
        /// Bu kartı şimdi oynarsam eli alma olasılığım.
        /// trick boşsa el açma senaryosu; doluysa önce masayı geçmesi gerekir (geçmiyorsa 0).
        /// </summary>
        public static double WinProbability(Card c, IReadOnlyList<Card> hand, TrickState trick,
                                            Suit? trump, GameMemory mem, int mySeat)
        {
            bool leading = trick.Cards.Count == 0;
            Suit led = leading ? c.Suit : trick.Cards[0].Suit;

            // Masadakileri geçemiyorsa olasılık sıfır
            if (!leading)
            {
                var t = new List<Card>(trick.Cards) { c };
                if (GameRules.TrickWinnerOffset(t, led, trump) != t.Count - 1) return 0;
            }

            var unseen = mem.UnseenCards(hand);
            int U = unseen.Count;
            if (U == 0) return 1;

            bool cIsTrump = trump.HasValue && c.Suit == trump.Value;
            int higherInLed = unseen.Count(u => u.Suit == led && u.Rank > c.Rank);
            int higherTrumps = trump.HasValue ? unseen.Count(u => u.Suit == trump.Value && u.Rank > c.Rank) : 0;
            int anyTrumps = trump.HasValue ? unseen.Count(u => u.Suit == trump.Value) : 0;
            int ledUnseen = unseen.Count(u => u.Suit == led);

            double pWin = 1;
            foreach (int p in SeatsStillToPlay(trick, mySeat, leading))
            {
                int h = mem.Round.CardsPerPlayer - mem.CardsPlayedBySeat[p];
                if (h <= 0) continue;

                int oppNeed = mem.Bids[p] - mem.TricksWon[p];
                double duck = oppNeed > 0 ? 1.0 : 0.30; // ihalesi dolan bilerek altına kaçar

                double pBeat;
                bool knownVoidLed = mem.IsVoid(p, led);

                if (cIsTrump)
                {
                    if (led == trump.Value)
                    {
                        // Koz eli: takip zorunlu; büyük kozu varsa yenebilir (istek indirimli)
                        pBeat = HasAtLeastOne(higherTrumps, h, U) * duck;
                    }
                    else
                    {
                        // Ben yan renge çaktım: ancak o renkte boş VE daha büyük kozu olan söker.
                        // Koz mecburiyeti: büyük kozu tek koz ise İSTEMESE de basar → indirim yarım.
                        double pVoid = knownVoidLed ? 1.0 : VoidProb(ledUnseen, h, U);
                        pBeat = pVoid * HasAtLeastOne(higherTrumps, h, U) * (oppNeed > 0 ? 1.0 : 0.65);
                    }
                }
                else
                {
                    // Yan renk (veya sans) kartı oynadım
                    double pFollowBeat = knownVoidLed ? 0
                        : HasAtLeastOne(higherInLed, h, U) * duck;

                    double pRuff = 0;
                    if (trump.HasValue)
                    {
                        // Boşsa koz ZORUNLU → istek indirimi yok
                        double pVoid = knownVoidLed ? 1.0 : VoidProb(ledUnseen, h, U);
                        pRuff = pVoid * HasAtLeastOne(anyTrumps, h, U);
                    }
                    pBeat = 1 - (1 - pFollowBeat) * (1 - pRuff);
                }

                pWin *= (1 - Math.Clamp(pBeat, 0, 1));
            }
            return pWin;
        }

        private static IEnumerable<int> SeatsStillToPlay(TrickState trick, int mySeat, bool leading)
        {
            if (leading)
            {
                for (int i = 1; i <= 3; i++) yield return (mySeat + i) % 4;
            }
            else
            {
                int played = trick.Cards.Count; // ben index 'played' olacağım
                for (int i = played + 1; i <= 3; i++) yield return (trick.LeaderSeat + i) % 4;
            }
        }
    }
}
