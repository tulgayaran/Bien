using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// DUCKING KURAL KİTABI — amaç-olasılık mimarisi (Tulga şeması).
    /// Her kural adayları + AMACI tanımlar; GoalPicker olasılıkla puanlar, zorluk bandından seçer.
    ///  D1  Açış: amaç KAYBET → P(almaz) en yüksek.
    ///  D2  Takip, altta kalabiliyorum: amaç ERİT → altta kalanlardan gelecekte en tehlikelisi.
    ///  D3  Takip, mecburen alıyorum: amaç ERİT → en tehlikeli büyüğü yak.
    ///  D4a Koz mecburi, altta: ERİT → tehlikeli kozu erit.  D4b Koz mecburi, alıyorum: ERİT → büyüğü yak.
    ///  D5  Serbest atış: ERİT → gelecekte en tehlikeli yük.
    /// </summary>
    public static class DuckingBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, SkillTier tier, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "D0: tek geçerli kart"; return legal[0]; }

            List<(Card, double)> Score(IEnumerable<Card> set, Func<Card, double> goal)
                => set.Select(c => (c, goal(c))).ToList();

            // ---- D1: Açış — KAYBET ----
            if (trick.Cards.Count == 0)
            {
                var pick = GoalPicker.Pick(
                    Score(legal, c => GoalPicker.LoseNow(c, hand, trick, trump, mem, seat)),
                    tier, rng, out double p, out string bn);
                reason = $"D1: kaybetmek için açıyorum (%{p * 100:F0} almaz, {bn})";
                return pick;
            }

            var ledSuit = trick.Cards[0].Suit;
            bool WinsNow(Card c)
            {
                var t = new List<Card>(trick.Cards) { c };
                return GameRules.TrickWinnerOffset(t, ledSuit, trump) == t.Count - 1;
            }
            var losing = legal.Where(c => !WinsNow(c)).ToList();
            bool followingSuit = legal[0].Suit == ledSuit && hand.Any(h => h.Suit == ledSuit);
            bool forcedTrump = trump.HasValue && !hand.Any(h => h.Suit == ledSuit)
                                              && legal.All(c => c.Suit == trump.Value);

            Func<Card, double> danger = c => GoalPicker.FutureDanger(c, hand, trump, mem);

            if (followingSuit)
            {
                if (losing.Count > 0)
                {
                    var pick = GoalPicker.Pick(Score(losing, danger), tier, rng, out double p, out string bn);
                    reason = $"D2: altına kaçış — tehlikeliyi eritiyorum (%{p * 100:F0} ileride alırdı, {bn})";
                    return pick;
                }
                var burn = GoalPicker.Pick(Score(legal, danger), tier, rng, out double p2, out string bn2);
                reason = $"D3: mecburen alıyorum — en tehlikeliyi yakıyorum (%{p2 * 100:F0}, {bn2})";
                return burn;
            }

            if (forcedTrump)
            {
                if (losing.Count > 0)
                {
                    var pick = GoalPicker.Pick(Score(losing, danger), tier, rng, out double p, out string bn);
                    reason = $"D4a: koz mecburi, alttayım — tehlikeli kozu eritiyorum (%{p * 100:F0}, {bn})";
                    return pick;
                }
                var big = GoalPicker.Pick(Score(legal, danger), tier, rng, out double p3, out string bn3);
                reason = $"D4b: koz mecburi ve alıyorum — tehlikeliyi yakıyorum (%{p3 * 100:F0}, {bn3})";
                return big;
            }

            var dump = GoalPicker.Pick(Score(legal, danger), tier, rng, out double p4, out string bn4);
            reason = $"D5: serbest atış — en tehlikeli yük (%{p4 * 100:F0} ileride alırdı, {bn4})";
            return dump;
        }
    }
}
