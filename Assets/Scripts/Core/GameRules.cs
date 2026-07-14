using System.Collections.Generic;

namespace Bien.Core
{
    public static class GameRules
    {
        /// <summary>
        /// Hamle geçerliliği:
        /// 1) İstenen renkten kart varsa → o renkten atmak zorunlu (yükseltme şartı YOK).
        /// 2) İstenen renk yoksa ve kozlu turdaysak → koz varsa koz zorunlu (üstünü geçme şartı YOK).
        /// 3) İkisi de yoksa (veya sans'ta renk yoksa) → serbest.
        /// </summary>
        public static bool IsLegalPlay(Card card, IReadOnlyList<Card> hand, Suit? ledSuit, Suit? trump)
        {
            if (ledSuit == null) return true; // eli açan serbest

            if (HasSuit(hand, ledSuit.Value))
                return card.Suit == ledSuit.Value;

            if (trump.HasValue && HasSuit(hand, trump.Value))
                return card.Suit == trump.Value;

            return true;
        }

        public static List<Card> LegalPlays(IReadOnlyList<Card> hand, Suit? ledSuit, Suit? trump)
        {
            var result = new List<Card>();
            foreach (var c in hand)
                if (IsLegalPlay(c, hand, ledSuit, trump)) result.Add(c);
            return result;
        }

        /// <summary>Eli açan oyuncuya göre kazananın offset'ini döner (0 = eli açan).</summary>
        public static int TrickWinnerOffset(IReadOnlyList<Card> trick, Suit ledSuit, Suit? trump)
        {
            int winner = 0;
            for (int i = 1; i < trick.Count; i++)
                if (Beats(trick[i], trick[winner], trump)) winner = i;
            return winner;
        }

        private static bool Beats(Card challenger, Card current, Suit? trump)
        {
            bool cTrump = trump.HasValue && challenger.Suit == trump.Value;
            bool wTrump = trump.HasValue && current.Suit == trump.Value;
            if (cTrump != wTrump) return cTrump;          // koz, koz olmayanı yener
            if (challenger.Suit != current.Suit) return false; // farklı renk (koz da değil) kazanamaz
            return challenger.Rank > current.Rank;
        }

        private static bool HasSuit(IReadOnlyList<Card> hand, Suit s)
        {
            for (int i = 0; i < hand.Count; i++)
                if (hand[i].Suit == s) return true;
            return false;
        }
    }
}
