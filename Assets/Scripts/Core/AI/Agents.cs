using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bien.Core.AI
{
    /// <summary>
    /// Üç zorluk da aynı iskeleti kullanır; farkları parametrik:
    ///   Attention  — kart sayma kalitesi (Easy .20 / Normal .50 / Hard 1.0)
    ///   BidNoise   — ihale tahminine eklenen gürültü genliği
    ///   Optimism   — yuvarlama eğilimi (+: fazla söyler, riskli)
    ///   ReadsBids  — Hard: diğer ihalelerden masa yoğunluğunu okur
    ///   SloppyPlay — bu oranda plansız rastgele hamle (Easy'nin dağınıklığı)
    /// </summary>
    public class TableAgent : IPlayerAgent, IGameObserver
    {
        /// <summary>Debug: her ihale/hamle gerekçesi buraya yazılır (null ise maliyet yok).</summary>
        public Action<string> Debug;

        protected readonly Random Rng;
        protected readonly GameMemory Mem = new();
        private readonly double _bidNoise, _optimism, _sloppyPlay;
        private readonly bool _readsBids;

        public TableAgent(Random rng, double attention, double bidNoise, double optimism,
                          bool readsBids, double sloppyPlay)
        {
            Rng = rng;
            Mem.Attention = attention;
            _bidNoise = bidNoise; _optimism = optimism;
            _readsBids = readsBids; _sloppyPlay = sloppyPlay;
        }

        // ---- IGameObserver ----
        public void OnRoundStarted(RoundConfig rc, int d) => Mem.OnRoundStarted(rc, d);
        public void OnTrumpRevealed(Card? tc) => Mem.OnTrumpRevealed(tc);
        public void OnBidMade(int s, int b) => Mem.OnBidMade(s, b);
        public void OnCardPlayed(int s, Card c) => Mem.OnCardPlayed(s, c);
        public void OnTrickWon(int w) => Mem.OnTrickWon(w);

        // ---- İhale: tablo bazlı ----
        public Task<int> MakeBidAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                      IReadOnlyList<int?> bidsSoFar, int? forbidden)
        {
            int n = round.CardsPerPlayer;
            int myTL = trump.HasValue ? hand.Count(c => c.Suit == trump.Value) : 0;
            var ps = hand.Select(c => BidTable.CardP(c, trump, n, myTL)).ToArray();

            // Masa okuma (Hard): ihaleler adil paydan sapıyorsa ORTADA kartların p'sini eğ
            double pressureAdj = 0;
            if (_readsBids)
            {
                var others = bidsSoFar.Where(b => b.HasValue).Select(b => b.Value).ToList();
                if (others.Count > 0)
                {
                    double fairShare = n * others.Count / 4.0;
                    pressureAdj = -(others.Sum() - fairShare) / Math.Max(1.0, n) * 0.35;
                    for (int i = 0; i < ps.Length; i++)
                        if (ps[i] > 0.15 && ps[i] < 0.85)
                            ps[i] = Math.Clamp(ps[i] + pressureAdj, 0.02, 0.98);
                }
            }
            // İyimserlik: tüm p'lere hafif eğim (Easy fazla umutlu, Hard temkinli)
            if (_optimism != 0)
                for (int i = 0; i < ps.Length; i++)
                    ps[i] = Math.Clamp(ps[i] + _optimism * 0.10, 0.0, 1.0);

            // Poisson-binomial: P(k el alırım) dağılımı
            // (dist[0] her kartta güncellenmeli — sonda toplu çarpmak dağılımı bozar!)
            var dist = new double[n + 1];
            dist[0] = 1;
            foreach (var p in ps)
            {
                for (int k = Math.Min(n, hand.Count); k >= 1; k--)
                    dist[k] = dist[k] * (1 - p) + dist[k - 1] * p;
                dist[0] *= (1 - p);
            }

            // EV maksimizasyonu: argmax P(b) × (b² + 10), yasak hariç
            int bid = 0; double bestEv = double.MinValue;
            var evDbg = new List<string>();
            for (int b = 0; b <= n; b++)
            {
                double ev = dist[b] * (b * b + ScoreEngine.MakeBonus);
                if (b <= Math.Min(n, hand.Count) && dist[b] > 0.01)
                    evDbg.Add($"P({b})=%{dist[b] * 100:F0}→{ev:F1}");
                if (forbidden.HasValue && b == forbidden.Value) continue;
                if (ev > bestEv) { bestEv = ev; bid = b; }
            }

            // Mizaç (Easy): gürültü oranında ±1 sapma
            if (_bidNoise > 0 && Rng.NextDouble() < _bidNoise * 0.35)
            {
                int alt = bid + (Rng.NextDouble() < 0.5 ? -1 : 1);
                if (alt >= 0 && alt <= n && (!forbidden.HasValue || alt != forbidden.Value)) bid = alt;
            }

            if (Debug != null)
            {
                var parts = hand.OrderByDescending(c => BidTable.CardP(c, trump, n, myTL))
                    .Select(c => $"{c} {BidTable.CardP(c, trump, n, myTL):F2}");
                string msg = $"İHALE {bid} ← {string.Join(" ", parts)} | {string.Join(", ", evDbg.Take(4))}";
                if (pressureAdj != 0) msg += $" | masa {pressureAdj:+0.00;-0.00}";
                if (forbidden.HasValue) msg += $" | yasak: {forbidden.Value}";
                Debug(msg);
            }
            return Task.FromResult(bid);
        }

        public Task<int?> OfferBidRevisionAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                                IReadOnlyList<int?> currentBids, int dealerDesiredBid)
        {
            // Öz çıkar: tahmin mevcut ihaleden belirgin sapıyorsa oynat, değilse karışma
            double est = BidTable.ExpectedTricks(hand, trump, round.CardsPerPlayer);
            int cur = currentBids[seat] ?? 0;
            int alt = est > cur ? cur + 1 : cur - 1;
            if (Math.Abs(est - cur) >= 0.7 && alt >= 0 && alt <= round.CardsPerPlayer)
                return Task.FromResult<int?>(alt);
            return Task.FromResult<int?>(null);
        }

        // ---- Oyun ----
        public Task<Card> PlayCardAsync(int seat, IReadOnlyList<Card> hand, TrickState trick, RoundConfig round, Suit? trump)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count > 1 && Rng.NextDouble() < _sloppyPlay)
            {
                var slip = legal[Rng.Next(legal.Count)];
                Debug?.Invoke($"{slip} — dalgınlık, plansız attım");
                return Task.FromResult(slip);
            }
            var card = Policy.Decide(seat, hand, trick, trump, Mem, Rng, out string reason);
            Debug?.Invoke($"{card} — {reason}");
            return Task.FromResult(card);
        }
    }

    public sealed class EasyAgent : TableAgent
    {
        public EasyAgent(Random rng)
            : base(rng, attention: 0.20, bidNoise: 1.1, optimism: +0.35, readsBids: false, sloppyPlay: 0.35) { }
    }

    public sealed class NormalAgent : TableAgent
    {
        public NormalAgent(Random rng)
            : base(rng, attention: 0.50, bidNoise: 0.35, optimism: 0.0, readsBids: false, sloppyPlay: 0.06) { }
    }

    public sealed class HardAgent : TableAgent
    {
        public HardAgent(Random rng)
            : base(rng, attention: 1.00, bidNoise: 0.0, optimism: -0.10, readsBids: true, sloppyPlay: 0.0) { }
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