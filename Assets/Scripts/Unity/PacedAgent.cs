using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bien.Core;
using Bien.Core.AI;

namespace Bien.Unity
{
    /// <summary>Ajanı sarmalar: her aksiyondan önce controller'ın pacing kapısını bekler,
    /// AI ise ek düşünme gecikmesi ekler (UX). İnsan için gecikme 0.
    /// Gözlemci olaylarını içerideki ajana iletir (AI hafızası beslenmeli).</summary>
    public class PacedAgent : IPlayerAgent, IGameObserver
    {
        private readonly IPlayerAgent _inner;
        private readonly IGameObserver _innerObs;
        private readonly Func<Task> _gate;
        private readonly int _thinkMs;

        public PacedAgent(IPlayerAgent inner, Func<Task> gate, int thinkMs)
        { _inner = inner; _innerObs = inner as IGameObserver; _gate = gate; _thinkMs = thinkMs; }

        public void OnRoundStarted(RoundConfig rc, int d) => _innerObs?.OnRoundStarted(rc, d);
        public void OnTrumpRevealed(Card? tc) => _innerObs?.OnTrumpRevealed(tc);
        public void OnBidMade(int s, int b) => _innerObs?.OnBidMade(s, b);
        public void OnCardPlayed(int s, Card c) => _innerObs?.OnCardPlayed(s, c);
        public void OnTrickWon(int w) => _innerObs?.OnTrickWon(w);

        public async Task<int> MakeBidAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                            IReadOnlyList<int?> bidsSoFar, int? forbidden)
        {
            await _gate();
            if (_thinkMs > 0) await Task.Delay(_thinkMs);
            return await _inner.MakeBidAsync(seat, hand, round, trump, bidsSoFar, forbidden);
        }

        public async Task<int?> OfferBidRevisionAsync(int seat, IReadOnlyList<Card> hand, RoundConfig round, Suit? trump,
                                                      IReadOnlyList<int?> currentBids, int dealerDesiredBid)
        {
            await _gate();
            if (_thinkMs > 0) await Task.Delay(_thinkMs);
            return await _inner.OfferBidRevisionAsync(seat, hand, round, trump, currentBids, dealerDesiredBid);
        }

        public async Task<Card> PlayCardAsync(int seat, IReadOnlyList<Card> hand, TrickState trick, RoundConfig round, Suit? trump)
        {
            await _gate();
            if (_thinkMs > 0) await Task.Delay(_thinkMs);
            return await _inner.PlayCardAsync(seat, hand, trick, round, trump);
        }
    }
}
