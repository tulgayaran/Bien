using System;

namespace Bien.Core.AI
{
    /// <summary>
    /// MOD GEÇİŞ KURAL KİTABI — yaşayan liste. Her el sonrası (rol dengelemesinden SONRA)
    /// değerlendirilir. Rol etiketleri gerçeği her zaman ememez (boss koz "Loser" yazılsa da alır);
    /// artakalan uyumsuzluğu MOD emer.
    ///
    /// v1 (2026-07):
    ///  T1  İhtiyaç bitti, sökülmez garanti yok → Ducking (temiz kaçış).
    ///  T2  İhtiyaç bitti AMA elde sökülmez garanti var (boss koz) → yine Ducking,
    ///      log'a batış riski uyarısı (o kartlar istemsiz alacak — kaçınılmazı yönet).
    ///  T3  Ateş gücü ihtiyacı 1+ aşıyor (f ≥ ihtiyaç+1) → Balanced (temkin; tenziller yetmedi).
    ///  T4  Plan hedefte (|f − ihtiyaç| < 1) → Balanced.
    ///  T5  Açık büyük (f ≤ ihtiyaç−1) → Hunting (Swing'ler zorlanacak).
    ///  T6  El kıtlığı (masa −3'ten kötü) Balanced'ı bir kademe agresifleştirir → Hunting.
    ///  T7  El bolluğu (masa +3'ten bol) Hunting'i bir kademe sakinleştirir → Balanced.
    /// </summary>
    public static class TransitionBook
    {
        public static (PlayerStance stance, string rule) Evaluate(
            int need, double firepower, int forcedWinners, int tableSurplus)
        {
            PlayerStance s; string r;

            if (need <= 0)
            {
                s = PlayerStance.Ducking;
                r = forcedWinners > 0
                    ? $"T2: hedef tamam ama {forcedWinners} sökülmez garanti elde — batış riski, kaçınılmazı yönet"
                    : "T1: hedef tamam → temiz kaçış";
            }
            else if (firepower >= need + 1)
            {
                s = PlayerStance.Balanced;
                r = $"T3: ateş gücü fazla (f={firepower:F1}, ihtiyaç {need}) → temkin";
            }
            else if (firepower <= need - 1)
            {
                s = PlayerStance.Hunting;
                r = $"T5: açık büyük (f={firepower:F1}, ihtiyaç {need}) → av modu";
            }
            else
            {
                s = PlayerStance.Balanced;
                r = $"T4: plan hedefte (f={firepower:F1} ≈ {need})";
            }

            // Masa dengesi kaydırmaları
            if (tableSurplus <= -3 && s == PlayerStance.Balanced && need > 0)
            { s = PlayerStance.Hunting; r += " | T6: el kıtlığı, bir kademe agresif"; }
            else if (tableSurplus >= 3 && s == PlayerStance.Hunting)
            { s = PlayerStance.Balanced; r += " | T7: el bolluğu, bir kademe sakin"; }

            return (s, r);
        }
    }
}
