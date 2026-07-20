using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// HUNTING KURAL KİTABI — yaşayan liste. İhtiyaç ateş gücünü aşıyor: el ÜRETİLECEK.
    ///
    /// v1 (2026-07):
    ///  H1  Açış, boss var: boss'la aç — ERİYEN önce (yan renk boss'u çakılmadan bozdur,
    ///      koz boss dayanıklı, sona kalabilir).
    ///  H2  Açış, boss yok: rakiplerin boş olmadığı en uzun rengin tepesinden zorla.
    ///  H3  Takip, kazanıyorum, son oyuncuyum: en ucuz kazanan.
    ///  H4  Takip, kazanıyorum, arkada oyuncu var: en ucuz SÖKÜLMEZ kazanan; yoksa en büyükle bastır.
    ///  H5  Takip, kazanamıyorum: en küçüğü ver (Swing'ler sermaye).
    ///  H6  Koz mecburi: kazanıyorsam en ucuz kazanan koz; üstüm varsa en küçük kozu feda.
    ///  H7  Serbest atış: en değersiz Loser; Swing/Winner'a dokunma.
    ///  H8  Son sıkışma (kalan el = ihtiyaç): fren yok — en yüksek kazanma şanslı kart.
    /// </summary>
    public static class HuntingBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, HandPlan plan, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "H0: tek geçerli kart"; return legal[0]; }

            int need = plan.TargetTricks - mem.TricksWon[seat];

            // ---- H8: son sıkışma — her el şart ----
            if (need >= hand.Count)
            {
                var pick = legal.OrderByDescending(c => Prob.WinProbability(c, hand, trick, trump, mem, seat)).First();
                reason = $"H8: her el şart ({need}/{hand.Count}), en yüksek şans %{Prob.WinProbability(pick, hand, trick, trump, mem, seat) * 100:F0}";
                return pick;
            }

            bool IsBossSafe(Card c) // sökülmez: koz boss'u; sans'ta boss; yan boss'u rakip boşluğu yoksa
            {
                if (!mem.IsBoss(c, hand)) return false;
                if (!trump.HasValue || c.Suit == trump.Value) return true;
                for (int s = 0; s < 4; s++) if (mem.IsVoid(s, c.Suit)) return false;
                return true;
            }

            // ---- Açış ----
            if (trick.Cards.Count == 0)
            {
                var bosses = legal.Where(c => mem.IsBoss(c, hand)).ToList();
                if (bosses.Count > 0)
                {
                    // H1: eriyen (yan renk) boss önce; koz boss'ları dayanıklı, en son
                    var pick = bosses.OrderBy(c => trump.HasValue && c.Suit == trump.Value ? 1 : 0)
                                     .ThenByDescending(c => c.Rank).First();
                    bool decaying = trump.HasValue && pick.Suit != trump.Value;
                    reason = decaying ? "H1: eriyen boss'u çakılmadan bozduruyorum"
                                      : "H1: koz boss'la açıyorum";
                    return pick;
                }
                // H2: rakip boşluğu olmayan en uzun rengin tepesi
                var bySuit = legal.GroupBy(c => c.Suit)
                                  .OrderBy(g => Enumerable.Range(0, 4).Any(s => mem.IsVoid(s, g.Key)) ? 1 : 0)
                                  .ThenByDescending(g => g.Count());
                var top = bySuit.First().OrderByDescending(c => c.Rank).First();
                reason = "H2: boss yok — uzun rengin tepesiyle zorluyorum";
                return top;
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
                // ---- H6 ----
                if (winning.Count > 0)
                {
                    var pick = winning.OrderBy(c => c.Rank).First();
                    reason = "H6: koz mecburi ve fırsat — en ucuz kazanan kozla çakıyorum";
                    return pick;
                }
                var sac = legal.OrderBy(c => c.Rank).First();
                reason = "H6: koz mecburi ama üstüm var — en küçüğü feda";
                return sac;
            }

            if (freeDiscard)
            {
                // ---- H7: Loser'lardan en değersizi; Swing/Winner korunur ----
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var pool = losers.Count > 0 ? losers : legal;
                // Koz en son harcanır: küçük kozun çakma potansiyeli var, yan çöp önce gider
                var dump = pool.OrderBy(c => trump.HasValue && c.Suit == trump.Value ? 1 : 0)
                               .ThenBy(c => TableAgent.CardPoints(c, trump, mem.Round.CardsPerPlayer))
                               .ThenBy(c => c.Rank).First();
                reason = losers.Count > 0 ? "H7: en değersiz Loser'ı atıyorum (sermaye korunur)"
                                          : "H7: mecburen sermayeden — en ucuzunu";
                return dump;
            }

            // ---- Renk takibi ----
            if (winning.Count > 0)
            {
                if (playersAfter == 0)
                {
                    var pick = winning.OrderBy(c => c.Rank).First();
                    reason = $"H3: son söz bende — en ucuz kazanan (ihtiyaç {need})";
                    return pick;
                }
                var sure = winning.Where(IsBossSafe).ToList();
                if (sure.Count > 0)
                {
                    var pick = sure.OrderBy(c => c.Rank).First();
                    reason = $"H4: en ucuz sökülmez kazanan (arkada {playersAfter} kişi)";
                    return pick;
                }
                var press = winning.OrderByDescending(c => c.Rank).First();
                reason = $"H4: sökülmez yok — en büyükle bastırıyorum";
                return press;
            }

            var save = losing.OrderBy(c => c.Rank).First();
            reason = "H5: bu el alınmaz — en küçüğü verip sermayeyi saklıyorum";
            return save;
        }
    }
}