using System.Collections.Generic;
using UnityEngine;

namespace Bien.Unity
{
    /// <summary>Resources/Cards altından sprite yükler. İsim: {suit}_{rank:D2}, ör. spades_14.</summary>
    public static class CardSprites
    {
        private static readonly Dictionary<string, Sprite> _cache = new();
        private static Sprite _back;

        public static Sprite Get(Bien.Core.Card c)
        {
            string key = $"{c.Suit.ToString().ToLower()}_{(int)c.Rank:D2}";
            if (!_cache.TryGetValue(key, out var s))
            {
                s = Resources.Load<Sprite>($"Cards/{key}");
                if (s == null) Debug.LogError($"Kart sprite bulunamadı: Cards/{key}");
                _cache[key] = s;
            }
            return s;
        }

        public static Sprite Back
        {
            get
            {
                if (_back == null) _back = Resources.Load<Sprite>("Cards/back");
                return _back;
            }
        }
    }
}
