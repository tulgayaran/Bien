using System;
using System.Collections.Generic;
using System.Linq;

namespace Bien.Core.AI
{
    public enum CardRole { Winner, Swing, Loser }
    public enum PlayerStance { Hunting, Balanced, Ducking }

    /// <summary>
    /// Bir turluk oyun planı. İhale anında kurulur, tur boyunca revize edilir.
    /// Rol eşikleri (Tulga): puan ≥ 0.8 → Winner, 0.5–0.8 → Swing, altı → Loser.
    /// İhale = W + S/2 (yuvarlanır). Mod: W ≥ hedef → Balanced, W < hedef → Hunting, hedef 0 → Ducking.
    /// </summary>
    public sealed class HandPlan
    {
        public readonly Dictionary<Card, CardRole> Roles = new();
        public PlayerStance Stance;
        public int TargetTricks;      // konuşulan ihale (mizaç dahil)
        public double RawBid;         // W + S/2 ham değeri

        public int Winners => Roles.Count(kv => kv.Value == CardRole.Winner);
        public int Swings => Roles.Count(kv => kv.Value == CardRole.Swing);
        public int Losers => Roles.Count(kv => kv.Value == CardRole.Loser);

        public static HandPlan Build(IReadOnlyList<Card> hand, Suit? trump, int roundSize)
        {
            var plan = new HandPlan();
            foreach (var c in hand)
            {
                double p = TableAgent.CardPoints(c, trump, roundSize);
                plan.Roles[c] = p >= 0.8 ? CardRole.Winner
                              : p >= 0.5 ? CardRole.Swing
                              : CardRole.Loser;
            }
            plan.RawBid = plan.Winners + plan.Swings * 0.5;
            return plan;
        }

        /// <summary>Konuşulan ihale kesinleşince hedef belirlenir. Gerçek mod, tüm ihaleler
        /// bilinince ilk Rebalance'ta hesaplanır (masa dengesi o zaman belli olur).</summary>
        public void Commit(int spokenBid)
        {
            TargetTricks = spokenBid;
            Stance = PlayerStance.Balanced;
        }

        public CardRole RoleOf(Card c) => Roles.TryGetValue(c, out var r) ? r : CardRole.Loser;

        public void SetRole(Card c, CardRole r) => Roles[c] = r;

        public void Remove(Card c) => Roles.Remove(c);

