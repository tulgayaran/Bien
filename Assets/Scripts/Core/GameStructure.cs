using System.Collections.Generic;

namespace Bien.Core
{
    public readonly struct RoundConfig
    {
        public readonly int RoundIndex;        // 0-tabanlı
        public readonly int CardsPerPlayer;    // 1..13
        public readonly bool HasTrump;         // 13'lük sans turlarda false
        public readonly bool DealerRestricted; // ilk 4 tur (1-4 kart) false

        public RoundConfig(int index, int cards, bool hasTrump, bool restricted)
        { RoundIndex = index; CardsPerPlayer = cards; HasTrump = hasTrump; DealerRestricted = restricted; }
    }

    public static class GameStructure
    {
        public const int PlayerCount = 4;

        /// <summary>
        /// Tur dizisi: 1,2,...,12 kart (kozlu) + 4 × 13 kart (sans) = 16 tur.
        /// 13 kartlık turlarda 13×4=52 → deste biter, koz açılamaz → sans.
        /// Dağıtıcı kısıtı ilk 4 turda (1-4 kart) uygulanmaz.
        /// </summary>
        public static List<RoundConfig> BuildRounds()
        {
            var rounds = new List<RoundConfig>(16);
            int idx = 0;
            for (int cards = 1; cards <= 12; cards++)
                rounds.Add(new RoundConfig(idx++, cards, hasTrump: true, restricted: cards > 4));
            for (int i = 0; i < 4; i++)
                rounds.Add(new RoundConfig(idx++, 13, hasTrump: false, restricted: true));
            return rounds;
        }
    }

    public static class BiddingEngine
    {
        /// <summary>Dağıtıcının söyleyemeyeceği değer; kısıt yoksa veya değer aralık dışıysa null.</summary>
        public static int? ForbiddenDealerBid(int sumOfOtherBids, int cardsThisRound, bool restricted)
        {
            if (!restricted) return null;
            int forbidden = cardsThisRound - sumOfOtherBids;
            return (forbidden >= 0 && forbidden <= cardsThisRound) ? forbidden : null;
        }

        public static bool IsDealerBidLegal(int bid, int sumOfOtherBids, int cardsThisRound, bool restricted)
        {
            if (bid < 0 || bid > cardsThisRound) return false;
            var forbidden = ForbiddenDealerBid(sumOfOtherBids, cardsThisRound, restricted);
            return forbidden == null || bid != forbidden.Value;
        }
    }

    public static class ScoreEngine
    {
        public const int MakeBonus = 10;

        /// <summary>Tam tutturan tahmin² + 10 alır (0 ihale → 10 puan), batan 0 alır.</summary>
        public static int RoundScore(int bid, int tricksWon)
            => bid == tricksWon ? bid * bid + MakeBonus : 0;
    }
}
