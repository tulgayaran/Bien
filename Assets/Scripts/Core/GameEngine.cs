using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bien.Core
{
    public sealed class RoundResult
    {
        public int[] Bids = new int[4];
        public int[] TricksWon = new int[4];
        public int[] Scores = new int[4];
        public Suit? Trump;
        public Card? TrumpCard;
        public int DealerSeat;
        public bool DealerWasRescued;
    }

    /// <summary>UI'ın dinleyeceği olaylar. Presentation katmanı sadece bunlara abone olur.</summary>
    public sealed class GameEvents
    {
        public Action<RoundConfig, int> RoundStarted;                  // config, dealerSeat
        public Action<IReadOnlyList<Card>[], Card?> HandsDealt;        // 4 el, koz kartı (sans'ta null)
        public Action<int> BidTurnStarted;                             // seat: sırası geldi, ihale bekleniyor
        public Action<int> PlayTurnStarted;                            // seat: sırası geldi, kart bekleniyor
        public Action<int, int> BidMade;                               // seat, bid
        public Action<int, int, int> BidRevised;                       // seat, oldBid, newBid
        public Action<int> DealerForcedToChange;                       // dealerSeat
        public Action<int, Card> CardPlayed;                           // seat, card
        public Action<int, IReadOnlyList<Card>> TrickWon;              // winnerSeat, trick
        public Action<RoundResult> RoundEnded;
        public Action<int[]> GameEnded;                                // toplam skorlar
    }

    public sealed class GameEngine
    {
        private readonly IPlayerAgent[] _agents;
        private readonly Random _rng;
        public readonly int[] TotalScores = new int[4];
        public readonly GameEvents Events = new();

        /// <summary>Turlar arası bekleme kancası: UI burada "son eli göster → tablo → Devam"
        /// akışını tamamlar; motor Task bitmeden bir sonraki tura BAŞLAMAZ. null ise beklemez.</summary>
        public Func<RoundResult, bool, Task> InterRoundGate; // (biten tur, sonMuydu)

        public GameEngine(IPlayerAgent[] agents, Random rng)
        {
            if (agents.Length != 4) throw new ArgumentException("4 oyuncu gerekli.");
            _agents = agents; _rng = rng;
            foreach (var a in agents)
                if (a is AI.IGameObserver o) WireObserver(o);
        }

        /// <summary>Gözlemci ajanlara kamuya açık olayları bağlar (el içerikleri geçmez).</summary>
        public void WireObserver(AI.IGameObserver o)
        {
            Events.RoundStarted += o.OnRoundStarted;
            Events.HandsDealt += (hands, tc) => o.OnTrumpRevealed(tc);
            Events.BidMade += o.OnBidMade;
            Events.BidRevised += (s, oldB, newB) => o.OnBidMade(s, newB);
            Events.CardPlayed += o.OnCardPlayed;
            Events.TrickWon += (w, cards) => o.OnTrickWon(w);
        }

        public async Task<List<RoundResult>> PlayGameAsync(int firstDealer = 0)
        {
            var results = new List<RoundResult>();
            int dealer = firstDealer;
            var rounds = GameStructure.BuildRounds();
            for (int ri = 0; ri < rounds.Count; ri++)
            {
                var r = await PlayRoundAsync(rounds[ri], dealer);
                results.Add(r);
                for (int i = 0; i < 4; i++) TotalScores[i] += r.Scores[i];
                dealer = (dealer + 1) % 4;

                if (InterRoundGate != null)
                    await InterRoundGate(r, ri == rounds.Count - 1);
            }
            Events.GameEnded?.Invoke(TotalScores);
            return results;
        }

        public async Task<RoundResult> PlayRoundAsync(RoundConfig rc, int dealerSeat)
        {
            var result = new RoundResult { DealerSeat = dealerSeat };
            Events.RoundStarted?.Invoke(rc, dealerSeat);

            // --- Dağıtım: dağıtıcının solundan (saat yönünde) ---
            var deck = new Deck();
            deck.Shuffle(_rng);
            var hands = new List<Card>[4];
            for (int i = 0; i < 4; i++) hands[i] = new List<Card>(rc.CardsPerPlayer);
            for (int c = 0; c < rc.CardsPerPlayer; c++)
                for (int p = 1; p <= 4; p++)
                    hands[(dealerSeat + p) % 4].Add(deck.Draw());

            // --- Koz: destenin üstü açılır (13'lükte deste boş → sans) ---
            Suit? trump = null;
            if (rc.HasTrump)
            {
                var tc = deck.Draw();
                result.TrumpCard = tc;
                trump = tc.Suit;
            }
            result.Trump = trump;
            Events.HandsDealt?.Invoke(hands.Select(h => (IReadOnlyList<Card>)h).ToArray(), result.TrumpCard);

            // --- İhale fazı ---
            var bids = new int?[4];
            int firstBidder = (dealerSeat + 1) % 4;
            for (int i = 0; i < 3; i++)
            {
                int seat = (firstBidder + i) % 4;
                Events.BidTurnStarted?.Invoke(seat);
                bids[seat] = ClampBid(await _agents[seat].MakeBidAsync(seat, hands[seat], rc, trump, bids, null), rc);
                Events.BidMade?.Invoke(seat, bids[seat].Value);
            }

            int sumOthers = bids.Where(b => b.HasValue).Sum(b => b.Value);
            int? forbidden = BiddingEngine.ForbiddenDealerBid(sumOthers, rc.CardsPerPlayer, rc.DealerRestricted);
            // İlk soruşta dağıtıcı gerçek arzusunu söyler (kısıt bildirilmez)
            Events.BidTurnStarted?.Invoke(dealerSeat);
            int dealerDesired = ClampBid(await _agents[dealerSeat].MakeBidAsync(dealerSeat, hands[dealerSeat], rc, trump, bids, null), rc);

            if (forbidden.HasValue && dealerDesired == forbidden.Value)
            {
                Events.BidMade?.Invoke(dealerSeat, dealerDesired); // arzu masaya kondu, ama yasak
                bool rescued = false;
                for (int i = 0; i < 3 && !rescued; i++)
                {
                    int seat = (firstBidder + i) % 4;
                    Events.BidTurnStarted?.Invoke(seat);
                    int? rev = await _agents[seat].OfferBidRevisionAsync(seat, hands[seat], rc, trump, bids, dealerDesired);
                    if (rev.HasValue && rev.Value != bids[seat])
                    {
                        int newBid = ClampBid(rev.Value, rc);
                        int newSum = sumOthers - bids[seat].Value + newBid;
                        if (rc.CardsPerPlayer - newSum != dealerDesired) // gerçekten kurtarıyorsa
                        {
                            Events.BidRevised?.Invoke(seat, bids[seat].Value, newBid);
                            bids[seat] = newBid;
                            sumOthers = newSum;
                            rescued = true;
                        }
                    }
                }
                if (rescued)
                {
                    bids[dealerSeat] = dealerDesired;
                    result.DealerWasRescued = true;
                }
                else
                {
                    Events.DealerForcedToChange?.Invoke(dealerSeat);
                    forbidden = BiddingEngine.ForbiddenDealerBid(sumOthers, rc.CardsPerPlayer, rc.DealerRestricted);
                    Events.BidTurnStarted?.Invoke(dealerSeat);
                    int forced = ClampBid(await _agents[dealerSeat].MakeBidAsync(dealerSeat, hands[dealerSeat], rc, trump, bids, forbidden), rc);
                    if (forbidden.HasValue && forced == forbidden.Value)
                        forced = forced > 0 ? forced - 1 : forced + 1;
                    bids[dealerSeat] = forced;
                    Events.BidMade?.Invoke(dealerSeat, forced);
                }
            }
            else
            {
                bids[dealerSeat] = dealerDesired;
                Events.BidMade?.Invoke(dealerSeat, dealerDesired);
            }

            for (int i = 0; i < 4; i++) result.Bids[i] = bids[i].Value;

            // --- Oyun fazı: ilk eli dağıtıcının solu açar, eli alan sonrakini açar ---
            int leader = (dealerSeat + 1) % 4;
            for (int t = 0; t < rc.CardsPerPlayer; t++)
            {
                var trick = new TrickState { LeaderSeat = leader };
                for (int i = 0; i < 4; i++)
                {
                    int seat = (leader + i) % 4;
                    Events.PlayTurnStarted?.Invoke(seat);
                    var card = await _agents[seat].PlayCardAsync(seat, hands[seat], trick, rc, trump);
                    if (!GameRules.IsLegalPlay(card, hands[seat], trick.LedSuit, trump))
                        throw new InvalidOperationException($"P{seat} geçersiz hamle: {card}");
                    hands[seat].Remove(card);
                    trick.Cards.Add(card);
                    Events.CardPlayed?.Invoke(seat, card);
                }
                int winnerSeat = (leader + GameRules.TrickWinnerOffset(trick.Cards, trick.Cards[0].Suit, trump)) % 4;
                result.TricksWon[winnerSeat]++;
                Events.TrickWon?.Invoke(winnerSeat, trick.Cards);
                leader = winnerSeat;
            }

            for (int i = 0; i < 4; i++)
                result.Scores[i] = ScoreEngine.RoundScore(result.Bids[i], result.TricksWon[i]);

            Events.RoundEnded?.Invoke(result);
            return result;
        }

        private static int ClampBid(int bid, RoundConfig rc) => Math.Clamp(bid, 0, rc.CardsPerPlayer);
    }
}