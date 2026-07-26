using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    public enum SkillTier { Easy, Normal, Hard }

    /// <summary>
    /// AMAÇ-OLASILIK SEÇİCİ (Tulga şeması):
    /// Kitap kuralı adayları ve AMACI tanımlar; her aday amaca hizmet olasılığıyla puanlanır,
    /// yüksekten düşüğe sıralanır ve zorluk bandından seçilir:
    ///   Hard   → daima 1. (en iyi)
    ///   Normal → üst üçte bir (1. dahil) içinden rastgele
    ///   Easy   → ORTA üçte bir içinden rastgele (en kötüyü o bile oynamaz)
    /// Olasılıklar hafızadan (kart sayımı) beslendiği için dikkat farkı kararlara
    /// kendiliğinden sızar: Easy'nin sayımı bulanık → sıralaması da yanlış.
    /// </summary>
    public static class GoalPicker
    {
        public static Card Pick(List<(Card card, double p)> scored, SkillTier tier, Random rng,
                                out double chosenP, out string bandNote)
        {
            scored.Sort((a, b) => b.p.CompareTo(a.p));
            int n = scored.Count;
            int lo, hi; // dahil aralık (0 tabanlı)
            switch (tier)
            {
                case SkillTier.Hard: lo = 0; hi = 0; break;
                case SkillTier.Normal:
                    lo = 0; hi = Math.Max(0, (n + 2) / 3 - 1); break;   // üst 1/3, 1. dahil
                default:
                    lo = n / 3; hi = Math.Max(lo, Math.Min(n - 1, (2 * n + 2) / 3 - 1)); break; // orta 1/3
            }
            int idx = lo + (hi > lo ? rng.Next(hi - lo + 1) : 0);
            chosenP = scored[idx].p;
            bandNote = n > 1 ? $"{idx + 1}./{n} aday" : "tek aday";
            return scored[idx].card;
        }

        // ---------------- AMAÇ FONKSİYONLARI (hafıza enjeksiyonu) ----------------

        /// <summary>AL amacı: bu kart bu eli kazanır olasılığı (Prob motoru).</summary>
        public static double WinNow(Card c, IReadOnlyList<Card> hand, TrickState trick,
                                    Suit? trump, GameMemory mem, int seat)
            => Prob.WinProbability(c, hand, trick, trump, mem, seat);

        /// <summary>KAYBET amacı: bu kart bu eli ALMAZ olasılığı.</summary>
        public static double LoseNow(Card c, IReadOnlyList<Card> hand, TrickState trick,
                                     Suit? trump, GameMemory mem, int seat)
            => 1.0 - Prob.WinProbability(c, hand, trick, trump, mem, seat);

        /// <summary>
        /// HARCAMA SIRASI amacı (Winner'lar için): elde TUTULURSA Winner'lığını kaybetme riski.
        /// Yüksek risk = önce harcanmalı. Koz/sans boss'u dayanıklı (bekler), yan boss'u eriyendir,
        /// boss olmayan Winner en kırılgandır.
        /// </summary>
        public static double DecayRisk(Card c, IReadOnlyList<Card> hand, Suit? trump, GameMemory mem)
        {
            bool durable = !trump.HasValue || c.Suit == trump.Value;
            if (mem.IsBoss(c, hand))
            {
                if (durable) return 0.05;                       // koz/sans boss: bekleyebilir
                for (int s = 0; s < 4; s++)
                    if (mem.IsVoid(s, c.Suit)) return 0.80;     // yan boss + bilinen boşluk: hızla erir
                return 0.50;                                    // yan boss: erir
            }
            return durable ? 0.30 : 0.65;                       // boss değil: kırılgan
        }

        /// <summary>
        /// ERİT amacı: bu kartın GELECEKTE istemsiz el alma tehlikesi (hafızadan).
        /// Yüksek tehlike = önce eritilmeli. Sayım bilgisi: boss'luk, kalan üst kart sayısı,
        /// koz mecburiyeti, rakip boşlukları (boş rakip + canlı koz → yan kart çakılır, tehlikesi düşer).
        /// </summary>
        public static double FutureDanger(Card c, IReadOnlyList<Card> hand, Suit? trump, GameMemory mem)
        {
            bool isTrump = trump.HasValue && c.Suit == trump.Value;

            if (mem.IsBoss(c, hand))
            {
                if (isTrump || !trump.HasValue) return 0.97 + (int)c.Rank * 0.001; // sökülmez
                // Yan boss: çakılabilirse tehlike düşer
                for (int s = 0; s < 4; s++)
                    if (mem.IsVoid(s, c.Suit)) return 0.55 + (int)c.Rank * 0.001;
                return 0.88 + (int)c.Rank * 0.001;
            }

            // Boss değil: görülmemiş üst kart azaldıkça tehlike artar
            int higherUnseen = 0;
            foreach (var u in mem.UnseenCards(hand))
                if (u.Suit == c.Suit && u.Rank > c.Rank) higherUnseen++;
            double d = Math.Clamp(0.85 - higherUnseen * 0.16, 0.03, 0.85);

            if (isTrump) d = Math.Min(0.96, d + 0.28); // koz mecburiyeti: istemsiz kazanma kanalı
            else
            {
                for (int s = 0; s < 4; s++)
                    if (mem.IsVoid(s, c.Suit)) { d *= 0.6; break; } // çakılma ihtimali tehlikeyi kırpar
            }
            return d;
        }
    }
}
