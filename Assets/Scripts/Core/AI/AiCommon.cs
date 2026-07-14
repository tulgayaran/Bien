using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    public enum AiDifficulty { Easy, Normal, Hard }

    public static class AiFactory
    {
        public static IPlayerAgent Create(AiDifficulty d, Random rng) => d switch
        {
            AiDifficulty.Easy => new EasyAgent(rng),
            AiDifficulty.Normal => new NormalAgent(rng),
            AiDifficulty.Hard => new HardAgent(rng),
            _ => new NormalAgent(rng)
        };
    }

    /// <summary>Motorun, uygulayan ajanlara kamuya açık oyun olaylarını beslediği arayüz.
    /// (Sadece herkesin masada görebileceği bilgi — başka ellerin içeriği ASLA geçmez.)</summary>
    public interface IGameObserver
    {
        void OnRoundStarted(RoundConfig rc, int dealerSeat);
        void OnTrumpRevealed(Card? trumpCard);
        void OnBidMade(int seat, int bid);
        void OnCardPlayed(int seat, Card card);
        void OnTrickWon(int winnerSeat);
    }

    /// <summary>Kamuya açık bilgiden tur hafızası: oynanmış kartlar, renk boşluk çıkarımı,
    /// ihaleler, alınan eller. Her AI ajanının kendi kopyası olur.</summary>
    public sealed class GameMemory : IGameObserver
    {
        /// <summary>Kart sayma dikkati: 1.0 = her kartı kaydeder, 0.2 = %20'sini.
        /// Kaçırılan kart hafızada "görülmemiş" kalır → boss tespiti ve boşluk çıkarımı zayıflar.</summary>
        public double Attention = 1.0;
        private readonly Random _rng = new();

        public RoundConfig Round { get; private set; }
        public int DealerSeat { get; private set; }
        public Suit? Trump { get; private set; }
        public Card? TrumpCard { get; private set; }
        public readonly int[] Bids = new int[4];
        public readonly int[] TricksWon = new int[4];
        public readonly HashSet<Card> Played = new();
        public readonly int[] CardsPlayedBySeat = new int[4];

        private readonly bool[,] _voidIn = new bool[4, 4]; // [seat, suit]
        private readonly List<Card> _curTrick = new(4);
        private Suit? _curLed;

        public void OnRoundStarted(RoundConfig rc, int dealerSeat)
        {
            Round = rc; DealerSeat = dealerSeat;
            Trump = null; TrumpCard = null;
            Array.Clear(Bids, 0, 4); Array.Clear(TricksWon, 0, 4);
            Array.Clear(CardsPlayedBySeat, 0, 4);
            Played.Clear(); _curTrick.Clear(); _curLed = null;
            for (int s = 0; s < 4; s++) for (int c = 0; c < 4; c++) _voidIn[s, c] = false;
        }

        public void OnTrumpRevealed(Card? trumpCard)
        {
            TrumpCard = trumpCard;
            Trump = trumpCard?.Suit;
        }

        public void OnBidMade(int seat, int bid) => Bids[seat] = bid;

        public void OnCardPlayed(int seat, Card card)
        {
            // Masadaki mevcut el herkesin gözü önünde: el yapısı her zaman tam izlenir
            if (_curTrick.Count == 0) _curLed = card.Suit;
            else if (_curLed.HasValue && card.Suit != _curLed.Value)
            {
                if (_rng.NextDouble() < Attention)
                    _voidIn[seat, (int)_curLed.Value] = true; // "X bu renkte kalmadı"yı fark etme
            }
            _curTrick.Add(card);
            if (_rng.NextDouble() < Attention)
                Played.Add(card); // uzun vadeli sayım: dikkat oranında
            CardsPlayedBySeat[seat]++;
        }

        public void OnTrickWon(int winnerSeat)
        {
            TricksWon[winnerSeat]++;
            _curTrick.Clear(); _curLed = null;
        }

        public bool IsVoid(int seat, Suit suit) => _voidIn[seat, (int)suit];

        /// <summary>Bu kart, kendi rengi içinde rakiplerde olabilecek en büyük mü?
        /// (oynanmışlar + benim elim + açılan koz kartı hariç, daha büyüğü görülmemişse boss)</summary>
        public bool IsBoss(Card card, IReadOnlyList<Card> myHand)
        {
            for (int r = (int)card.Rank + 1; r <= 14; r++)
            {
                var higher = new Card(card.Suit, (Rank)r);
                if (Played.Contains(higher)) continue;
                if (myHand.Contains(higher)) continue;
                if (TrumpCard.HasValue && TrumpCard.Value == higher) continue;
                return false; // görülmemiş daha büyük var → rakipte olabilir
            }
            return true;
        }

        /// <summary>Rakiplerde olabilecek (görülmemiş) tüm kartlar.</summary>
        public List<Card> UnseenCards(IReadOnlyList<Card> myHand)
        {
            var unseen = new List<Card>(39);
            for (int s = 0; s < 4; s++)
                for (int r = 2; r <= 14; r++)
                {
                    var c = new Card((Suit)s, (Rank)r);
                    if (Played.Contains(c) || myHand.Contains(c)) continue;
                    if (TrumpCard.HasValue && TrumpCard.Value == c) continue;
                    unseen.Add(c);
                }
            return unseen;
        }
    }

    /// <summary>El gücünden ihale tahmini. Normal ve Hard'ın temeli, Easy gürültülü kullanır.</summary>
    public static class HandEvaluator
    {
        public static double EstimateTricks(IReadOnlyList<Card> hand, Suit? trump)
        {
            double est = 0;
            var bySuit = hand.GroupBy(c => c.Suit).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Rank).ToList());

            foreach (var (suit, cards) in bySuit)
            {
                bool isTrump = trump.HasValue && suit == trump.Value;
                int len = cards.Count;
                foreach (var c in cards)
                {
                    if (c.Rank == Rank.Ace) est += isTrump ? 1.0 : 0.95;
                    else if (c.Rank == Rank.King) est += len >= 2 ? 0.7 : 0.35;
                    else if (c.Rank == Rank.Queen) est += len >= 3 ? 0.35 : 0.15;
                }
                if (isTrump && len > 3) est += (len - 3) * 0.6; // uzun koz
                if (!isTrump && !trump.HasValue && len > 4) est += (len - 4) * 0.45; // sans'ta uzun renk
            }

            // Kısa yan renk + koz = çakma potansiyeli
            if (trump.HasValue && bySuit.TryGetValue(trump.Value, out var trumps) && trumps.Count >= 2)
            {
                foreach (Suit s in Enum.GetValues(typeof(Suit)))
                {
                    if (s == trump.Value) continue;
                    int len = bySuit.TryGetValue(s, out var cs) ? cs.Count : 0;
                    if (len == 0) est += 0.8;
                    else if (len == 1) est += 0.4;
                }
            }
            return Math.Min(est, hand.Count);
        }

        public static int LegalizeBid(int bid, int maxBid, int? forbidden)
        {
            bid = Math.Clamp(bid, 0, maxBid);
            if (forbidden.HasValue && bid == forbidden.Value)
                bid = bid > 0 ? bid - 1 : bid + 1;
            return Math.Clamp(bid, 0, maxBid);
        }
    }
}
