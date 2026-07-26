using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// BALANCED KURAL KİTABI — amaç-olasılık mimarisi. Plan zamanlamayla İCRA edilir.
    ///  B1  Açış: eriyen Winner bekletilmez (deterministik).
    ///  B2  Açış: kaybedilecek eli baştan kaybet — KAYBET amacı, P(almaz) en yüksek (koz hariç).
    ///  B3  Açış: elde yalnız Winner → tepeden.
    ///  B4  Takip: güvenli Winner — AL amacı; B4b bedava Swing (takaslık Winner şartıyla).
    ///  B5  Takip, planda yok: ERİT — altta kalanlardan gelecekte en tehlikelisi.
    ///  B6  Koz mecburi: alıyorsam SÖKÜLMEZİ harca (tehlike puanı en yüksek); üstüm varsa erit.
    ///  B7  Serbest atış: en tehlikeli Loser.
    /// </summary>
    public static class BalancedBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, HandPlan plan, SkillTier tier, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "B0: tek geçerli kart"; return legal[0]; }

            List<(Card, double)> Score(IEnumerable<Card> set, Func<Card, double> goal)
                => set.Select(c => (c, goal(c))).ToList();
            Func<Card, double> winNow = c => GoalPicker.WinNow(c, hand, trick, trump, mem, seat);
            Func<Card, double> danger = c => GoalPicker.FutureDanger(c, hand, trump, mem);

            bool IsBossSafe(Card c)
            {
                if (!mem.IsBoss(c, hand)) return false;
                if (!trump.HasValue || c.Suit == trump.Value) return true;
                for (int s = 0; s < 4; s++) if (mem.IsVoid(s, c.Suit)) return false;
                return true;
            }

            // ---- Açış ----
            if (trick.Cards.Count == 0)
            {
                var decaying = legal.Where(c => plan.RoleOf(c) == CardRole.Winner &&
                                                trump.HasValue && c.Suit != trump.Value).ToList();
                if (decaying.Count > 0)
                {
                    var pick = decaying.OrderByDescending(c => c.Rank).First();
                    reason = "B1: eriyen Winner bekletilmez — bozduruyorum";
                    return pick;
                }
                if (hand.All(c => plan.RoleOf(c) == CardRole.Winner))
                {
                    var top = legal.OrderByDescending(c => c.Rank).First();
                    reason = "B3: elde yalnız Winner — tepeden kapatıyorum";
                    return top;
                }
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var pool = losers.Count > 0 ? losers
                         : legal.Where(c => plan.RoleOf(c) == CardRole.Swing).ToList();
                // Koz kayıp yakıtı olmaz: yan aday varsa kozlar havuzdan çıkar
                if (trump.HasValue && pool.Any(c => c.Suit != trump.Value))
                    pool = pool.Where(c => c.Suit != trump.Value).ToList();
                var lead = GoalPicker.Pick(Score(pool, c => GoalPicker.LoseNow(c, hand, trick, trump, mem, seat)),
                                           tier, rng, out double p, out string bn);
                reason = $"B2: kaybı baştan yaşıyorum (%{p * 100:F0} almaz, {bn})";
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
                    // Mecburen alıyorum → sökülmezi harca: tehlike puanı EN YÜKSEK kazanan
                    var pick = GoalPicker.Pick(Score(winning, danger), tier, rng, out double p, out string bn);
                    reason = $"B6: koz mecburi — sökülmezle alıyorum, yedirilebilir kalsın ({bn})";
                    return pick;
                }
                var sac = GoalPicker.Pick(Score(losing, danger), tier, rng, out double p2, out string bn2);
                reason = $"B6: koz mecburi ama üstüm var — tehlikeli kozu eritiyorum ({bn2})";
                return sac;
            }

            if (freeDiscard)
            {
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                var pool = losers.Count > 0 ? losers : legal;
                if (losers.Count == 0)
                {
                    var weak = GoalPicker.Pick(Score(legal, c => 1.0 - GoalPicker.FutureDanger(c, hand, trump, mem)),
                                               tier, rng, out double pw, out string bnw);
                    reason = $"B7: Loser yok — en zayıf gidiyor ({bnw})";
                    return weak;
                }
                var dump = GoalPicker.Pick(Score(pool, danger), tier, rng, out double p3, out string bn3);
                reason = $"B7: en tehlikeli Loser'ı boşaltıyorum (%{p3 * 100:F0}, {bn3})";
                return dump;
            }

            if (winning.Count > 0)
            {
                var winners = winning.Where(c => plan.RoleOf(c) == CardRole.Winner)
                                     .Where(c => playersAfter == 0 || IsBossSafe(c)).ToList();
                if (winners.Count > 0)
                {
                    // Harcama sırası: en çok ERİYECEK Winner önce (dayanıklı koz boss'u bekler)
                    var pick = GoalPicker.Pick(
                        Score(winners, c => GoalPicker.DecayRisk(c, hand, trump, mem)),
                        tier, rng, out double p, out string bn);
                    reason = $"B4: planlı el — eriyen Winner'la alıyorum ({bn})";
                    return pick;
                }
                if (playersAfter == 0 && plan.Winners >= 1)
                {
                    var swings = winning.Where(c => plan.RoleOf(c) == CardRole.Swing).ToList();
                    if (swings.Count > 0)
                    {
                        var pick = swings.OrderBy(c => c.Rank).First();
                        reason = "B4b: bedava Swing eli — Winner'ı Swing'e çevirip kadroyu güçlendiriyorum";
                        return pick;
                    }
                }
            }

            if (losing.Count > 0)
            {
                var pick = GoalPicker.Pick(Score(losing, danger), tier, rng, out double p4, out string bn4);
                reason = $"B5: planda yok — altına kaçıp tehlikeliyi eritiyorum (%{p4 * 100:F0}, {bn4})";
                return pick;
            }

            var forced = GoalPicker.Pick(Score(winning, danger), tier, rng, out double p5, out string bn5);
            reason = $"B5b: kaçış yok — sökülmezle alıyorum, dengeleme emecek ({bn5})";
            return forced;
        }
    }
}
