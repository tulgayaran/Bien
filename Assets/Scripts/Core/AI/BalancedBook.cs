using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// BALANCED KURAL KİTABI — yaşayan liste. Plan hedefte (f ≈ ihtiyaç) veya ateş gücü
    /// fazlasıyla temkin: el ÜRETİLMEZ (Hunting işi), el KAÇILMAZ (Ducking işi) —
    /// planlı Winner'lar zamanında bozdurulur, Swing'ler fırsata saklanır, Loser'lar eritilir.
    ///
    /// v1 (2026-07):
    ///  B1  Açış, boss var: eriyen boss önce bozdurulur (H1 ile aynı — plan alacağını zamanında alır).
    ///  B2  Açış, boss yok: en tehlikesiz Loser ile aç (zorlamak Hunting işi); Loser yoksa en tehlikesiz kart.
    ///  B3a Takip, kazanıyorum, son oyuncuyum: en ucuz kazanan.
    ///  B3b Takip, kazanıyorum, arkada oyuncu var: en ucuz SÖKÜLMEZ kazanan.
    ///  B3c Sökülmez yok: fırsatçılık eşiği — en iyi kazanan %55+ alıyorsa oyna, altındaysa zorlamaz → B4.
    ///  B4  Takip, almıyorum: altta kalan Loser'ların EN BÜYÜĞÜNÜ erit (D2 mantığı);
    ///      altta Loser yoksa en küçük altta kalanı ver (Swing sermayesi korunur).
    ///  B5a Koz mecburi, kazanabiliyorum: en ucuz kazanan koz (el plana yazılır).
    ///  B5b Koz mecburi, üstüm var: altta kalan kozların en büyüğünü erit.
    ///  B6  Serbest atış: en tehlikeli Loser'ı boşalt (D5 mantığı); Loser yoksa en ucuz Swing.
    ///  B7  Eş-tehlike kırılımı: kısa renkten olanı (D6 — renk boşalt, serbest atış hakkı kazan).
    /// </summary>
    public static class BalancedBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, HandPlan plan, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "B0: tek geçerli kart"; return legal[0]; }

            int need = plan.TargetTricks - mem.TricksWon[seat];

            double Danger(Card c) // DuckingBook ile aynı ölçü
            {
                double d = (int)c.Rank / 14.0;
                if (mem.IsBoss(c, hand)) d += 0.60;
                if (trump.HasValue && c.Suit == trump.Value) d += 0.35;
                return d;
            }
            int SuitLen(Card c) => hand.Count(h => h.Suit == c.Suit);

            bool IsBossSafe(Card c) // sökülmez: koz/sans boss'u; yan boss'u rakip boşluğu yoksa
            {
                if (!mem.IsBoss(c, hand)) return false;
                if (!trump.HasValue || c.Suit == trump.Value) return true;
                for (int s = 0; s < 4; s++) if (mem.IsVoid(s, c.Suit)) return false;
                return true;
            }

            // ---- B8: garanti rezervi — koz boss'larım ihtiyacı kapatıyorsa el ALINMAZ, yük atılır ----
            bool IsStableBank(Card c) => trump.HasValue && c.Suit == trump.Value && mem.IsBoss(c, hand);
            int stableBank = trump.HasValue ? hand.Count(IsStableBank) : 0;
            bool reserveMode = need > 0 && stableBank >= need;
            if (reserveMode)
            {
                var reserved = hand.Where(IsStableBank).OrderByDescending(c => c.Rank)
                                   .Take(need).ToHashSet();
                var cands = legal.Where(c => !reserved.Contains(c)).ToList();
                if (cands.Count == 0)
                {
                    var cash = legal.OrderBy(c => c.Rank).First();
                    reason = $"B8: elde yalnız garantiler kaldı, bozduruyorum (ihtiyaç {need})";
                    return cash;
                }
                // Garanti sona saklı (Policy mantığı): düşük alma-olasılıklı bandın
                // içinden en riskli yükü öne sür — %40 istemsiz alma riski, %100 garantiyi
                // erken yakmaktan iyidir.
                var pwC = cands.ToDictionary(c => c, c => Prob.WinProbability(c, hand, trick, trump, mem, seat));
                double minD = pwC.Values.Min();
                var burn = cands.Where(c => pwC[c] <= minD + 0.13)
                                .OrderByDescending(Danger).ThenBy(SuitLen).ThenByDescending(c => c.Rank).First();
                reason = $"B8: koz garantim cepte ({stableBank}/{need}), bu eli pas geçip yük atıyorum (%{pwC[burn] * 100:F0})";
                return burn;
            }

            // ---- Açış ----
            if (trick.Cards.Count == 0)
            {
                // B9: elim tamamen koz — ihtiyaç < kart: kaybedilecek eli BAŞTAN kaybet (küçük koz);
                //     ihtiyaç ≥ kart: tepeden süpür.
                if (need > 0 && trump.HasValue && hand.All(c => c.Suit == trump.Value))
                {
                    if (need >= hand.Count)
                    {
                        var sweep = legal.OrderByDescending(c => c.Rank).First();
                        reason = $"B9: hepsi lazım, tepeden süpürüyorum ({need}/{hand.Count})";
                        return sweep;
                    }
                    var lowT = legal.OrderBy(c => c.Rank).First();
                    reason = "B9: elim tamamen koz — fazla eli erkenden kaybediyorum (küçük koz)";
                    return lowT;
                }
                var bosses = legal.Where(c => mem.IsBoss(c, hand)).ToList();
                if (bosses.Count > 0 && need > 0)
                {
                    // B1: eriyen (yan renk) boss önce; koz boss dayanıklı, sona
                    var pick = bosses.OrderBy(c => trump.HasValue && c.Suit == trump.Value ? 1 : 0)
                                     .ThenByDescending(c => c.Rank).First();
                    bool decaying = trump.HasValue && pick.Suit != trump.Value;
                    reason = decaying ? "B1: eriyen boss'u zamanında bozduruyorum"
                                      : "B1: koz boss'la açıyorum (plan alacağı)";
                    return pick;
                }
                // B2: en tehlikesiz Loser ile aç (+B7 kırılımı)
                var losersLead = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var poolLead = losersLead.Count > 0 ? losersLead : legal;
                var open = poolLead.OrderBy(Danger).ThenBy(SuitLen).ThenBy(c => c.Rank).First();
                reason = losersLead.Count > 0 ? "B2: en tehlikesiz Loser ile açış (zorlamıyorum)"
                                              : "B2: Loser yok — en tehlikesiz kartla açış";
                return open;
            }

            var ledSuit = trick.Cards[0].Suit;
            int playersAfter = 3 - trick.Cards.Count;
            bool WinsNow(Card c)
            {
                var t = new List<Card>(trick.Cards) { c };
                return GameRules.TrickWinnerOffset(t, ledSuit, trump) == t.Count - 1;
            }
            var winning = legal.Where(WinsNow).ToList();
            var losing = legal.Where(c => !WinsNow(c)).ToList();

            bool forcedTrump = trump.HasValue && !hand.Any(h => h.Suit == ledSuit)
                                              && legal.All(c => c.Suit == trump.Value);
            bool freeDiscard = !forcedTrump && legal[0].Suit != ledSuit;

            if (forcedTrump)
            {
                if (winning.Count > 0 && need > 0)
                {
                    var pick = winning.OrderBy(c => c.Rank).First();
                    reason = "B5a: koz mecburi ve fırsat — en ucuz kazanan kozla alıyorum";
                    return pick;
                }
                if (losing.Count > 0)
                {
                    var keepT = losing.OrderBy(c => c.Rank).First();
                    reason = "B5b: koz mecburi ama üstüm var — en küçüğü feda (koz sermayesi korunur)";
                    return keepT;
                }
                var small = legal.OrderBy(c => c.Rank).First();
                reason = "B5a: koz mecburi, mecburen alıyorum — en küçüğüyle";
                return small;
            }

            if (freeDiscard)
            {
                // B6 (+B7): en tehlikeli Loser'ı boşalt; Loser yoksa en ucuz Swing
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var pool = losers.Count > 0 ? losers : legal;
                var dump = pool.OrderBy(c => TableAgent.CardPoints(c, trump, mem.Round.CardsPerPlayer))
                               .ThenBy(c => c.Rank).First();
                reason = losers.Count > 0 ? "B6: serbest atış — en değersiz Loser (büyükler ileride terfi edebilir)"
                                          : "B6: Loser yok — en ucuz karttan";
                return dump;
            }

            // ---- Renk takibi ----
            if (winning.Count > 0 && need > 0)
            {
                if (playersAfter == 0)
                {
                    var pick = winning.OrderBy(c => c.Rank).First();
                    reason = $"B3a: son söz bende — en ucuz kazanan (ihtiyaç {need})";
                    return pick;
                }
                var sure = winning.Where(IsBossSafe).ToList();
                if (sure.Count > 0)
                {
                    var pick = sure.OrderBy(c => IsStableBank(c) ? 1 : 0).ThenBy(c => c.Rank).First();
                    reason = IsStableBank(pick)
                        ? $"B3b: en ucuz sökülmez kazanan (arkada {playersAfter} kişi)"
                        : $"B3b: eriyen sökülmezi önce bozduruyorum (arkada {playersAfter} kişi)";
                    return pick;
                }
                // B3c: fırsatçılık eşiği — zorlamak Hunting işi
                var best = winning.OrderByDescending(c => Prob.WinProbability(c, hand, trick, trump, mem, seat)).First();
                double p = Prob.WinProbability(best, hand, trick, trump, mem, seat);
                // El kıtlığında bekleme lüksü yok: eşik düşer (Policy adaptasyonu)
                int surplus = mem.Round.CardsPerPlayer - (mem.Bids[0] + mem.Bids[1] + mem.Bids[2] + mem.Bids[3]);
                double conf = surplus < 0 ? 0.48 : 0.55;
                if (p >= conf)
                {
                    reason = $"B3c: fırsat yeterli (%{p * 100:F0}, eşik %{conf * 100:F0}) — deniyorum";
                    return best;
                }
                // fırsat zayıf → boşaltmaya düş (B4)
            }

            // B4: almıyorum/almam gerekmiyor — altta kalan Loser'ların en büyüğü; yoksa en küçük
            if (losing.Count > 0)
            {
                var keep = losing.OrderBy(c => c.Rank).First();
                reason = "B4: bu el alınmaz — en küçüğü verip sermayeyi koruyorum";
                return keep;
            }

            // Her kartım alıyor ama ihtiyacım yok/tercihim değil: en ucuz kazananla al
            var forced = legal.OrderBy(c => c.Rank).First();
            reason = "B4: kaçış yok — en ucuzla alıyorum";
            return forced;
        }
    }
}
