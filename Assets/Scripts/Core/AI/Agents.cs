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

        public TableAgent(Random rng, double attention, double devProb, double devMag, double sloppyPlay)
        {
            Rng = rng;
            Mem.Attention = attention;
            _devProb = devProb; _devMag = devMag; _sloppyPlay = sloppyPlay;
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
                double p = r >= 5 ? (r - 4) / 10.0 : 0.05;   // A=1.0, K=0.9 ... 5=0.1, altı 0.05
                // Küçük tur koz takviyesi: 1 kart %20 → 5 kart %10 (lineer), 6+ değişmez
                if (roundSize <= 5)
                    p *= 1.0 + (0.20 - (roundSize - 1) * 0.025);
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
            double raw = Plan.RawBid; // W + S/2
            double adjusted = raw;
            bool deviated = false;

            if (forbidden == null && _devProb > 0 && Rng.NextDouble() < _devProb)
            {
                // Mizaç: ± U(0..mag) × kart sayısı — büyük ellerde acemilik daha pahalı
                adjusted += (Rng.NextDouble() * 2 - 1) * _devMag * n;
                deviated = true;
            }

            // Yuvarlama: tam .5 kesirler AŞAĞI (tek Swing yazı-turadır, ihale değil — temkin)
            int bid = forbidden.HasValue
                ? NearestLegal(raw, n, forbidden.Value) // zorunlu yeniden ihale: ham değere en yakın legal
                : Math.Clamp((int)Math.Ceiling(adjusted - 0.5), 0, n);

            Plan.Commit(bid);

            if (Debug != null)
            {
                var parts = hand.OrderByDescending(c => CardPoints(c, trump, n))
                                .Select(c => $"{c} {CardPoints(c, trump, n):F2}");
                string msg = $"İHALE {bid} ← {string.Join(" ", parts)} | W+S/2 = {Plan.Winners}+{Plan.Swings}/2 = {raw:F1}";
                if (deviated) msg += $" | mizaç → {adjusted:F2}";
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
            bool needDone = Plan != null && Plan.TargetTricks - Mem.TricksWon[seat] <= 0;
            if (Plan != null && Plan.Stance == PlayerStance.Ducking && needDone)
                card = DuckingBook.Decide(hand, trick, trump, Mem, Rng, out reason);
            else if (Plan != null && Plan.Stance == PlayerStance.Hunting)
                card = HuntingBook.Decide(seat, hand, trick, trump, Mem, Plan, Rng, out reason);
            else if (Plan != null && Plan.Stance == PlayerStance.Balanced)
                card = BalancedBook.Decide(seat, hand, trick, trump, Mem, Plan, Rng, out reason);
            else
                card = Policy.Decide(seat, hand, trick, trump, Mem, Rng, out reason);
            Debug?.Invoke($"{card} [{Plan?.RoleOf(card)}, mod {Plan?.Stance}] — {reason}");
            return Task.FromResult(card);
        }
    }

    public sealed class EasyAgent : TableAgent
    {
        public EasyAgent(Random rng)
            : base(rng, attention: 0.20, devProb: 0.50, devMag: 0.20, sloppyPlay: 0.35) { }
    }

    public sealed class NormalAgent : TableAgent
    {
        public NormalAgent(Random rng)
            : base(rng, attention: 0.50, devProb: 0.30, devMag: 0.10, sloppyPlay: 0.06) { }
    }

    public sealed class HardAgent : TableAgent
    {
        public HardAgent(Random rng)
            : base(rng, attention: 1.00, devProb: 0.0, devMag: 0.0, sloppyPlay: 0.0) { }
    }

    /// <summary>İhtiyaç bazlı ortak oyun politikası — hafıza kalitesi kararları doğal ayrıştırır:
    /// dikkat düşükse boss tespiti ve boşluk çıkarımı yanılır, aynı kod kötü oynar.</summary>
    public static class Policy
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, Random rng)
            => Decide(seat, hand, trick, trump, mem, rng, out _);

        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            int need = mem.Bids[seat] - mem.TricksWon[seat];
            if (legal.Count == 1) { reason = $"tek geçerli kart (ihtiyaç {need})"; return legal[0]; }

            // Her legal kart için yerel alma olasılığı
            var pw = legal.ToDictionary(c => c, c => Prob.WinProbability(c, hand, trick, trump, mem, seat));
            bool leading = trick.Cards.Count == 0;

            // Masa dengesi: + ise sahipsiz el var (herkes kaçacak), - ise el kıtlığı (kapışma)
            int surplus = mem.Round.CardsPerPlayer - (mem.Bids[0] + mem.Bids[1] + mem.Bids[2] + mem.Bids[3]);

            // SAĞLAM garanti: yalnız KOZ boss'u — ne zaman oynansa alır, çakılamaz, el sırası gerektirmez.
            // (Sans boss'u bozdurma el sırası ister → sağlam sayılmaz. Yan renk As'ı zaten eriyendir.)
            bool IsStableBank(Card c) => trump.HasValue && c.Suit == trump.Value && mem.IsBoss(c, hand);
            int stableBank = trump.HasValue ? hand.Count(IsStableBank) : 0;

            if (need > 0 && stableBank >= need)
            {
                // Koz garantileri ihtiyacı kapatıyor: GERÇEKTEN kaçabiliyorsam bu eli pas geç, yük at.
                var reserved = hand.Where(IsStableBank).OrderByDescending(c => c.Rank)
                                   .Take(need).ToHashSet();
                var cands = legal.Where(c => !reserved.Contains(c)).ToList();
                if (cands.Count > 0)
                {
                    // Rezerv dururken garanti GÖNÜLLÜ bozulmaz. Kaçış pahalı olsa bile
                    // yükü öne sür: %40'lık istemsiz kazanma riski, %100'lük garantiyi
                    // erken yakıp sonda mecburi fazla el almaktan her zaman iyidir.
                    double minD = cands.Min(c => pw[c]);
                    var dmp = cands.Where(c => pw[c] <= minD + dumpBandOf(surplus))
                                   .OrderByDescending(c => RiskValue(c, trump)).First();
                    reason = minD < 0.25
                        ? $"koz garantim cepte ({need} el), bu eli pas geçip yük atıyorum (%{pw[dmp] * 100:F0})"
                        : $"garanti sona saklı, bombayı ateşe sürüyorum (%{pw[dmp] * 100:F0} istemsiz alma riski)";
                    return dmp;
                }
                // Elde yalnız rezerv kaldı: garantiden oyna (mecburen kazanır, ihtiyaç düşer)
                var cashR = legal.OrderBy(c => c.Rank).First();
                reason = $"elde yalnız garantiler kaldı, bozduruyorum (ihtiyaç {need})";
                return cashR;
            }

            // Koz yönetimi (açışta): elim tamamen kozsa büyükten gitmek rakip kozlarını eritir,
            // küçük kozum sona istemsiz kazanan kalır. İhtiyaç < kart sayısıysa kaybedilecek eli
            // BAŞTAN kaybet: en küçük kozu sür. İhtiyaç = kart sayısıysa süpür: tepeden in.
            if (need > 0 && leading && trump.HasValue && hand.All(c => c.Suit == trump.Value))
            {
                if (need >= hand.Count)
                {
                    var sweep = legal.OrderByDescending(c => c.Rank).First();
                    reason = $"hepsi lazım, tepeden süpürüyorum (ihtiyaç {need}/{hand.Count})";
                    return sweep;
                }
                var low = legal.OrderBy(c => c.Rank).First();
                reason = $"fazla eli erkenden kaybetmeye çalışıyorum — küçük kozu sürdüm (%{pw[low] * 100:F0} yine de alır)";
                return low;
            }

            if (need > 0)
            {
                double best = pw.Values.Max();
                // El kıtlığında bekleme lüksü yok: eşiği düşür, fırsatı erken yakala
                double confidence = surplus < 0 ? 0.48 : 0.55;

                if (best >= confidence)
                {
                    // Yüksek şanslı bandın içinden: önce ERİYEN kazananlar (yan renk), sağlamlar sona;
                    // eşitse en ucuzu.
                    var band = legal.Where(c => pw[c] >= best - 0.08)
                                    .OrderBy(c => IsStableBank(c) ? 1 : 0)
                                    .ThenBy(c => c.Rank).First();
                    reason = IsStableBank(band)
                        ? $"%{pw[band] * 100:F0} alır (ihtiyaç {need})"
                        : $"%{pw[band] * 100:F0} alır — eriyen kazananı erken bozduruyorum (ihtiyaç {need})";
                    return band;
                }
                if (!leading && best > 0 && best >= 0.30)
                {
                    var pick = legal.OrderByDescending(c => pw[c]).First();
                    reason = $"riskli ama deniyorum: %{pw[pick] * 100:F0} (ihtiyaç {need})";
                    return pick;
                }
                var save = legal.OrderBy(c => pw[c]).ThenBy(c => c.Rank).First();
                reason = leading
                    ? $"elimde güvenli açış yok (en iyi %{best * 100:F0}), küçükle açıyorum"
                    : $"bu el alınmaz (en iyi %{best * 100:F0}), küçüğü verdim";
                return save;
            }

            // İhtiyaç yok / batmış: alma olasılığını MİNİMİZE et; eşitler arasında en riskli yükü at.
            // Masada fazla el varsa tehlike büyük (sahipsiz eller dolaşıyor) → boşaltma bandını genişlet,
            // büyük kartları eritmeye daha erken davran.
            double min = pw.Values.Min();
            var dumps = legal.Where(c => pw[c] <= min + dumpBandOf(surplus)).ToList();
            if (min >= 0.65)
            {
                // Kaçış yok, mecburen alıyorum: en büyüğü yak
                var burn = legal.OrderByDescending(c => c.Rank).First();
                reason = $"kaçamıyorum (%{pw[burn] * 100:F0} alır), en büyüğü yaktım";
                return burn;
            }
            var dump = dumps.OrderByDescending(c => RiskValue(c, trump)).First();
            string ctx = surplus > 0 ? $" (masada {surplus} sahipsiz el var, acele ediyorum)" : "";
            reason = need == 0
                ? $"ihalem doldu, %{pw[dump] * 100:F0} riskle yük boşaltıyorum{ctx}"
                : $"battım, yük boşaltıyorum (%{pw[dump] * 100:F0}){ctx}";
            return dump;
        }

        private static double dumpBandOf(int surplus) => surplus > 0 ? 0.13 : 0.05;

        private static int RiskValue(Card c, Suit? trump)
        {
            int v = (int)c.Rank * 2;
            if (trump.HasValue && c.Suit == trump.Value) v -= 3;
            return v;
        }
    }
}