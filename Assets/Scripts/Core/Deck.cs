using System;
using System.Collections.Generic;

namespace Bien.Core
{
    public sealed class Deck
    {
        private readonly List<Card> _cards = new(52);
        public int Count => _cards.Count;

        public Deck()
        {
            for (int s = 0; s < 4; s++)
                for (int r = 2; r <= 14; r++)
                    _cards.Add(new Card((Suit)s, (Rank)r));
        }

        /// <summary>Fisher-Yates. Seed verilirse deterministik — test ve replay için.</summary>
        public void Shuffle(Random rng)
        {
            for (int i = _cards.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
            }
        }

        public Card Draw()
        {
            if (_cards.Count == 0) throw new InvalidOperationException("Deste boş.");
            var c = _cards[^1];
            _cards.RemoveAt(_cards.Count - 1);
            return c;
        }
    }
}
