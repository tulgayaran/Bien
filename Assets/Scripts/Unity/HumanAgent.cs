using System.Collections.Generic;
using System.Threading.Tasks;
using Bien.Core;

namespace Bien.Unity
{
    /// <summary>UI'dan beslenen insan oyuncu. Controller TCS'leri tamamlar.</summary>
    public class HumanAgent : IPlayerAgent
    {
        private TaskCompletionSource<int> _bidTcs;
        private TaskCompletionSource<int?> _revisionTcs;
        private TaskCompletionSource<Card> _cardTcs;

        // Controller bu callback'lerle UI'ı açar
        public System.Action<RoundConfig, int?, IReadOnlyList<int?>> OnBidRequested;      // round, forbidden, bids
        public System.Action<int, IReadOnlyList<int?>, RoundConfig> OnRevisionRequested;  // dealerDesired, bids, round
        public System.Action<IReadOnlyList<Card>, Suit?, Suit?> OnCardRequested;          // hand, ledSuit, trump

        public Task<int> MakeBidAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                      IReadOnlyList<int?> bidsSoFar, int? forbidden)
        {
            _bidTcs = new TaskCompletionSource<int>();
            OnBidRequested?.Invoke(round, forbidden, bidsSoFar);
            return _bidTcs.Task;
        }

        public Task<int?> OfferBidRevisionAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                                IReadOnlyList<int?> currentBids, int dealerDesiredBid)
        {
            _revisionTcs = new TaskCompletionSource<int?>();
            OnRevisionRequested?.Invoke(dealerDesiredBid, currentBids, round);
            return _revisionTcs.Task;
        }

        public Task<Card> PlayCardAsync(int seat, IReadOnlyList<Card> hand, TrickState trick, RoundConfig round, Suit? trump)
        {
            _cardTcs = new TaskCompletionSource<Card>();
            OnCardRequested?.Invoke(hand, trick.LedSuit, trump);
            return _cardTcs.Task;
        }

        public void SubmitBid(int bid) => _bidTcs?.TrySetResult(bid);
        public void SubmitRevision(int? bid) => _revisionTcs?.TrySetResult(bid);
        public void SubmitCard(Card card) => _cardTcs?.TrySetResult(card);
    }
}
