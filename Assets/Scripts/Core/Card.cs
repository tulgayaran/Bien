using System;

namespace Bien.Core
{
    public enum Suit { Spades = 0, Hearts = 1, Diamonds = 2, Clubs = 3 }

    /// <summary>2..14, As = 14 (en büyük).</summary>
    public enum Rank
    {
        Two = 2, Three = 3, Four = 4, Five = 5, Six = 6, Seven = 7, Eight = 8,
        Nine = 9, Ten = 10, Jack = 11, Queen = 12, King = 13, Ace = 14
    }

    public readonly struct Card : IEquatable<Card>
    {
        public readonly Suit Suit;
        public readonly Rank Rank;

        public Card(Suit suit, Rank rank) { Suit = suit; Rank = rank; }

        public bool Equals(Card other) => Suit == other.Suit && Rank == other.Rank;
        public override bool Equals(object obj) => obj is Card c && Equals(c);
        public override int GetHashCode() => ((int)Suit << 4) | (int)Rank;
        public static bool operator ==(Card a, Card b) => a.Equals(b);
        public static bool operator !=(Card a, Card b) => !a.Equals(b);

        public override string ToString()
        {
            string s = Suit switch
            {
                Suit.Spades => "♠", Suit.Hearts => "♥",
                Suit.Diamonds => "♦", _ => "♣"
            };
            string r = (int)Rank <= 10 ? ((int)Rank).ToString()
                     : Rank switch { Rank.Jack => "J", Rank.Queen => "Q", Rank.King => "K", _ => "A" };
            return s + r;
        }
    }
}
