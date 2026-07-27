using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// HUNTING KURAL KİTABI — amaç-olasılık mimarisi. El ÜRETİLECEK.
    ///  H1  Açış, boss var: eriyen boss önce, koz boss sona (deterministik — plan gereği).
    ///  H2  Açış, boss yok: renklerin tepeleri aday → AL amacı, P(kazanır) en yüksek.
    ///  H3/H4 Takip, kazanabiliyorum: AL amacı → arkada oyuncu varken P(sökülmez) yüksek;
    ///        son sözde israf etme → gelecek değeri EN DÜŞÜK kazananla al.
    ///  H5  Kazanamıyorum: sermaye koru → gelecek değeri en düşük kartı ver.
    ///  H6  Koz mecburi: kazanıyorsam israfsız kazan; üstüm varsa en değersizi feda.
    ///  H7  Serbest atış: en değersiz Loser (Swing/Winner korunur).
    ///  H8  Son sıkışma: fren yok — P(kazanır) en yüksek.
    /// </summary>
    public static class HuntingBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, HandPlan plan, SkillTier tier, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "H0: tek geçerli kart"; return legal[0]; }

            int need = plan.TargetTricks - mem.TricksWon[seat];
            List<(Card, double)> Score(IEnumerable<Card> set, Func<Card, double> goal)
                => set.Select(c => (c, goal(c))).ToList();
            Func<Card, double> winNow = c => GoalPicker.WinNow(c, hand, trick, trump, mem, seat);
            Func<Card, double> keepValue = c => 1.0 - GoalPicker.FutureDanger(c, hand, trump, mem);

            // ---- H8: her el şart ----
            if (need >= hand.Count)
            {
                // Eşitlikte (özellikle hepsi P=0: bu el alınamaz) EN UCUZ kart harcanır — sermaye yanmaz
                var pick = GoalPicker.Pick(Score(legal, winNow), tier, rng, out double p, out string bn, keepValue);
                reason = $"H8: her el şart ({need}/{hand.Count}) — %{p * 100:F0} alır ({bn})";
                return pick;
            }

            // ---- Açış ----
            if (trick.Cards.Count == 0)
            {
                var bosses = legal.Where(c => mem.IsBoss(c, hand)).ToList();
                if (bosses.Count > 0)
                {
                    var pick = bosses.OrderBy(c => trump.HasValue && c.Suit == trump.Value ? 1 : 0)
                                     .ThenByDescending(c => c.Rank).First();
                    reason = trump.HasValue && pick.Suit != trump.Value
                        ? "H1: eriyen boss'u çakılmadan bozduruyorum" : "H1: koz boss'la açıyorum";
                    return pick;
                }
                var tops = legal.GroupBy(c => c.Suit)
                                .Select(g => g.OrderByDescending(c => c.Rank).First()).ToList();
                var lead = GoalPicker.Pick(Score(tops, winNow), tier, rng, out double p2, out string bn2, keepValue);
                reason = $"H2: boss yok — zorlama açış (%{p2 * 100:F0} alır, {bn2})";
                return lead;
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
                if (winning.Count > 0)
                {
                    var pick = GoalPicker.Pick(Score(winning, keepValue), tier, rng, out double p, out string bn);
                    reason = $"H6: koz mecburi, fırsat — israfsız çakıyorum ({bn})";
                    return pick;
                }
                var sac = GoalPicker.Pick(Score(legal, keepValue), tier, rng, out double p3, out string bn3);
                reason = $"H6: koz mecburi ama üstüm var — en değersizi feda ({bn3})";
                return sac;
            }

            if (freeDiscard)
            {
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var pool = losers.Count > 0 ? losers : legal;
                var dump = GoalPicker.Pick(Score(pool, keepValue), tier, rng, out double p4, out string bn4);
                reason = losers.Count > 0 ? $"H7: en değersiz Loser gidiyor ({bn4})"
                                          : $"H7: mecburen sermayeden en ucuzu ({bn4})";
                return dump;
            }

            if (winning.Count > 0)
            {
                if (playersAfter == 0)
                {
                    var pick = GoalPicker.Pick(Score(winning, keepValue), tier, rng, out double p, out string bn);
                    reason = $"H3: son söz bende — israfsız alıyorum (ihtiyaç {need}, {bn})";
                    return pick;
                }
                var press = GoalPicker.Pick(Score(winning, winNow), tier, rng, out double p5, out string bn5, keepValue);
                reason = $"H4: arkada {playersAfter} kişi — %{p5 * 100:F0} sağ çıkar ({bn5})";
                return press;
            }

            var save = GoalPicker.Pick(Score(losing, keepValue), tier, rng, out double p6, out string bn6);
            reason = $"H5: bu el alınmaz — sermayeyi saklıyorum ({bn6})";
            return save;
        }
    }
}
