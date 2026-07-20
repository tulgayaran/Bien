using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// DUCKING KURAL KİTABI — yaşayan liste. Test sürüşlerinde bulunan yeni vakalar
    /// numaralı kural olarak eklenir; her karar logda kural numarasıyla görünür.
    ///
    /// v1 (2026-07):
    ///  D1  Açış: en düşük tehlike puanlı kartla aç.
    ///  D2  Takip, altına kaçış mümkün: altta kalanların EN BÜYÜĞÜNÜ ver (yükü ucuza erit).
    ///  D3  Takip, mecburen üstteyim: en büyüğü yak (madem alıyorum, bombayı öldür).
    ///  D4a Koz mecburi, masada beni geçen koz var: altta kalan kozların en büyüğünü ver.
    ///  D4b Koz mecburi, mecburen alıyorum: BÜYÜĞÜ yak (ileride bir el daha almaya mahkûmdu — D3 tutarlı).
    ///  D5  Serbest atış (renk+koz yok): en tehlikeli kartı boşalt.
    ///  D6  Eş-tehlike kırılımı: kısa renkten olanı at (renk boşalt → serbest atış hakkı kazan).
    /// </summary>
    public static class DuckingBook
    {
        public static Card Decide(IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "D0: tek geçerli kart"; return legal[0]; }

            // Tehlike: istemsiz kazanma potansiyeli. Boss ve koz ağır basar.
            double Danger(Card c)
            {
                double d = (int)c.Rank / 14.0;
                if (mem.IsBoss(c, hand)) d += 0.60;
                if (trump.HasValue && c.Suit == trump.Value) d += 0.35;
                return d;
            }
            int SuitLen(Card c) => hand.Count(h => h.Suit == c.Suit);

            // D6 kırılımı gömülü sıralayıcılar
            Card MostDangerous(IEnumerable<Card> set) =>
                set.OrderByDescending(Danger).ThenBy(SuitLen).ThenByDescending(c => c.Rank).First();
            Card LeastDangerous(IEnumerable<Card> set) =>
                set.OrderBy(Danger).ThenBy(SuitLen).ThenBy(c => c.Rank).First();

            // ---- D1: Açış ----
            if (trick.Cards.Count == 0)
            {
                var pick = LeastDangerous(legal);
                reason = $"D1: en tehlikesiz kartla açış";
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

            if (followingSuit)
            {
                // ---- D2: altına kaçış mümkün ----
                if (losing.Count > 0)
                {
                    var pick = losing.OrderByDescending(c => c.Rank).First();
                    reason = $"D2: altına kaçış — altta kalanların en büyüğünü eritiyorum";
                    return pick;
                }
                // ---- D3: mecburen üstteyim ----
                var burn = legal.OrderByDescending(c => c.Rank).First();
                reason = $"D3: mecburen alıyorum, en büyüğü yakıyorum";
                return burn;
            }

            if (forcedTrump)
            {
                // ---- D4a: masada beni geçen koz var ----
                if (losing.Count > 0)
                {
                    var pick = losing.OrderByDescending(c => c.Rank).First();
                    reason = $"D4a: koz mecburi ama altta kalıyorum — büyük kozu eritiyorum";
                    return pick;
                }
                // ---- D4b: mecburen alıyorum → en tehlikeli büyüğü yak (D3 ile tutarlı:
                // büyük koz ileride bir el daha almaya mahkûmdu, madem alıyorum onu öldüreyim) ----
                var big = legal.OrderByDescending(c => c.Rank).First();
                reason = "D4b: koz mecburi ve alıyorum — büyüğü yakıyorum, tehlike azalsın";
                return big;
            }

            // ---- D5 (+D6 kırılımı): serbest atış ----
            var dump = MostDangerous(legal);
            reason = $"D5: serbest atış — en tehlikeli yükü boşaltıyorum" +
                     (legal.Count(c => Math.Abs(Danger(c) - Danger(dump)) < 0.05) > 1 ? " (D6: kısa renk önce)" : "");
            return dump;
        }
    }
}