        /// <summary>
        /// Her el sonrası yeniden dengeleme (Tulga kuralı): kalan elde W + S/2 ≈ kalan ihtiyaç
        /// sağlanana kadar terfi/tenzil. Güçler hafızayla tazelenir (boss olan Winner-sınıfı sayılır —
        /// dikkat düşükse boss kaçar, plan bayat kalır). İhtiyaç ≤ 0 → Ducking, herkes Loser.
        /// Dönen liste: log için değişim satırları.
        /// </summary>
        public List<string> Rebalance(IReadOnlyList<Card> handRemaining, Suit? trump, int roundSize,
                                      int tricksTaken, GameMemory mem, int tableSurplus)
        {
            var changes = new List<string>();
            int need = TargetTricks - tricksTaken;
            var oldStance = Stance;

            // Oynanmışları düş, yeni gelenleri (olmamalı ama) Loser başlat
            var stale = Roles.Keys.Where(c => !handRemaining.Contains(c)).ToList();
            foreach (var c in stale) Roles.Remove(c);
            foreach (var c in handRemaining) if (!Roles.ContainsKey(c)) Roles[c] = CardRole.Loser;

            double Strength(Card c)
            {
                // Boss (saydıklarıma göre üstü kalmamış) kart Winner-sınıfı güçtedir;
                // kozlu turda yan renk boss'u çakılabilir → hafif kırpma.
                if (mem != null && mem.IsBoss(c, handRemaining))
                {
                    bool ruffable = trump.HasValue && c.Suit != trump.Value;
                    return (ruffable ? 0.82 : 0.95) + (int)c.Rank * 0.001;
                }
                return TableAgent.CardPoints(c, trump, roundSize);
            }

            if (need <= 0)
            {
                foreach (var c in handRemaining)
                    if (Roles[c] != CardRole.Loser)
                    { changes.Add($"{c}: {Roles[c]}→Loser (ihtiyaç bitti, her kart yük)"); Roles[c] = CardRole.Loser; }
                int fw = trump.HasValue
                    ? handRemaining.Count(c => c.Suit == trump.Value && mem != null && mem.IsBoss(c, handRemaining))
                    : 0;
                var (st, whyRule) = TransitionBook.Evaluate(need, 0, fw, tableSurplus);
                Stance = st;
                if (oldStance != Stance) changes.Add($"mod: {oldStance}→{Stance} [{whyRule}]");
                return changes;
            }

            // 1) Güçlere göre taze sınıflandırma
            foreach (var c in handRemaining)
            {
                double s = Strength(c);
                var fresh = s >= 0.8 ? CardRole.Winner : s >= 0.5 ? CardRole.Swing : CardRole.Loser;
                if (fresh != Roles[c])
                {
                    string why = s >= 0.8 && TableAgent.CardPoints(c, trump, roundSize) < 0.8
                        ? "boss oldu" : "güç güncellendi";
                    changes.Add($"{c}: {Roles[c]}→{fresh} ({why})");
                    Roles[c] = fresh;
                }
            }

            // 2) Denge döngüsü: W + S/2 ≈ need
            int guard = 0;
            while (guard++ < 26)
            {
                double f = Winners + Swings * 0.5;
                if (Math.Abs(f - need) <= 0.25) break;

                if (f > need)
                {
                    // Fazla ateş gücü: en zayıf Winner → Swing; Winner yoksa en zayıf Swing → Loser
                    var w = Roles.Where(kv => kv.Value == CardRole.Winner)
                                 .OrderBy(kv => Strength(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                    if (w != default)
                    { changes.Add($"{w}: Winner→Swing (formül fazlası: {f:F1} > ihtiyaç {need})"); Roles[w] = CardRole.Swing; continue; }
                    var sw = Roles.Where(kv => kv.Value == CardRole.Swing)
                                  .OrderBy(kv => Strength(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                    if (sw != default)
                    { changes.Add($"{sw}: Swing→Loser (formül fazlası)"); Roles[sw] = CardRole.Loser; continue; }
                    break;
                }
                else
                {
                    // Açık var: en güçlü Swing → Winner; yoksa en güçlü Loser → Swing
                    var sw = Roles.Where(kv => kv.Value == CardRole.Swing)
                                  .OrderByDescending(kv => Strength(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                    if (sw != default)
                    { changes.Add($"{sw}: Swing→Winner (açık: {f:F1} < ihtiyaç {need})"); Roles[sw] = CardRole.Winner; continue; }
                    var lo = Roles.Where(kv => kv.Value == CardRole.Loser)
                                  .OrderByDescending(kv => Strength(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
                    if (lo != default)
                    { changes.Add($"{lo}: Loser→Swing (açık — zoraki terfi)"); Roles[lo] = CardRole.Swing; continue; }
                    break;
                }
            }

            // Mod geçişi: TransitionBook (rol dengelemesinden artakalan uyumsuzluğu mod emer)
            int forcedWinners = trump.HasValue
                ? handRemaining.Count(c => c.Suit == trump.Value && mem != null && mem.IsBoss(c, handRemaining))
                : 0;
            var (newStance, rule) = TransitionBook.Evaluate(need, Winners + Swings * 0.5, forcedWinners, tableSurplus);
            Stance = newStance;
            if (oldStance != Stance)
                changes.Add($"mod: {oldStance}→{Stance} [{rule}]");
            return changes;
        }

        public string Describe()
        {
            string List(CardRole r) => string.Join(" ", Roles.Where(kv => kv.Value == r)
                                                             .Select(kv => kv.Key.ToString()));
            return $"W{Winners} S{Swings} L{Losers} | W:[{List(CardRole.Winner)}] S:[{List(CardRole.Swing)}] L:[{List(CardRole.Loser)}]";
        }
    }
}