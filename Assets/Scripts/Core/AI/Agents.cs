using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bien.Core.AI
{
    /// <summary>
    /// Üç zorluk aynı iskelet, farklar parametrik:
    ///   Attention — kart sayma kalitesi (Easy .20 / Normal .50 / Hard 1.0)
    ///   DevProb   — ihalede sapma olasılığı (Tulga kuralı)
    ///   DevMag    — sapma genliği: toplam ± U(0..mag) × kartSayısı
    ///   SloppyPlay— plansız rastgele hamle oranı
    /// İHALE = TULGA PUANLAMASI: koz A=1.0→...→5=0.1, altı 0.05;
    /// yan A=0.8→...→7=0.1, altı 0. Toplam yuvarlanır (Hard), Normal/Easy olasılıkla sapar.
    /// </summary>
    public class TableAgent : IPlayerAgent, IGameObserver
    {
        /// <summary>Debug: her ihale/hamle gerekçesi buraya yazılır (null ise maliyet yok).</summary>
        public Action<string> Debug;

        protected readonly Random Rng;
        protected readonly GameMemory Mem = new();
        private readonly double _devProb, _devMag, _sloppyPlay;

        private readonly SkillTier _tier;

        public TableAgent(Random rng, double attention, double devProb, double devMag, double sloppyPlay,
                          SkillTier tier = SkillTier.Hard)
        {
            Rng = rng;
            Mem.Attention = attention;
            _devProb = devProb; _devMag = devMag; _sloppyPlay = sloppyPlay;
            _tier = tier;
        }

        // ---- IGameObserver ----
        public void OnRoundStarted(RoundConfig rc, int d) => Mem.OnRoundStarted(rc, d);
        public void OnTrumpRevealed(Card? tc) => Mem.OnTrumpRevealed(tc);
        public void OnBidMade(int s, int b) => Mem.OnBidMade(s, b);
        public void OnCardPlayed(int s, Card c) => Mem.OnCardPlayed(s, c);
        public void OnTrickWon(int w) => Mem.OnTrickWon(w);

        // ---- İhale: Tulga puanlaması ----
        public static double CardPoints(Card c, Suit? trump, int roundSize)
        {
            int r = (int)c.Rank;

            if (trump.HasValue && c.Suit == trump.Value)
            {
                // 1 kartlık tur: KESİN olasılık. Kaçış yok, koz her yan kartı döver;
                // tek soru: 3 rakip kartında benden büyük koz var mı? (50 görünmezden çekiliş)
                if (roundSize == 1)
                {
                    int h = 14 - r; // benden büyük koz adedi
                    double p1 = 1.0;
                    for (int i = 0; i < 3; i++) p1 *= (50.0 - h - i) / (50.0 - i);
                    return p1; // örn. koz 7 → %63, koz 9 → %72, koz A → %100
                }
                double p = r >= 5 ? (r - 4) / 10.0 : 0.05;   // A=1.0, K=0.9 ... 5=0.1, altı 0.05
                // Küçük tur koz takviyesi: 2 kart %26 → 5 kart %15 (lineer), 6+ değişmez
                if (roundSize <= 5)
                    p *= 1.0 + (0.30 - (roundSize - 1) * 0.0375);
                return p;
            }

            if (!trump.HasValue)
            {
                // SANS: her renk kendi içinde koz gibi ama merdiven DİK — büyükler kral,
                // küçükler ölü. Sağlama: el başına ~3.4 → masa toplamı ~13.6 ≈ 13 el ✓
                return r switch
                {
                    14 => 1.00,
                    13 => 0.85,
                    12 => 0.65,
                    11 => 0.45,
                    10 => 0.30,
                    9 => 0.15,
                    8 => 0.05,
                    _ => 0.0
                };
            }
            return r >= 7 ? (r - 6) / 10.0 : 0.0;            // yan: A=0.8 ... 7=0.1, altı 0
        }

        public static double HandPoints(IReadOnlyList<Card> hand, Suit? trump, int roundSize)
        {
            double sum = 0;
            foreach (var c in hand) sum += CardPoints(c, trump, roundSize);
            return sum;
        }

        protected HandPlan Plan; // tur planı: ihalede kurulur, oyunda revize edilir

        public Task<int> MakeBidAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                      IReadOnlyList<int?> bidsSoFar, int? forbidden)
        {
            int n = round.CardsPerPlayer;
            Plan = HandPlan.Build(hand, trump, n);

            // KÜÇÜK TURLAR (≤3 kart): kesin çözücü — dünyaları örnekle, minimax bandından
            // P(b tutar) çıkar, EV maksimize et. Puan merdiveni yerine gerçek olasılık.
            if (n <= 4)
            {
                int worlds = n == 1 ? 300 : n == 2 ? 350 : n == 3 ? 160 : 110;
                int leaderSeat = (Mem.DealerSeat + 1) % 4;
                var pMake = ExactSolver.MakeProbabilities(hand, seat, trump, Mem.TrumpCard,
                                                          leaderSeat, Rng, worlds);
                int exBid = 0; double bestEv = double.MinValue;
                for (int b = 0; b <= n; b++)
                {
                    if (forbidden.HasValue && b == forbidden.Value) continue;
                    double ev = pMake[b] * (b * b + ScoreEngine.MakeBonus);
                    if (ev > bestEv) { bestEv = ev; exBid = b; }
                }
                // Mizaç: Normal/Easy olasılıkla ±1 şaşar
                if (_devProb > 0 && Rng.NextDouble() < _devProb)
                {
                    int alt = exBid + (Rng.NextDouble() < 0.5 ? -1 : 1);
                    if (alt >= 0 && alt <= n && (!forbidden.HasValue || alt != forbidden.Value)) exBid = alt;
                }
                Plan.Commit(exBid);
                if (Debug != null)
                {
                    var pp = string.Join(", ", Enumerable.Range(0, n + 1)
                        .Select(b => $"P({b})=%{pMake[b] * 100:F0}"));
                    Debug($"İHALE {exBid} ← çözücü ({worlds} dünya): {pp}");
                    Debug($"PLAN: {Plan.Describe()} | mod: {Plan.Stance}");
                }
                return Task.FromResult(exBid);
            }

            double raw = Plan.RawBid; // W + S/2
            double adjusted = raw;
            bool deviated = false;

            if (forbidden == null && _devProb > 0 && Rng.NextDouble() < _devProb)
            {
                // Mizaç: ± U(0..mag) × kart sayısı — büyük ellerde acemilik daha pahalı
                adjusted += (Rng.NextDouble() * 2 - 1) * _devMag * n;
                deviated = true;
            }

            // Yuvarlama (Tulga): tam .5 kesir — elde Winner VARSA yukarı (kontrol → cesaret),
            // yoksa aşağı (kontrolsüz yalnız-Swing yarımı temkinle yutulur).
            int bid;
            if (forbidden.HasValue)
                bid = NearestLegal(raw, n, forbidden.Value); // zorunlu yeniden ihale: en yakın legal
            else
            {
                double fl = Math.Floor(adjusted);
                bool half = Math.Abs(adjusted - fl - 0.5) < 1e-9;
                bid = half ? (int)fl + (Plan.Winners >= 1 ? 1 : 0)
                           : (int)Math.Round(adjusted, MidpointRounding.AwayFromZero);
                bid = Math.Clamp(bid, 0, n);
            }

            // Sıfır emniyeti (Tulga, rafine): 0 ancak KAÇABİLEN elle denir. Kaçamayan
            // Swing/Winner varsa taban 1 — o kart istemeden kazanır, 0 yanar.
            // Kaçamayan = koz Swing'i/Winner'ı (koz mecburiyeti zorla kazandırır) veya
            // tekli yan büyüğü (renk açılınca mecburen çıkar). Yanında küçüğü olan yan
            // büyüğü kaçabilir (altına dalar), o 0'a engel değil.
            bool zeroLifted = false;
            if (bid == 0 && (!forbidden.HasValue || forbidden.Value != 1) && n >= 1)
            {
                bool unduckable = hand.Any(c =>
                {
                    if (CardPoints(c, trump, n) < 0.5) return false;         // Swing altı tehdit değil
                    if (trump.HasValue && c.Suit == trump.Value) return true; // koz: mecburiyet riski
                    return hand.Count(h => h.Suit == c.Suit) == 1;            // tekli yan büyüğü
                });
                if (unduckable) { bid = 1; zeroLifted = true; }
            }

            Plan.Commit(bid);

            if (Debug != null)
            {
                var parts = hand.OrderByDescending(c => CardPoints(c, trump, n))
                                .Select(c => $"{c} {CardPoints(c, trump, n):F2}");
                string msg = $"İHALE {bid} ← {string.Join(" ", parts)} | W+S/2 = {Plan.Winners}+{Plan.Swings}/2 = {raw:F1}";
                if (deviated) msg += $" | mizaç → {adjusted:F2}";
                if (zeroLifted) msg += " | 0 güvensiz (kaçamayan Swing) → 1";
                if (forbidden.HasValue) msg += $" | yasak {forbidden.Value}, en yakın legal";
                Debug(msg);
                Debug($"PLAN: {Plan.Describe()} | mod: {Plan.Stance}");
            }
            return Task.FromResult(bid);
        }

        private static int NearestLegal(double raw, int n, int forbidden)
        {
            int best = -1; double bestDist = double.MaxValue;
            for (int b = 0; b <= n; b++)
            {
                if (b == forbidden) continue;
                double d = Math.Abs(raw - b);
                if (d < bestDist || (d == bestDist && b < best)) { bestDist = d; best = b; }
            }
            return best;
        }

        public Task<int?> OfferBidRevisionAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                                IReadOnlyList<int?> currentBids, int dealerDesiredBid)
        {
            // Öz çıkar: ham W+S/2 mevcut ihalemden belirgin sapıyorsa 1 oynat
            double sum = Plan?.RawBid ?? HandPlan.Build(hand, trump, round.CardsPerPlayer).RawBid;
            int cur = currentBids[seat] ?? 0;
            int alt = sum > cur ? cur + 1 : cur - 1;
            if (Math.Abs(sum - cur) >= 0.7 && alt >= 0 && alt <= round.CardsPerPlayer)
                return Task.FromResult<int?>(alt);
            return Task.FromResult<int?>(null);
        }

        // ---- Oyun ----
        public Task<Card> PlayCardAsync(int seat, IReadOnlyList<Card> hand, TrickState trick, RoundConfig round, Suit? trump)
        {
            // Her hamleden önce plan tazelenir: W + S/2 ≈ kalan ihtiyaç, mod = masa dengesi
            int tableSurplus = round.CardsPerPlayer - (Mem.Bids[0] + Mem.Bids[1] + Mem.Bids[2] + Mem.Bids[3]);
            if (Plan != null && Debug != null)
            {
                var changes = Plan.Rebalance(hand, trump, round.CardsPerPlayer, Mem.TricksWon[seat], Mem, tableSurplus);
                foreach (var ch in changes) Debug($"  PLAN {ch}");
            }
            else Plan?.Rebalance(hand, trump, round.CardsPerPlayer, Mem.TricksWon[seat], Mem, tableSurplus);

            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count > 1 && Rng.NextDouble() < _sloppyPlay)
            {
                var slip = legal[Rng.Next(legal.Count)];
                Debug?.Invoke($"{slip} — dalgınlık, plansız attım");
                return Task.FromResult(slip);
            }
            Card card; string reason;
            // Kural kitabı: kişisel ihtiyacı bitmiş (veya batmış) oyuncuya uygulanır.
            // Masa-fazlalığı Ducking'i ihtiyacı sürenlere D5 ile As attırıyordu (turnuva yakaladı) —
            // onlar şimdilik eski politikada; "fazlalıkta pasif oyun" kuralları ayrıca yazılacak.
            // Üç mod, üç kitap — icra tamamen kural kitaplarında
            if (Plan == null)
                card = BalancedBook.Decide(seat, hand, trick, trump, Mem,
                        HandPlan.Build(hand, trump, round.CardsPerPlayer), _tier, Rng, out reason); // emniyet
            else if (Plan.Stance == PlayerStance.Ducking)
                card = DuckingBook.Decide(seat, hand, trick, trump, Mem, _tier, Rng, out reason);
            else if (Plan.Stance == PlayerStance.Hunting)
                card = HuntingBook.Decide(seat, hand, trick, trump, Mem, Plan, _tier, Rng, out reason);
            else
                card = BalancedBook.Decide(seat, hand, trick, trump, Mem, Plan, _tier, Rng, out reason);
            Debug?.Invoke($"{card} [{Plan?.RoleOf(card)}, mod {Plan?.Stance}] — {reason}");
            return Task.FromResult(card);
        }
    }

    public sealed class EasyAgent : TableAgent
    {
        public EasyAgent(Random rng)
            : base(rng, attention: 0.20, devProb: 0.50, devMag: 0.20, sloppyPlay: 0.15, SkillTier.Easy) { }
    }

    public sealed class NormalAgent : TableAgent
    {
        public NormalAgent(Random rng)
            : base(rng, attention: 0.50, devProb: 0.30, devMag: 0.10, sloppyPlay: 0.03, SkillTier.Normal) { }
    }

    public sealed class HardAgent : TableAgent
    {
        public HardAgent(Random rng)
            : base(rng, attention: 1.00, devProb: 0.0, devMag: 0.0, sloppyPlay: 0.0, SkillTier.Hard) { }
    }

}