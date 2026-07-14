using System.Collections.Generic;
using System.Threading.Tasks;

namespace Bien.Core
{
    /// <summary>İnsan, AI ve ileride network oyuncusu aynı arayüzü uygular.
    /// Async: insan oyuncu UI'dan, AI anında (Task.FromResult), network beklemeli döner.</summary>
    public interface IPlayerAgent
    {
        /// <param name="forbidden">Zorunlu yeniden ihalede yasak değer; ilk soruşta null (arzu serbest).</param>
        Task<int> MakeBidAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                               IReadOnlyList<int?> bidsSoFar, int? forbidden);

        /// <summary>Dağıtıcının arzusu yasak; bozmadan önce sorulur. Yeni ihale = revizyon, null = "değiştirmiyorum".</summary>
        Task<int?> OfferBidRevisionAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                         IReadOnlyList<int?> currentBids, int dealerDesiredBid);

        Task<Card> PlayCardAsync(int seat, IReadOnlyList<Card> hand, TrickState trick, RoundConfig round, Suit? trump);
    }

    public sealed class TrickState
    {
        public readonly List<Card> Cards = new(4);
        public int LeaderSeat;
        public Suit? LedSuit => Cards.Count > 0 ? Cards[0].Suit : null;
    }
}
