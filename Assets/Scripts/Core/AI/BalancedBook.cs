using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    /// <summary>
    /// BALANCED KURAL KİTABI — yaşayan liste. Ateş gücü ≈ ihtiyaç: plan ZAMANLAMAYLA icra edilir.
    ///
    /// v1 (2026-07):
    ///  B1   Açış: eriyen Winner (yan renk boss'u) önce bozdurulur — değer kaybı geri gelmez.
    ///  B2   Açış: Winner'lar dayanıklıysa kaybedilecek eli BAŞTAN kaybet — en riskli Loser'ı
    ///       ateşe sür (çakılırsa yük gitti; almazsa zaten alacaktı).
    ///  B3   Açış: elde yalnız dayanıklı Winner kaldıysa tepeden kapat.
    ///  B4   Takip: Winner işbaşında — güvenliyse (son söz / sökülmez) en ucuz kazanan.
    ///  B4b  Takip: Swing FIRSATI — bedava kazanıyorsa VE tenzil edilecek Winner varsa al
    ///       (formül: +0.5 fazla, W→S tenzili −0.5 → denklem kapanır). Winner yoksa DOKUNMA.
    ///  B5   Takip: almayacağım/alamayacağım el — altta kalanların en büyüğüyle kaç (Loser erit);
    ///       kaçış yoksa mecburen en ucuz kazanan.
    ///  B6   Koz mecburi: kazanıyorsam EN KÜÇÜK kazanan koz; üstüm varsa büyük altta-kalanı erit.
    ///  B7   Serbest atış: en tehlikeli Loser; Loser yoksa en zayıf Swing.
    /// </summary>
    public static class BalancedBook
    {
        public static Card Decide(int seat, IReadOnlyList<Card> hand, TrickState trick, Suit? trump,
                                  GameMemory mem, HandPlan plan, Random rng, out string reason)
        {
            var legal = GameRules.LegalPlays(hand, trick.LedSuit, trump);
            if (legal.Count == 1) { reason = "B0: tek geçerli kart"; return legal[0]; }

            bool Durable(Card c) => mem.IsBoss(c, hand) && (!trump.HasValue || c.Suit == trump.Value);
            bool Decaying(Card c) => mem.IsBoss(c, hand) && trump.HasValue && c.Suit != trump.Value;

            double Danger(Card c)
            {
                double d = (int)c.Rank / 14.0;
                if (mem.IsBoss(c, hand)) d += 0.60;
                if (trump.HasValue && c.Suit == trump.Value) d += 0.35;
                return d;
            }

            // ---- Açış ----
            if (trick.Cards.Count == 0)
            {
                var melting = legal.Where(Decaying).ToList();
                if (melting.Count > 0)
                {
                    var pick = melting.OrderByDescending(c => c.Rank).First();
                    reason = "B1: eriyen Winner'ı çakılmadan bozduruyorum";
                    return pick;
                }
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                if (losers.Count > 0)
                {
                    var pick = losers.OrderByDescending(Danger).First();
                    reason = "B2: planlı kayıp — en riskli Loser'ı ateşe sürüyorum";
                    return pick;
                }
                if (legal.All(Durable))
                {
                    var pick = legal.OrderByDescending(c => c.Rank).First();
                    reason = "B3: elde yalnız dayanıklı Winner — tepeden kapatıyorum";
                    return pick;
                }
                var probe = legal.OrderBy(c => c.Rank).First();
                reason = "B2b: Swing'lerle bekliyorum — en küçükle yokluyorum";
                return probe;
            }

            var ledSuit = trick.Cards[0].Suit;
            int playersAfter = 3 - trick.Cards.Count;
            bool WinsNow(Card c)
            {
                var t = new List<Card>(trick.Cards) { c };
                return GameRules.TrickWinnerOffset(t, ledSuit, trump) == t.Count - 1;
            }
            bool SafeWin(Card c) => playersAfter == 0 ||
                                    (mem.IsBoss(c, hand) &&
                                     (!trump.HasValue || c.Suit == trump.Value ||
                                      !Enumerable.Range(0, 4).Any(s => mem.IsVoid(s, ledSuit))));

            var winning = legal.Where(WinsNow).ToList();
            var losing = legal.Where(c => !WinsNow(c)).ToList();

            bool forcedTrump = trump.HasValue && !hand.Any(h => h.Suit == ledSuit)
                                              && legal.All(c => c.Suit == trump.Value);
            bool freeDiscard = !forcedTrump && legal[0].Suit != ledSuit;

            if (forcedTrump)
            {
                if (winning.Count > 0)
                {
                    var pick = winning.OrderBy(c => c.Rank).First();
                    reason = "B6: koz mecburi ve alıyorum — en küçük kazanan kozla";
                    return pick;
                }
                var melt = legal.OrderByDescending(c => c.Rank).First();
                reason = "B6: koz mecburi, üstüm var — büyük kozu eritiyorum";
                return melt;
            }

            if (freeDiscard)
            {
                var losers = legal.Where(c => plan.RoleOf(c) == CardRole.Loser).ToList();
                if (losers.Count > 0)
                {
                    var pick = losers.OrderByDescending(Danger).First();
                    reason = "B7: serbest atış — en tehlikeli Loser'ı boşaltıyorum";
                    return pick;
                }
                var weak = legal.OrderBy(c => TableAgent.CardPoints(c, trump, mem.Round.CardsPerPlayer)).First();
                reason = "B7: Loser kalmadı — en zayıf Swing'i veriyorum";
                return weak;
            }

            // ---- Renk takibi ----
            int need = plan.TargetTricks - mem.TricksWon[seat];
            bool swingsAreWorking = plan.Winners < need; // ihtiyaç Swing'lere yaslanıyor:
                                                         // Swing kazançları fırsat değil PLANIN KENDİSİ
            if (winning.Count > 0)
            {
                // B4: planlı işçiler (Winner'lar + ihtiyaç yaslanıyorsa Swing'ler) güvenli kazanç
                var working = winning.Where(c => SafeWin(c) &&
                        (plan.RoleOf(c) == CardRole.Winner ||
                         (swingsAreWorking && plan.RoleOf(c) == CardRole.Swing))).ToList();
                if (working.Count > 0)
                {
                    var pick = working.OrderBy(c => c.Rank).First();
                    reason = "B4: planlı kart işbaşında — en ucuz güvenli kazanan";
                    return pick;
                }

                // B4b: Swing FIRSATI (W ≥ ihtiyaçken bedava el) — tenzil edilecek Winner şart
                var swingFree = winning.Where(c => plan.RoleOf(c) == CardRole.Swing && SafeWin(c)).ToList();
                if (!swingsAreWorking && swingFree.Count > 0 && plan.Winners >= 1)
                {
                    var pick = swingFree.OrderBy(c => c.Rank).First();
                    reason = "B4b: bedava Swing fırsatı — alıyorum, bir Winner tenzil edilecek";
                    return pick;
                }

                // B4c: güvenli kazanan yok ama el plana lazım — Loser olmayan en büyükle bastır
                var press = winning.Where(c => plan.RoleOf(c) != CardRole.Loser)
                                   .OrderByDescending(c => c.Rank).ToList();
                if (need > 0 && press.Count > 0 && playersAfter > 0)
                {
                    reason = $"B4c: garanti yok ama el lazım — en büyükle bastırıyorum (arkada {playersAfter})";
                    return press.First();
                }
            }

            // B5: kaç — altta kalanların en büyüğü; kaçış yoksa mecburen en ucuz kazanan
            if (losing.Count > 0)
            {
                var pick = losing.OrderByDescending(c => c.Rank).First();
                reason = "B5: bu eli plana almıyorum — altta kalanların en büyüğüyle kaçıyorum";
                return pick;
            }
            var forced = winning.OrderBy(c => c.Rank).First();
            reason = "B5b: kaçış yok — mecburen en ucuz kazananla alıyorum";
            return forced;
        }
    }
}